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
    private const string ParamSuffix = "sce_sys/param.json";

    private readonly SetupManifest _manifest;
    private readonly UpdateOptions _options;
    private readonly SetupServices _services;
    private readonly SetupRunner _setup;
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
            await using (var file = File.OpenRead(local))
            {
                await _services.Manager.UploadAsync(payload.UploadName, file, cancellationToken);
            }

            uploaded.Add(payload);
        }

        if (uploaded.Count == 0)
        {
            return;
        }

        inventory = await _services.Manager.ListPayloadsAsync(cancellationToken);
        var toLaunch = new List<(ManifestPayload, ManagedPayload)>();
        foreach (var payload in uploaded)
        {
            var stored = SetupRunner.FindStored(inventory, payload);
            if (stored is null)
            {
                Add(payload.Name, SetupStatus.Fail, "Payload Manager does not list it after the upload.");
            }
            else if (await _setup.IsReadyAsync(payload, cancellationToken))
            {
                Add(payload.Name, SetupStatus.Warn, $"Uploaded {payload.Version}, but the old build is still running. The new one loads at the next autoload or console reboot.");
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
            if (installed is null)
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

            if (installed.Trim() == wanted.Trim())
            {
                Add(app.Name, SetupStatus.Ok, $"{app.Version} is current.");
                continue;
            }

            if (_options.CheckOnly)
            {
                Pending(app.Name, $"installed build differs from {app.Version}");
                continue;
            }

            if (await IsRunningAsync(app.TitleId, cancellationToken))
            {
                Add(app.Name, SetupStatus.Warn, $"{app.TitleId} is running; close it and run update again. Nothing was changed.");
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

    private async Task<bool> IsRunningAsync(string titleId, CancellationToken cancellationToken)
    {
        if (_services.VoidShell is null)
        {
            return false;
        }

        var snapshot = await _services.VoidShell.ProbeAsync(cancellationToken);
        // The reported id can be truncated.
        return snapshot is { Available: true, RunningKnown: true, ActiveTitleId.Length: > 0 }
            && titleId.StartsWith(snapshot.ActiveTitleId, StringComparison.OrdinalIgnoreCase);
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
