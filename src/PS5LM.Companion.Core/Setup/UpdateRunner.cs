using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// Brings an already prepared console up to the pinned manifest: replaces payloads and extra files whose bytes differ
/// from the pin, and with <see cref="UpdateOptions.IncludeApps"/> the installed PS5LM / PS5LM Plus app. It never
/// installs something that is absent (that is <c>setup</c>'s job) and never rewrites the autoload list.
/// </summary>
public sealed class UpdateRunner
{
    private const int MaxPayloadBytes = 256 * 1024 * 1024;
    private const int MaxTextBytes = 4 * 1024 * 1024;
    private const string PayloadStorageRoot = "/data/pldmgr/payloads/";
    private const string ParamSuffix = "sce_sys/param.json";

    private readonly SetupManifest _manifest;
    private readonly UpdateOptions _options;
    private readonly SetupServices _services;
    private readonly SetupRunner _setup;
    private readonly List<(ManifestPayload Payload, ManagedPayload Stored)> _stillOld = [];
    private int _pending;

    public UpdateRunner(SetupManifest manifest, UpdateOptions options, SetupServices services)
    {
        _manifest = manifest;
        _options = options;
        _services = services;
        _setup = new SetupRunner(manifest, options.ToSetupOptions(), services);
    }

    private ISetupUi Ui => _services.Ui;

    public async Task<UpdateReport> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await CheckPayloadManagerAsync(cancellationToken))
            {
                await UpdatePayloadsAsync(cancellationToken);
                await UpdateExtraFilesAsync(cancellationToken);
                if (_options.IncludeApps)
                {
                    await UpdateAppsAsync(cancellationToken);
                }

                // Last: replacing ftpsrv or VoidShell drops connections the steps above still use.
                await RestartOrWarnAsync(cancellationToken);
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Add("Update", SetupStatus.Fail, "A request to the console timed out. Steps reported above completed before it.");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ConsoleUnreachableException or InvalidDataException or JsonException)
        {
            Add("Update", SetupStatus.Fail, ex.Message);
        }

        return new UpdateReport(_setup.Lines, _options.CheckOnly, _pending);
    }

    private async Task<bool> CheckPayloadManagerAsync(CancellationToken cancellationToken)
    {
        Ui.Info($"Checking Payload Manager on {_options.Host}:{_options.PayloadManagerPort}");
        var text = await _services.Manager.GetVersionAsync(cancellationToken);
        if (text is null || !PayloadManagerVersion.TryParse(text, out var running))
        {
            Add("Payload Manager", SetupStatus.Fail, $"Not reachable on port {_options.PayloadManagerPort}. Update only works on a prepared console; run setup first.");
            return false;
        }

        PayloadManagerVersion.TryParse(_manifest.PayloadManager.Version, out var target);
        if (running >= target)
        {
            Add("Payload Manager", SetupStatus.Ok, $"{text} is current.");
            return true;
        }

        if (_options.CheckOnly)
        {
            Pending("Payload Manager", $"{text} -> {_manifest.PayloadManager.Version}");
            return true;
        }

        var ok = await _setup.EnsurePayloadManagerAsync(cancellationToken);
        if (ok && await _services.Manager.GetVersionAsync(cancellationToken) is { } now && PayloadManagerVersion.TryParse(now, out var after) && after >= target)
        {
            Add("Payload Manager", SetupStatus.Ok, $"Updated to {now}.");
        }

        return ok;
    }

    private async Task UpdatePayloadsAsync(CancellationToken cancellationToken)
    {
        if (!await RequireFtpAsync("Payloads", cancellationToken))
        {
            return;
        }

        var inventory = await _services.Manager.ListPayloadsAsync(cancellationToken);
        var uploaded = new List<ManifestPayload>();
        foreach (var payload in _manifest.Payloads)
        {
            var stored = SetupRunner.FindStored(inventory, payload);
            if (stored is null)
            {
                Add(payload.Name, SetupStatus.Warn, "Not stored in Payload Manager. Run setup to install it.");
                continue;
            }

            if (string.IsNullOrEmpty(stored.Path))
            {
                Add(payload.Name, SetupStatus.Warn, "Payload Manager does not report where the file is stored, so it cannot be compared.");
                continue;
            }

            if (!IsPayloadStoragePath(stored.Path))
            {
                Add(payload.Name, SetupStatus.Warn, $"Payload Manager reports '{stored.Path}', which is outside {PayloadStorageRoot}. It was not compared or replaced.");
                continue;
            }

            var local = await _services.Artifacts.FetchAsync(payload.Url, payload.Sha256, cancellationToken);
            var remote = await _services.Files.ReadBytesAsync(stored.Path, MaxPayloadBytes, cancellationToken);
            if (remote is not null && HashEquals(remote, local))
            {
                Add(payload.Name, SetupStatus.Ok, $"{payload.Version} is current.");
                continue;
            }

            if (_options.CheckOnly)
            {
                Pending(payload.Name, $"stored file differs from {payload.Version}");
                continue;
            }

            Ui.Info($"Uploading {payload.Name} {payload.Version}");
            try
            {
                await using var file = File.OpenRead(local);
                await _services.Manager.UploadAsync(payload.UploadName, file, cancellationToken);
            }
            catch (IOException ex)
            {
                // Payload Manager cannot overwrite a file that is in use; renaming a new file over it works.
                Ui.Info($"Payload Manager refused the upload ({ex.Message}); replacing {stored.Path} over FTP instead");
                await using var file = File.OpenRead(local);
                await _services.Files.UploadAsync(file, stored.Path + ".part", null, cancellationToken);
                await _services.Files.MoveAsync(stored.Path + ".part", stored.Path, cancellationToken);
            }

            uploaded.Add(payload);
        }

        if (uploaded.Count == 0)
        {
            return;
        }

        inventory = await _services.Manager.ListPayloadsAsync(cancellationToken);
        var toLaunch = new List<(ManifestPayload, ManagedPayload)>();
        var processes = await _services.Manager.GetProcessesAsync(cancellationToken);
        foreach (var payload in uploaded)
        {
            var stored = SetupRunner.FindStored(inventory, payload);
            if (stored is null)
            {
                Add(payload.Name, SetupStatus.Fail, "Payload Manager does not list it after the upload.");
            }
            else if (payload.Ready.Port <= 0 && processes is null)
            {
                // Launching a payload that may already run would leave a second copy that cannot bind.
                Add(payload.Name, SetupStatus.Warn, $"Uploaded {payload.Version}, but Payload Manager's process list could not be read, so it is unknown whether the old build runs. It was not launched; run update again.");
            }
            else if (await _setup.IsReadyAsync(payload, cancellationToken, processes))
            {
                _stillOld.Add((payload, stored));
            }
            else
            {
                toLaunch.Add((payload, stored));
            }
        }

        await _setup.LaunchPayloadsAsync(toLaunch, cancellationToken);
        foreach (var (payload, _) in toLaunch)
        {
            var ready = await _setup.IsReadyAsync(payload, cancellationToken);
            Add(
                payload.Name,
                ready ? SetupStatus.Ok : payload.Optional ? SetupStatus.Warn : SetupStatus.Fail,
                ready ? $"Updated to {payload.Version} and running." : $"Uploaded {payload.Version}, but it is not running.");
        }
    }

    // A running payload cannot be replaced by launching it again (the new copy cannot bind its port), so the old one is
    // terminated by the pid it wrote to its pid file, after checking that pid really is a payload process.
    private async Task RestartOrWarnAsync(CancellationToken cancellationToken)
    {
        foreach (var (payload, stored) in _stillOld)
        {
            var uploadedNote = $"Uploaded {payload.Version}, but the old build is still running. The new one loads at the next autoload or console reboot";
            if (!_options.Restart)
            {
                Add(payload.Name, SetupStatus.Warn, uploadedNote + (payload.PidFile is null ? "." : "; run update --restart to replace it now."));
                continue;
            }

            if (payload.PidFile is null)
            {
                Add(payload.Name, SetupStatus.Warn, uploadedNote + " (it cannot be restarted: no pid file is known for it).");
                continue;
            }

            var failure = await TerminateAsync(payload, cancellationToken);
            if (failure is not null)
            {
                Add(payload.Name, SetupStatus.Warn, $"{uploadedNote}. Not restarted: {failure}");
                continue;
            }

            await _setup.LaunchPayloadsAsync([(payload, stored)], cancellationToken);
            var ready = await _setup.IsReadyAsync(payload, cancellationToken);
            Add(
                payload.Name,
                ready ? SetupStatus.Ok : payload.Optional ? SetupStatus.Warn : SetupStatus.Fail,
                ready ? $"Updated to {payload.Version}; the old copy was stopped and the new one is running." : "The old copy was stopped, but the new one did not come up.");
        }
    }

    /// <summary>Stops the running copy; returns why it could not, or null when it is gone.</summary>
    private async Task<string?> TerminateAsync(ManifestPayload payload, CancellationToken cancellationToken)
    {
        var text = await _services.Files.ReadTextAsync(payload.PidFile!, 64, cancellationToken);
        if (text is null || !int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 1)
        {
            return $"{payload.PidFile} is missing or has no pid.";
        }

        var processes = await _services.Manager.GetProcessIdsAsync(cancellationToken);
        if (processes is null || !processes.TryGetValue(pid, out var name))
        {
            return $"pid {pid} from {payload.PidFile} is not running.";
        }

        // A payload started through elfldr shows up as the loader's generic "payload.elf", not under its own file name.
        var names = new[] { "payload.elf", payload.UploadName, payload.Id + ".elf" };
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase) || name is "pldmgr.elf" or "elfldr.elf" or "mini-syscore.elf")
        {
            return $"pid {pid} from {payload.PidFile} is '{name}', which is not this payload's process.";
        }

        Ui.Info($"Stopping {payload.Name} (pid {pid})");
        if (!await _services.Manager.KillProcessAsync(pid, cancellationToken))
        {
            return $"Payload Manager could not stop pid {pid}.";
        }

        var gone = await _setup.WaitForAsync(async ct => !await _setup.IsReadyAsync(payload, ct), _options.WaitOverride ?? TimeSpan.FromSeconds(10), cancellationToken);
        return gone ? null : $"pid {pid} was stopped but {payload.Name} still answers, so another copy runs (the pid file can name a copy that failed to start).";
    }

    private async Task UpdateExtraFilesAsync(CancellationToken cancellationToken)
    {
        var files = _manifest.Payloads.Where(p => p.Files is { Count: > 0 }).SelectMany(p => p.Files!.Select(f => (Payload: p, File: f))).ToList();
        if (files.Count == 0 || !await RequireFtpAsync("Extra files", cancellationToken))
        {
            return;
        }

        foreach (var (payload, file) in files)
        {
            var label = $"{payload.Name} file";
            var local = await _services.Artifacts.FetchAsync(file.Url, file.Sha256, cancellationToken);
            var remote = await _services.Files.ReadBytesAsync(file.Target, MaxPayloadBytes, cancellationToken);
            if (remote is not null && HashEquals(remote, local))
            {
                Add(label, SetupStatus.Ok, $"{file.Target} is current.");
                continue;
            }

            if (remote is null)
            {
                Add(label, SetupStatus.Warn, $"{file.Target} is missing. Run setup to place it.");
                continue;
            }

            if (_options.CheckOnly)
            {
                Pending(label, $"{file.Target} differs from the pin");
                continue;
            }

            Ui.Info($"Uploading {file.Target}");
            await using (var stream = File.OpenRead(local))
            {
                await _services.Files.UploadAsync(stream, file.Target + ".part", null, cancellationToken);
            }

            await _services.Files.MoveAsync(file.Target + ".part", file.Target, cancellationToken);
            Add(label, SetupStatus.Ok, $"Replaced {file.Target}.");
        }
    }

    private async Task UpdateAppsAsync(CancellationToken cancellationToken)
    {
        var apps = _manifest.Apps.Where(a => _options.AppId is null || a.Id.Equals(_options.AppId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (apps.Count == 0)
        {
            Add("App", SetupStatus.Fail, $"Unknown app '{_options.AppId}'. Choose {string.Join(" or ", _manifest.Apps.Select(a => a.Id))}.");
            return;
        }

        if (!await RequireFtpAsync("App", cancellationToken))
        {
            return;
        }

        foreach (var app in apps)
        {
            var installed = await _services.Files.ReadTextAsync($"/data/homebrew/{app.TitleId}/{ParamSuffix}", MaxTextBytes, cancellationToken);
            if (installed is null && !await _services.Files.FileExistsAsync($"/data/homebrew/{app.TitleId}/{ParamSuffix}", cancellationToken))
            {
                // With an explicit --variant the person expects it to be there.
                Add(app.Name, _options.AppId is null ? SetupStatus.Ok : SetupStatus.Warn, "Not installed; nothing to update. Run setup to install it.");
                continue;
            }

            var wanted = await ReadArchiveParamAsync(app, cancellationToken);
            if (wanted is null)
            {
                Add(app.Name, SetupStatus.Fail, "The download has no sce_sys/param.json.");
                continue;
            }

            if (SetupRunner.ReadTitleId(wanted) is not { } archiveTitle || !archiveTitle.Equals(app.TitleId, StringComparison.Ordinal))
            {
                Add(app.Name, SetupStatus.Fail, $"The download's param.json names title '{SetupRunner.ReadTitleId(wanted) ?? "(none)"}', expected {app.TitleId}. Nothing was changed.");
                continue;
            }

            // An installed param.json over the read limit comes back as null: it cannot be equal, so it is stale.
            if (installed is not null && installed.Trim() == wanted.Trim())
            {
                Add(app.Name, SetupStatus.Ok, $"{app.Version} is current.");
                continue;
            }

            if (_options.CheckOnly)
            {
                Pending(app.Name, $"installed build differs from {app.Version}");
                continue;
            }

            switch (await IsRunningAsync(app.TitleId, cancellationToken))
            {
                case true:
                    Add(app.Name, SetupStatus.Warn, $"{app.TitleId} is running; close it and run update again. Nothing was changed.");
                    continue;
                case null:
                    Add(app.Name, SetupStatus.Warn, $"Cannot tell whether {app.TitleId} is running (VoidShell is not reporting it). Start VoidShell and run update again. Nothing was changed.");
                    continue;
            }

            if (await _setup.InstallAppAsync(app, cancellationToken) == SetupRunner.AppInstall.Copied)
            {
                await _setup.RefreshVoidShellLibraryAsync(app, cancellationToken);
            }
        }
    }

    private async Task<string?> ReadArchiveParamAsync(ManifestApp app, CancellationToken cancellationToken)
    {
        var zipPath = await _services.Artifacts.FetchAsync(app.Url, app.Sha256, cancellationToken);
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries
            .Where(e => e.FullName.EndsWith(ParamSuffix, StringComparison.Ordinal) && e.FullName.Count(c => c == '/') <= 2)
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>True or false when VoidShell establishes whether the title runs; null when it cannot.</summary>
    private async Task<bool?> IsRunningAsync(string titleId, CancellationToken cancellationToken)
    {
        if (_services.VoidShell is null)
        {
            return null;
        }

        var snapshot = await _services.VoidShell.ProbeAsync(cancellationToken);
        if (!snapshot.Available || !snapshot.RunningKnown)
        {
            return null;
        }

        // The reported id can be truncated; null means nothing runs.
        return snapshot.ActiveTitleId is { Length: > 0 } active
            && titleId.StartsWith(active, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> RequireFtpAsync(string component, CancellationToken cancellationToken)
    {
        if (await _services.Probe.PortOpenAsync(_options.FtpPort, cancellationToken))
        {
            return true;
        }

        Add(component, SetupStatus.Fail, $"ftpsrv is not answering on port {_options.FtpPort}, so nothing could be compared.");
        return false;
    }

    // The path is data from the console's HTTP service; only ever write under Payload Manager's own storage.
    private static bool IsPayloadStoragePath(string path) =>
        path.StartsWith(PayloadStorageRoot, StringComparison.Ordinal)
        && path.Length > PayloadStorageRoot.Length
        && !path.EndsWith('/')
        && path[PayloadStorageRoot.Length..].Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    private static bool HashEquals(byte[] remote, string localPath)
    {
        using var stream = File.OpenRead(localPath);
        return SHA256.HashData(stream).AsSpan().SequenceEqual(SHA256.HashData(remote));
    }

    private void Pending(string component, string detail)
    {
        _pending++;
        Add(component, SetupStatus.Warn, "Update available: " + detail + ".");
    }

    private void Add(string component, SetupStatus status, string message) => _setup.Add(component, status, message);
}
