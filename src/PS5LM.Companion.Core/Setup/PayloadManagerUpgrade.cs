using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// Replaces the running Payload Manager with the manifest version.
/// Storing <c>pldmgr_vX.Y.Z.elf</c> through the manager is what makes the upgrade survive a reboot: on startup the old
/// copy finds the newer file in its own folder and hands off to it. The rest starts the new copy now, through elfldr.
/// </summary>
internal sealed class PayloadManagerUpgrade(
    SetupManifest manifest,
    SetupOptions options,
    SetupServices services,
    Action<string, SetupStatus, string> add,
    Action<string> info)
{
    private const string Component = "Payload Manager";

    public async Task<bool> RunAsync(Version target, CancellationToken cancellationToken)
    {
        var wanted = manifest.PayloadManager;
        info($"Upgrading Payload Manager to {wanted.Version}");
        var path = await services.Artifacts.FetchAsync(wanted.Url, wanted.Sha256, cancellationToken);
        await using (var file = File.OpenRead(path))
        {
            await services.Manager.UploadAsync(wanted.FileName, file, cancellationToken);
        }

        if (!await EnsureElfldrAsync(cancellationToken))
        {
            add(Component, SetupStatus.Fail, $"{wanted.FileName} is stored, so Payload Manager upgrades itself the next time the console starts it. Setup needs the new version now, so it stopped.");
            return false;
        }

        info("Stopping the old Payload Manager");
        await services.Manager.ShutdownAsync(cancellationToken);
        if (!await WaitAsync(async ct => !await services.Probe.PortOpenAsync(options.PayloadManagerPort, ct), 20, cancellationToken))
        {
            add(Component, SetupStatus.Fail, "The old Payload Manager did not stop. Nothing else was changed.");
            return false;
        }

        try
        {
            info("Starting the new Payload Manager through elfldr");
            await services.Elfldr.SendAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            add(Component, SetupStatus.Fail, Fallback($"Sending it to elfldr failed: {ex.Message}"));
            return false;
        }

        var upgraded = await WaitAsync(
            async ct =>
            {
                var text = await services.Manager.GetVersionAsync(ct);
                return text is not null && PayloadManagerVersion.TryParse(text, out var now) && now >= target;
            },
            60,
            cancellationToken);
        if (!upgraded)
        {
            add(Component, SetupStatus.Fail, Fallback($"It did not report {wanted.Version} within 60 s."));
            return false;
        }

        add(Component, SetupStatus.Ok, $"Upgraded to {wanted.Version}.");
        return true;
    }

    // Payload Manager reaches other payloads through elfldr, so a closed port has to be fixed first.
    private async Task<bool> EnsureElfldrAsync(CancellationToken cancellationToken)
    {
        if (await services.Probe.PortOpenAsync(options.ElfldrPort, cancellationToken))
        {
            return true;
        }

        var elfldr = manifest.Payloads.FirstOrDefault(p => p.Id.Equals("elfldr", StringComparison.OrdinalIgnoreCase));
        if (elfldr is null)
        {
            return false;
        }

        info("elfldr is not listening; storing and launching it first");
        var path = await services.Artifacts.FetchAsync(elfldr.Url, elfldr.Sha256, cancellationToken);
        await using (var file = File.OpenRead(path))
        {
            await services.Manager.UploadAsync(elfldr.UploadName, file, cancellationToken);
        }

        var stored = (await services.Manager.ListPayloadsAsync(cancellationToken))
            .FirstOrDefault(p => p.FileName.Equals(elfldr.UploadName, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
        {
            return false;
        }

        var launch = await services.Manager.LoadAsync(stored.LaunchTarget, cancellationToken);
        if (!launch.Accepted)
        {
            info($"Payload Manager could not start elfldr: {launch.Message}");
        }

        if (await WaitAsync(ct => services.Probe.PortOpenAsync(options.ElfldrPort, ct), 20, cancellationToken))
        {
            return true;
        }

        info(PayloadDependencies.ElfldrBootstrap(options.ElfldrPort));
        return false;
    }

    private static string Fallback(string reason) =>
        $"{reason} Payload Manager is down until the console starts its autoloader again; it then comes up as the new version from the stored update.";

    private async Task<bool> WaitAsync(Func<CancellationToken, Task<bool>> check, int seconds, CancellationToken cancellationToken)
    {
        var deadline = services.Time.GetUtcNow() + options.Wait(TimeSpan.FromSeconds(seconds));
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
}
