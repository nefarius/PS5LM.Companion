using System.IO.Compression;
using System.Text.Json;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// Brings a console that only runs Payload Manager to a working PS5LM host: upgrades the manager when asked,
/// uploads and autoloads the pinned payloads, launches them, places their extra files, allow-lists the app in
/// OnionHEN, installs PS5LM or PS5LM Plus for ShadowMountPlus, and finishes with a reachability report.
/// Every step is safe to repeat.
/// </summary>
public sealed class SetupRunner(SetupManifest manifest, SetupOptions options, SetupServices services)
{
    private const string OnionConfigPath = "/data/OnionHEN/config.ini";
    private const string ShadowMountLog = "/data/shadowmount/debug.log";
    private const string HomebrewRoot = "/data/homebrew";
    private const string StagingRoot = "/data/ps5lm-companion/staging";
    private const int MaxTextBytes = 4 * 1024 * 1024;

    private readonly List<ReportLine> _lines = [];
    private readonly Dictionary<string, string> _notes = new(StringComparer.OrdinalIgnoreCase);
    private ISetupUi Ui => services.Ui;

    public async Task<SetupReport> RunAsync(CancellationToken cancellationToken)
    {
        ManifestApp? app = null;
        if (!options.SkipApp)
        {
            app = manifest.Apps.FirstOrDefault(a => a.Id.Equals(options.AppId, StringComparison.OrdinalIgnoreCase));
            if (app is null)
            {
                Add("App", SetupStatus.Fail, $"Unknown app '{options.AppId}'. Choose {string.Join(" or ", manifest.Apps.Select(a => a.Id))}, or skip the app.");
                return new SetupReport(_lines);
            }
        }

        try
        {
            if (!await EnsurePayloadManagerAsync(cancellationToken))
            {
                return new SetupReport(_lines);
            }

            var resolved = await UploadPayloadsAsync(cancellationToken);
            await ConfigureAutoloadAsync(resolved, cancellationToken);
            await LaunchPayloadsAsync(resolved, cancellationToken);
            await PlaceExtraFilesAsync(resolved, cancellationToken);
            if (app is not null)
            {
                await AllowAppInOnionHenAsync(app, cancellationToken);
                var install = await InstallAppAsync(app, cancellationToken);
                if (install == AppInstall.Copied)
                {
                    if (await WaitForShadowMountAsync(app, cancellationToken))
                    {
                        await RefreshVoidShellLibraryAsync(app, cancellationToken);
                    }
                }
                else if (install == AppInstall.Unchanged)
                {
                    await CheckExistingRegistrationAsync(app, cancellationToken);
                }
            }

            await FinalCheckAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Add("Setup", SetupStatus.Fail, "A request to the console timed out. Steps reported above completed before it.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ConsoleUnreachableException or InvalidDataException or JsonException)
        {
            Add("Setup", SetupStatus.Fail, ex.Message);
        }

        return new SetupReport(_lines);
    }

    /// <summary>Downloads every artifact in the manifest and checks its hash. No console is involved.</summary>
    public static async Task<bool> VerifyManifestAsync(SetupManifest manifest, IArtifactStore store, ISetupUi ui, CancellationToken cancellationToken)
    {
        var ok = true;
        foreach (var (label, url, sha256) in manifest.Artifacts())
        {
            try
            {
                await store.FetchAsync(url, sha256, cancellationToken);
                ui.Info($"OK    {label}");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException)
            {
                ok = false;
                ui.Info($"FAIL  {label}: {ex.Message}");
            }
        }

        return ok;
    }

    // Step 1 (and 1a): Payload Manager must answer, and be 0.6.x or newer.
    private async Task<bool> EnsurePayloadManagerAsync(CancellationToken cancellationToken)
    {
        Ui.Info($"Checking Payload Manager on {options.Host}:{options.PayloadManagerPort}");
        var text = await services.Manager.GetVersionAsync(cancellationToken);
        if (text is null || !PayloadManagerVersion.TryParse(text, out var running))
        {
            Add("Payload Manager", SetupStatus.Fail, $"Not reachable on port {options.PayloadManagerPort}. Start Payload Manager from the console's autoloader and run setup again.");
            return false;
        }

        var wanted = manifest.PayloadManager;
        PayloadManagerVersion.TryParse(wanted.Version, out var target);
        PayloadManagerVersion.TryParse(wanted.MinimumVersion, out var minimum);
        if (running >= target)
        {
            Ui.Info($"Payload Manager {text} is current.");
            return true;
        }

        var required = running < minimum;
        var consent = options.UpgradePayloadManager
            || (Ui.CanPrompt && Ui.Confirm($"Payload Manager {text} is older than {wanted.Version}. Upgrade it now?"));
        if (!consent)
        {
            if (required)
            {
                Add("Payload Manager", SetupStatus.Fail, $"Version {text} is older than {wanted.MinimumVersion}, which setup needs. Run again with --upgrade-payload-manager.");
                return false;
            }

            Add("Payload Manager", SetupStatus.Warn, $"Version {text} is older than {wanted.Version}. Run with --upgrade-payload-manager to update it.");
            return true;
        }

        return await new PayloadManagerUpgrade(manifest, options, services, Add, Notify).RunAsync(target, cancellationToken);
    }

    // Steps 2 and 3: diff the manager's list against the manifest and upload what is missing.
    private async Task<List<(ManifestPayload Payload, ManagedPayload Stored)>> UploadPayloadsAsync(CancellationToken cancellationToken)
    {
        var inventory = await services.Manager.ListPayloadsAsync(cancellationToken);
        var missing = manifest.Payloads.Where(p => options.Force || FindStored(inventory, p) is null).ToList();
        Ui.Info(missing.Count == 0
            ? "All payloads are already stored in Payload Manager."
            : $"Missing in Payload Manager: {string.Join(", ", missing.Select(p => p.Name))}");
        foreach (var payload in missing)
        {
            Ui.Info($"Uploading {payload.Name} {payload.Version}");
            var path = await services.Artifacts.FetchAsync(payload.Url, payload.Sha256, cancellationToken);
            await using var file = File.OpenRead(path);
            await services.Manager.UploadAsync(payload.UploadName, file, cancellationToken);
        }

        if (missing.Count > 0)
        {
            inventory = await services.Manager.ListPayloadsAsync(cancellationToken);
        }

        var resolved = new List<(ManifestPayload, ManagedPayload)>();
        foreach (var payload in manifest.Payloads)
        {
            if (FindStored(inventory, payload) is { } stored)
            {
                resolved.Add((payload, stored));
            }
            else
            {
                _notes[payload.Id] = "Payload Manager does not list it after the upload.";
            }
        }

        return resolved;
    }

    // Step 4: autoload on, our payloads in order with pauses after the heavy ones.
    private async Task ConfigureAutoloadAsync(List<(ManifestPayload Payload, ManagedPayload Stored)> resolved, CancellationToken cancellationToken)
    {
        var current = await services.Manager.GetAutoloadAsync(cancellationToken);
        var list = AutoloadListBuilder.Build(resolved.Select(r => (r.Stored.LaunchTarget, r.Payload.DelayAfterMs)), current?.List);
        Ui.Info("Enabling autoload: " + list);
        await services.Manager.SetAutoloadAsync(true, list, cancellationToken);
        var check = await services.Manager.GetAutoloadAsync(cancellationToken);
        if (check is { Enabled: true } && check.List == list)
        {
            Add("Autoload", SetupStatus.Ok, "Enabled with " + resolved.Count + " payloads.");
        }
        else
        {
            Add("Autoload", SetupStatus.Warn, "Payload Manager did not report the saved autoload list back.");
        }
    }

    // Step 5: load in order, skipping what already runs.
    private async Task LaunchPayloadsAsync(List<(ManifestPayload Payload, ManagedPayload Stored)> resolved, CancellationToken cancellationToken)
    {
        foreach (var (payload, stored) in resolved)
        {
            if (await IsReadyAsync(payload, cancellationToken))
            {
                Ui.Info($"{payload.Name} is already running.");
                continue;
            }

            Ui.Info($"Launching {payload.Name}");
            var launch = await services.Manager.LoadAsync(stored.LaunchTarget, cancellationToken);
            var isElfldr = payload.Id.Equals("elfldr", StringComparison.OrdinalIgnoreCase);
            if (!launch.Accepted)
            {
                _notes[payload.Id] = isElfldr
                    ? PayloadDependencies.ElfldrBootstrap(options.ElfldrPort) + " (" + launch.Message + ")"
                    : launch.Message;
                if (isElfldr)
                {
                    Ui.Info("elfldr did not start; the other payloads cannot be launched without it.");
                    return;
                }

                continue;
            }

            var timeout = options.Wait(TimeSpan.FromSeconds(payload.ReadyTimeoutSeconds));
            if (!await WaitForAsync(ct => IsReadyAsync(payload, ct), timeout, cancellationToken))
            {
                _notes[payload.Id] = $"Launched, but not ready after {payload.ReadyTimeoutSeconds} s.";
                if (isElfldr)
                {
                    return;
                }

                continue;
            }

            if (payload.DelayAfterMs > 0)
            {
                Ui.Info($"Waiting {payload.DelayAfterMs / 1000.0:0.#} s for {payload.Name} to settle");
                await Task.Delay(options.Wait(TimeSpan.FromMilliseconds(payload.DelayAfterMs)), services.Time, cancellationToken);
            }
        }
    }

    // Step 5b: files that belong to a payload but travel over FTP.
    private async Task PlaceExtraFilesAsync(List<(ManifestPayload Payload, ManagedPayload Stored)> resolved, CancellationToken cancellationToken)
    {
        foreach (var payload in resolved.Select(r => r.Payload).Where(p => p.Files is { Count: > 0 }))
        {
            foreach (var file in payload.Files!)
            {
                var label = $"{payload.Name} file";
                if (!await WaitForAsync(ct => services.Probe.PortOpenAsync(options.FtpPort, ct), options.Wait(TimeSpan.FromSeconds(20)), cancellationToken))
                {
                    Add(label, SetupStatus.Fail, $"ftpsrv is not answering on port {options.FtpPort}, so {file.Target} was not placed.");
                    continue;
                }

                var path = await services.Artifacts.FetchAsync(file.Url, file.Sha256, cancellationToken);
                var size = new FileInfo(path).Length;
                var split = file.Target.LastIndexOf('/');
                var directory = file.Target[..split];
                var name = file.Target[(split + 1)..];
                var existing = (await services.Files.ListAsync(directory, cancellationToken)).FirstOrDefault(e => !e.IsDirectory && e.Name == name);
                if (!options.Force && existing?.Size == size)
                {
                    Add(label, SetupStatus.Ok, $"{file.Target} is already there.");
                    continue;
                }

                Ui.Info($"Uploading {file.Target}");
                await using (var stream = File.OpenRead(path))
                {
                    await services.Files.UploadAsync(stream, file.Target + ".part", null, cancellationToken);
                }

                await services.Files.MoveAsync(file.Target + ".part", file.Target, cancellationToken);
                Add(label, SetupStatus.Ok, $"Placed {file.Target}.");
            }
        }
    }

    // Step 6: let the app write /data/PS5LM when OnionHEN is the HEN.
    private async Task AllowAppInOnionHenAsync(ManifestApp app, CancellationToken cancellationToken)
    {
        if (!await services.Probe.PortOpenAsync(options.FtpPort, cancellationToken))
        {
            Add("OnionHEN allow-list", SetupStatus.Warn, $"ftpsrv is not answering, so {OnionConfigPath} was not checked.");
            return;
        }

        var text = await services.Files.ReadTextAsync(OnionConfigPath, MaxTextBytes, cancellationToken);
        if (text is null)
        {
            Add("OnionHEN allow-list", SetupStatus.Warn, $"{OnionConfigPath} does not exist yet. Run setup again once OnionHEN has started, or add {app.TitleId} to exact_title_ids in [app_jailbreak] yourself.");
            return;
        }

        var edit = OnionHenConfigEditor.AllowTitle(text, app.TitleId);
        switch (edit.Status)
        {
            case AllowListStatus.Added:
                await services.Files.WriteTextAsync(OnionConfigPath + ".bak", text, cancellationToken);
                await services.Files.WriteTextAsync(OnionConfigPath, edit.Text, cancellationToken);
                Add("OnionHEN allow-list", SetupStatus.Ok, $"Added {app.TitleId} to exact_title_ids (backup: config.ini.bak).");
                break;
            case AllowListStatus.AlreadyPresent:
                Add("OnionHEN allow-list", SetupStatus.Ok, $"{app.TitleId} is already allowed.");
                break;
            default:
                Add("OnionHEN allow-list", SetupStatus.Warn, $"Could not edit {OnionConfigPath} ({edit.Status}). Add {app.TitleId} to exact_title_ids in [app_jailbreak] by hand.");
                break;
        }
    }

    // Step 7: copy the app into the folder ShadowMountPlus scans.
    private async Task<AppInstall> InstallAppAsync(ManifestApp app, CancellationToken cancellationToken)
    {
        if (!await services.Probe.PortOpenAsync(options.FtpPort, cancellationToken))
        {
            Add(app.Name, SetupStatus.Fail, $"ftpsrv is not answering on port {options.FtpPort}, so the app cannot be installed.");
            return AppInstall.Failed;
        }

        var zipPath = await services.Artifacts.FetchAsync(app.Url, app.Sha256, cancellationToken);
        using var zip = ZipFile.OpenRead(zipPath);
        const string ParamSuffix = "sce_sys/param.json";
        var param = zip.Entries
            .Where(e => e.FullName.EndsWith(ParamSuffix, StringComparison.Ordinal) && e.FullName.Count(c => c == '/') <= 2)
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();
        if (param is null)
        {
            Add(app.Name, SetupStatus.Fail, "The download has no sce_sys/param.json.");
            return AppInstall.Failed;
        }

        var root = param.FullName[..^ParamSuffix.Length];
        var files = zip.Entries
            .Where(e => e.FullName.StartsWith(root, StringComparison.Ordinal) && !e.FullName.EndsWith('/'))
            .Select(e => (Entry: e, Relative: e.FullName[root.Length..]))
            .ToList();
        if (files.Any(f => f.Relative.Contains("..", StringComparison.Ordinal) || f.Relative.StartsWith('/')))
        {
            Add(app.Name, SetupStatus.Fail, "The download contains an unsafe path.");
            return AppInstall.Failed;
        }

        string newParam;
        using (var reader = new StreamReader(param.Open()))
        {
            newParam = await reader.ReadToEndAsync(cancellationToken);
        }

        var archiveTitleId = ReadTitleId(newParam);
        if (!string.Equals(archiveTitleId, app.TitleId, StringComparison.Ordinal))
        {
            Add(app.Name, SetupStatus.Fail, $"The download's param.json names title '{archiveTitleId ?? "(none)"}', expected {app.TitleId}. Nothing was installed.");
            return AppInstall.Failed;
        }

        var remote = $"{HomebrewRoot}/{app.TitleId}";
        var existingParam = await services.Files.ReadTextAsync(remote + "/" + ParamSuffix, MaxTextBytes, cancellationToken);
        if (existingParam is not null && existingParam.Trim() == newParam.Trim() && !options.Force)
        {
            Add(app.Name, SetupStatus.Ok, $"{app.Version} is already installed in {remote}.");
            return AppInstall.Unchanged;
        }

        var inPlace = existingParam is not null || await services.Files.DirectoryExistsAsync(remote, cancellationToken);
        var destination = inPlace ? remote : $"{StagingRoot}/{app.TitleId}";
        Ui.Info(inPlace
            ? $"Updating {app.Name} {app.Version} in place ({files.Count} files)"
            : $"Installing {app.Name} {app.Version} ({files.Count} files) through a staging folder");

        // Only log lines written after this point can belong to this install.
        _installLogBaseline = CountInstalledLines(await services.Files.ReadTextAsync(ShadowMountLog, MaxTextBytes, cancellationToken), app.TitleId);

        // param.json goes last so a half-copied folder is not mistaken for a complete title.
        foreach (var (entry, relative) in files.OrderBy(f => f.Relative == ParamSuffix))
        {
            await using var stream = entry.Open();
            await services.Files.UploadAsync(stream, $"{destination}/{relative}", null, cancellationToken);
        }

        if (!inPlace)
        {
            await services.Files.MoveDirectoryAsync(destination, remote, cancellationToken);
        }

        Add(app.Name, SetupStatus.Ok, $"Installed {app.Version} to {remote}.");
        return AppInstall.Copied;
    }

    private static string? ReadTitleId(string paramJson)
    {
        try
        {
            using var document = JsonDocument.Parse(paramJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in new[] { "titleId", "title_id" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    // Step 8: ShadowMountPlus scans on a timer; nothing can force it, so wait and look.
    private async Task<bool> WaitForShadowMountAsync(ManifestApp app, CancellationToken cancellationToken)
    {
        Ui.Info($"Waiting for ShadowMountPlus to register {app.TitleId}");
        var seen = await WaitForAsync(ct => TitleKnownAsync(app.TitleId, ct), options.Wait(options.AppInstallTimeout), cancellationToken);
        Add(
            "ShadowMountPlus install",
            seen ? SetupStatus.Ok : SetupStatus.Warn,
            seen
                ? $"{app.Name} ({app.TitleId}) is registered on the console."
                : $"{app.TitleId} did not show up within {options.AppInstallTimeout.TotalMinutes:0} minutes. ShadowMountPlus pauses scanning while a game is in the foreground; check /data/shadowmount/debug.log.");
        return seen;
    }

    // VoidShell only rescans on its own timer and on request; ask it, then confirm it lists the title.
    private async Task<bool> RequestVoidShellRescanAsync(CancellationToken cancellationToken) =>
        manifest.Payloads.Any(p => p.Id == "voidshell")
        && await services.Probe.PortOpenAsync(options.VoidShellPort, cancellationToken)
        && await services.Probe.PostAsync(options.VoidShellPort, "/api/rescan", cancellationToken);

    private async Task RefreshVoidShellLibraryAsync(ManifestApp app, CancellationToken cancellationToken)
    {
        if (!await RequestVoidShellRescanAsync(cancellationToken))
        {
            return;
        }

        Ui.Info("Asked VoidShell to rescan its library");
        var listed = await WaitForAsync(ct => ListedByVoidShellAsync(app.TitleId, ct), options.Wait(TimeSpan.FromSeconds(30)), cancellationToken);
        Add(
            "VoidShell library",
            listed ? SetupStatus.Ok : SetupStatus.Warn,
            listed
                ? $"{app.Name} ({app.TitleId}) was registered: it is listed in the VoidShell library after a library rescan."
                : $"VoidShell was asked to rescan but does not list {app.TitleId} yet. It rescans on its own every few seconds; reload the dashboard.");
    }

    // Step 9: everything reachable now?
    private async Task FinalCheckAsync(CancellationToken cancellationToken)
    {
        var version = await services.Manager.GetVersionAsync(cancellationToken);
        Add(
            "Payload Manager",
            version is null ? SetupStatus.Fail : SetupStatus.Ok,
            version is null ? $"Not answering on port {options.PayloadManagerPort}." : $"Version {version} answers on port {options.PayloadManagerPort}.");

        var processes = await services.Manager.GetProcessesAsync(cancellationToken);
        foreach (var payload in manifest.Payloads)
        {
            var ready = await IsReadyAsync(payload, cancellationToken, processes);
            var how = payload.Ready.Port > 0 ? $"port {PortFor(payload)}" : $"process {string.Join(" or ", payload.Ready.Processes!)}";
            var failure = payload.Optional ? SetupStatus.Warn : SetupStatus.Fail;
            var detail = $"Not running ({how}). {_notes.GetValueOrDefault(payload.Id)}".TrimEnd();
            if (!ready && payload.Optional && !string.IsNullOrWhiteSpace(payload.OptionalNote))
            {
                detail += $" Optional: {payload.OptionalNote}";
            }

            Add(payload.Name, ready ? SetupStatus.Ok : failure, ready ? $"Running ({how})." : detail);
        }

        var voidShell = manifest.Payloads.FirstOrDefault(p => p.Id == "voidshell");
        if (voidShell is not null && await services.Probe.PortOpenAsync(options.VoidShellPort, cancellationToken))
        {
            var html = await services.Probe.HtmlAsync(options.VoidShellPort, "/", cancellationToken);
            Add(
                "VoidShell dashboard",
                html ? SetupStatus.Ok : SetupStatus.Warn,
                html ? $"http://{options.Host}:{options.VoidShellPort}/ serves the dashboard." : "The port answers, but the dashboard page does not load (index.html missing?).");
        }

        var autoload = await services.Manager.GetAutoloadAsync(cancellationToken);
        if (autoload is { Enabled: false })
        {
            Add("Autoload", SetupStatus.Warn, "Autoload is switched off.");
        }
    }

    private int _installLogBaseline;

    private enum AppInstall
    {
        Failed,
        Unchanged,
        Copied,
    }

    private static int CountInstalledLines(string? log, string titleId) =>
        log is null
            ? 0
            : log.Split('\n').Count(line => line.Contains("installed game", StringComparison.OrdinalIgnoreCase)
                && line.Contains(titleId, StringComparison.OrdinalIgnoreCase));

    private async Task<bool> ListedByVoidShellAsync(string titleId, CancellationToken cancellationToken)
    {
        if (services.VoidShell is null)
        {
            return false;
        }

        var snapshot = await services.VoidShell.ProbeAsync(cancellationToken);
        return snapshot.Available && snapshot.InstalledTitleIds.Any(id => id.Equals(titleId, StringComparison.OrdinalIgnoreCase));
    }

    // Nothing was copied, so an old "installed game" log line proves nothing: only the live registration counts.
    private async Task CheckExistingRegistrationAsync(ManifestApp app, CancellationToken cancellationToken)
    {
        var rescanned = false;
        var listed = await ListedByVoidShellAsync(app.TitleId, cancellationToken);
        if (!listed && await RequestVoidShellRescanAsync(cancellationToken))
        {
            rescanned = true;
            listed = await WaitForAsync(ct => ListedByVoidShellAsync(app.TitleId, ct), options.Wait(TimeSpan.FromSeconds(30)), cancellationToken);
        }

        Add(
            "ShadowMountPlus install",
            listed ? SetupStatus.Ok : SetupStatus.Warn,
            listed
                ? $"{app.Name} ({app.TitleId}) is registered on the console{(rescanned ? ": listed in the VoidShell library after a library rescan" : string.Empty)}."
                : $"{app.Name} was already in place and nothing was copied, so setup cannot confirm the registration (VoidShell does not list it). Check the home screen.");
    }

    private async Task<bool> TitleKnownAsync(string titleId, CancellationToken cancellationToken)
    {
        if (await ListedByVoidShellAsync(titleId, cancellationToken))
        {
            return true;
        }

        // VoidShell's library can be empty for titles ShadowMountPlus installed, so read ShadowMountPlus's own
        // "installed game <id>" line, counting only lines written since this run copied the files.
        var log = await services.Files.ReadTextAsync(ShadowMountLog, MaxTextBytes, cancellationToken);
        return CountInstalledLines(log, titleId) > _installLogBaseline;
    }

    private async Task<bool> IsReadyAsync(ManifestPayload payload, CancellationToken cancellationToken, IReadOnlySet<string>? known = null)
    {
        if (payload.Ready.Port > 0)
        {
            return await services.Probe.PortOpenAsync(PortFor(payload), cancellationToken);
        }

        var processes = known ?? await services.Manager.GetProcessesAsync(cancellationToken);
        return processes is not null && payload.Ready.Processes!.Any(processes.Contains);
    }

    private int PortFor(ManifestPayload payload) => payload.Id.ToLowerInvariant() switch
    {
        "ftpsrv" => options.FtpPort,
        "elfldr" => options.ElfldrPort,
        "voidshell" => options.VoidShellPort,
        _ => payload.Ready.Port,
    };

    private static ManagedPayload? FindStored(IReadOnlyList<ManagedPayload> inventory, ManifestPayload payload) =>
        inventory.FirstOrDefault(item =>
            item.FileName.Equals(payload.UploadName, StringComparison.OrdinalIgnoreCase)
            || item.Slot.Equals("local/" + payload.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Id, payload.Id, StringComparison.OrdinalIgnoreCase));

    internal async Task<bool> WaitForAsync(Func<CancellationToken, Task<bool>> check, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = services.Time.GetUtcNow() + timeout;
        while (true)
        {
            if (await check(cancellationToken))
            {
                return true;
            }

            if (services.Time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(options.WaitOverride ?? options.PollInterval, services.Time, cancellationToken);
        }
    }

    private void Add(string component, SetupStatus status, string message) => _lines.Add(new ReportLine(component, status, message));

    private void Notify(string message) => Ui.Info(message);
}
