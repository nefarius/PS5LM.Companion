using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Infrastructure.Setup;

namespace PS5LM.Companion.App.Setup;

/// <summary>
/// <c>update</c>: a one-shot command that brings a prepared console up to the pinned manifest. Exit codes: 0 done,
/// 1 bad arguments or an unreadable manifest, 2 something failed, 3 <c>--check</c> found updates.
/// </summary>
internal static class UpdateCommand
{
    private const string Usage = """
        Usage: update --host <console> [--apps] [--variant ps5lm|ps5lmplus] [--check] [options]

          --host <console>              Console IP or host name (required)
          --apps                        Also update the installed PS5LM / PS5LM Plus app
          --variant <ps5lm|ps5lmplus>   Limit the app update to one variant
          --restart                     Stop and relaunch an updated payload that is still running (pinned VoidShell fork)
          --check                       Only report what is out of date (exit code 3 when something is)
          --upgrade-payload-manager     Upgrade an older Payload Manager without asking
          --payload-manager-url <url>   When Payload Manager is not on port 8084 of the host
          --ftp-port <port>             ftpsrv port (default 2121)
          --manifest <file>             Use this manifest instead of the built-in one
          --cache <dir>                 Download cache (default <Companion__DataDirectory>/setup-cache)
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        Dictionary<string, string?> values;
        try
        {
            values = CommandSupport.Parse(
                args,
                ["apps", "check", "restart", "upgrade-payload-manager", "help"],
                ["host", "variant", "payload-manager-url", "ftp-port", "manifest", "cache"]);
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

        var host = values.GetValueOrDefault("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            Console.Error.WriteLine("--host is required.");
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (values.ContainsKey("variant") && !values.ContainsKey("apps"))
        {
            Console.Error.WriteLine("--variant only applies together with --apps.");
            return 1;
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
            var variant = values.GetValueOrDefault("variant");
            if (variant is not null)
            {
                variant = manifest.Apps.FirstOrDefault(a => a.Id.Equals(variant, StringComparison.OrdinalIgnoreCase))?.Id;
                if (variant is null)
                {
                    Console.Error.WriteLine($"Unknown --variant '{values["variant"]}'. Choose {string.Join(" or ", manifest.Apps.Select(a => a.Id))}.");
                    Console.Error.WriteLine(Usage);
                    return 1;
                }
            }

            var ftpPort = CommandSupport.ParseFtpPort(values);
            var baseAddress = new PS5Options { Host = host, PayloadManagerUrl = values.GetValueOrDefault("payload-manager-url") }.PayloadManagerBaseAddress();
            var options = new UpdateOptions(host, variant)
            {
                IncludeApps = values.ContainsKey("apps"),
                CheckOnly = values.ContainsKey("check"),
                Restart = values.ContainsKey("restart"),
                UpgradePayloadManager = values.ContainsKey("upgrade-payload-manager"),
                FtpPort = ftpPort,
                PayloadManagerPort = baseAddress.Port,
            };

            using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var managerHttp = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) };
            using var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var voidShellHttp = new HttpClient { BaseAddress = new UriBuilder(Uri.UriSchemeHttp, host, options.VoidShellPort).Uri, Timeout = TimeSpan.FromSeconds(5) };
            using var files = new FtpFileSystem(new PS5Options { Host = host, FtpPort = ftpPort }, NullLogger<FtpFileSystem>.Instance);
            var services = new SetupServices(
                new PayloadManagerAdminClient(managerHttp),
                new HttpArtifactStore(downloads, cache),
                files,
                new ConsoleProbe(host, probeHttp),
                new ElfldrSender(host, options.ElfldrPort),
                new VoidShellClient(voidShellHttp),
                new ConsoleUi(),
                TimeProvider.System);

            var report = await new UpdateRunner(manifest, options, services).RunAsync(cancellation.Token);
            CommandSupport.PrintReport(report.Lines);
            return report.ExitCode;
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
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
