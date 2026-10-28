using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Infrastructure.Setup;

namespace PS5LM.Companion.App.Setup;

/// <summary>
/// <c>setup</c>: a one-shot command that prepares a console and exits. Exit codes: 0 done, 1 bad arguments or an
/// unreadable manifest, 2 a required component is not working.
/// </summary>
internal static class SetupCommand
{
    private const string Usage = """
        Usage: setup --host <console> [--variant ps5lm|ps5lmplus | --skip-app] [options]
               setup --verify-manifest [--manifest <file>] [--cache <dir>]

          --host <console>              Console IP or host name (required)
          --variant <ps5lm|ps5lmplus>   App to install for ShadowMountPlus
          --skip-app                    Do not install an app
          --upgrade-payload-manager     Upgrade an older Payload Manager without asking
          --force                       Re-upload payloads and extra files, reinstall the app
          --payload-manager-url <url>   When Payload Manager is not on port 8084 of the host
          --ftp-port <port>             ftpsrv port (default 2121)
          --manifest <file>             Use this manifest instead of the built-in one
          --cache <dir>                 Download cache (default <Companion__DataDirectory>/setup-cache)
          --verify-manifest             Download every artifact and check its SHA-256; no console needed

        The built-in manifest requires the pinned nefarius/voidshell fork on port 7007.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        Dictionary<string, string?> values;
        try
        {
            values = Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (values.ContainsKey("help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var manifest = values.TryGetValue("manifest", out var file) && file is not null
                ? SetupManifest.Parse(await File.ReadAllTextAsync(file, cancellation.Token))
                : SetupManifest.LoadEmbedded();
            var cache = values.GetValueOrDefault("cache")
                ?? Path.Combine(Environment.GetEnvironmentVariable("Companion__DataDirectory") ?? "data", "setup-cache");
            using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var store = new HttpArtifactStore(downloads, cache);
            var ui = new ConsoleUi();

            if (values.ContainsKey("verify-manifest"))
            {
                return await SetupRunner.VerifyManifestAsync(manifest, store, ui, cancellation.Token) ? 0 : 2;
            }

            return await RunSetupAsync(values, manifest, store, ui, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("A request timed out.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunSetupAsync(Dictionary<string, string?> values, SetupManifest manifest, HttpArtifactStore store, ConsoleUi ui, CancellationToken cancellationToken)
    {
        var host = values.GetValueOrDefault("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("--host is required.");
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var skipApp = values.ContainsKey("skip-app");
        var variant = values.GetValueOrDefault("variant");
        if (!skipApp && variant is null)
        {
            variant = ui.CanPrompt ? Choose(manifest) : null;
            if (variant is null)
            {
                Console.Error.WriteLine("Choose an app with --variant, or pass --skip-app.");
                return 1;
            }
        }

        var ftpPort = CommandSupport.ParseFtpPort(values);

        var baseAddress = new PS5Options { Host = host, PayloadManagerUrl = values.GetValueOrDefault("payload-manager-url") }.PayloadManagerBaseAddress();
        var options = new SetupOptions(host, variant)
        {
            UpgradePayloadManager = values.ContainsKey("upgrade-payload-manager"),
            Force = values.ContainsKey("force"),
            SkipApp = skipApp,
            FtpPort = ftpPort,
            PayloadManagerPort = baseAddress.Port,
        };

        using var managerHttp = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) };
        using var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var voidShellHttp = new HttpClient { BaseAddress = new UriBuilder(Uri.UriSchemeHttp, host, options.VoidShellPort).Uri, Timeout = TimeSpan.FromSeconds(5) };
        using var files = new FtpFileSystem(new PS5Options { Host = host, FtpPort = ftpPort }, NullLogger<FtpFileSystem>.Instance);
        var services = new SetupServices(
            new PayloadManagerAdminClient(managerHttp),
            store,
            files,
            new ConsoleProbe(host, probeHttp),
            new ElfldrSender(host, options.ElfldrPort),
            new VoidShellClient(voidShellHttp),
            ui,
            TimeProvider.System);

        var report = await new SetupRunner(manifest, options, services).RunAsync(cancellationToken);
        CommandSupport.PrintReport(report.Lines);

        return report.ExitCode;
    }

    private static string? Choose(SetupManifest manifest)
    {
        Console.WriteLine("Which app should be installed?");
        for (var i = 0; i < manifest.Apps.Count; i++)
        {
            Console.WriteLine($"  {i + 1}) {manifest.Apps[i].Name} {manifest.Apps[i].Version}");
        }

        Console.Write("Number: ");
        return int.TryParse(Console.ReadLine(), out var choice) && choice >= 1 && choice <= manifest.Apps.Count
            ? manifest.Apps[choice - 1].Id
            : null;
    }

    private static Dictionary<string, string?> Parse(string[] args) => CommandSupport.Parse(
        args,
        ["skip-app", "upgrade-payload-manager", "force", "verify-manifest", "help"],
        ["host", "variant", "payload-manager-url", "ftp-port", "manifest", "cache"]);
}
