using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Infrastructure.Setup;

namespace PS5LM.Companion.App.Setup;

/// <summary>
/// <c>update</c>: a one-shot command that brings a prepared console up to the pinned manifest. Exit codes: 0 done,
/// 1 bad arguments or an unreadable manifest, 2 something failed, 3 <c>--check</c> found updates. Several consoles
/// return the worst of those codes.
/// </summary>
internal static class UpdateCommand
{
    private const string Usage = """
        Usage: update (--host <console>)... | --all-consoles [--apps] [--settings] [--variant ps5lm|ps5lmplus] [--check] [--force] [options]

          --host <console>              Console IP or host name. Repeat for more than one console
          --all-consoles                Every console in PS5:Consoles and in the stored consoles.json
          --apps                        Also update the installed PS5LM / PS5LM Plus app
          --settings                    Rewrite the autoload list, countdown and OnionHEN bundled-kstuff switch
          --variant <ps5lm|ps5lmplus>   Limit the app update to one variant
          --restart                     Stop and relaunch an updated payload that is still running (VoidShell by its pid file, ShadowMountPlus by launching the new copy)
          --check                       Only report what is out of date (exit code 3 when something is)
          --force                       Also replace an installed app that is newer than the pin
          --upgrade-payload-manager     Upgrade an older Payload Manager without asking
          --payload-manager-url <url>   When Payload Manager is not on port 8084 of the host
          --ftp-port <port>             ftpsrv port (default 2121, or the console's own port with --all-consoles)
          --manifest <file>             Use this manifest instead of the built-in one
          --cache <dir>                 Download cache (default <Companion__DataDirectory>/setup-cache)
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        ParsedArgs parsed;
        try
        {
            parsed = CommandSupport.ParseWithRepeated(
                args,
                ["apps", "settings", "check", "restart", "force", "upgrade-payload-manager", "all-consoles", "help"],
                ["variant", "payload-manager-url", "ftp-port", "manifest", "cache"],
                ["host"]);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (parsed.Has("help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (parsed.Has("variant") && !parsed.Has("apps"))
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
            var dataDirectory = Environment.GetEnvironmentVariable("Companion__DataDirectory") ?? "data";
            var fleet = parsed.Has("all-consoles") ? FleetConsoles.Load(BuildConfiguration(), dataDirectory) : [];
            var targets = Targets(parsed, fleet);
            var manifest = parsed.Get("manifest") is { } file
                ? SetupManifest.Parse(await File.ReadAllTextAsync(file, cancellation.Token))
                : SetupManifest.LoadEmbedded();
            var cache = parsed.Get("cache") ?? Path.Combine(dataDirectory, "setup-cache");
            var variant = parsed.Get("variant");
            if (variant is not null)
            {
                variant = manifest.Apps.FirstOrDefault(app => app.Id.Equals(variant, StringComparison.OrdinalIgnoreCase))?.Id;
                if (variant is null)
                {
                    Console.Error.WriteLine($"Unknown --variant '{parsed.Get("variant")}'. Choose {string.Join(" or ", manifest.Apps.Select(app => app.Id))}.");
                    Console.Error.WriteLine(Usage);
                    return 1;
                }
            }

            var worst = 0;
            foreach (var target in targets)
            {
                if (cancellation.IsCancellationRequested)
                {
                    return 130;
                }

                if (targets.Count > 1)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== {target.Label} ({target.Host}) ===");
                }

                int code;
                try
                {
                    code = await RunOneAsync(manifest, target, parsed, variant, cache, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Cancelled.");
                    return 130;
                }
                catch (TaskCanceledException)
                {
                    Console.Error.WriteLine("A request timed out.");
                    code = 2;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or FormatException)
                {
                    Console.Error.WriteLine(ex.Message);
                    code = 1;
                }

                if (targets.Count > 1)
                {
                    Console.WriteLine($"{target.Label}: {ExitLabel(code)}");
                }

                if (code == 130)
                {
                    return 130;
                }

                worst = Worse(worst, code);
            }

            return worst;
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

    /// <summary>The consoles this invocation updates. Throws <see cref="ArgumentException"/> when the selection is empty or contradictory.</summary>
    internal static IReadOnlyList<ConsoleTarget> Targets(ParsedArgs args, IReadOnlyList<(string Id, PS5Options Options)> fleet)
    {
        var hosts = args.Repeat("host");
        var all = args.Has("all-consoles");
        if (all && hosts.Count > 0)
        {
            throw new ArgumentException("--all-consoles cannot be combined with --host.");
        }

        if (!all && hosts.Count == 0)
        {
            throw new ArgumentException("--host is required.");
        }

        var ftpPort = CommandSupport.ParseFtpPort(args.Values);
        var url = args.Get("payload-manager-url");
        if (!all)
        {
            return hosts.Select(host => new ConsoleTarget(host, host, new PS5Options
            {
                Host = host,
                FtpPort = ftpPort,
                PayloadManagerUrl = url,
            })).ToList();
        }

        if (fleet.Count == 0)
        {
            throw new ArgumentException("No consoles are configured. Pass --host, or set PS5:Consoles and the consoles stored in the data directory.");
        }

        var ftpOverride = args.Has("ftp-port");
        var urlOverride = args.Has("payload-manager-url");
        return fleet.Select(item =>
        {
            if (ftpOverride)
            {
                item.Options.FtpPort = ftpPort;
            }

            if (urlOverride)
            {
                item.Options.PayloadManagerUrl = url;
            }

            return new ConsoleTarget(item.Id, item.Options.Host, item.Options);
        }).ToList();
    }

    /// <summary>2 beats 1 beats 3 beats 0. Cancellation is handled by the caller.</summary>
    internal static int Worse(int current, int next)
    {
        static int Rank(int code) => code switch
        {
            2 => 3,
            1 => 2,
            3 => 1,
            _ => 0,
        };

        return Rank(next) > Rank(current) ? next : current;
    }

    internal static string ExitLabel(int code) => code switch
    {
        0 => "current",
        1 => "bad arguments",
        2 => "failed",
        3 => "updates found",
        130 => "cancelled",
        _ => "exit " + code.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<int> RunOneAsync(
        SetupManifest manifest,
        ConsoleTarget target,
        ParsedArgs parsed,
        string? variant,
        string cache,
        CancellationToken cancellationToken)
    {
        var baseAddress = target.Options.PayloadManagerBaseAddress();
        var options = new UpdateOptions(target.Host, variant)
        {
            IncludeApps = parsed.Has("apps"),
            IncludeSettings = parsed.Has("settings"),
            CheckOnly = parsed.Has("check"),
            Restart = parsed.Has("restart"),
            Force = parsed.Has("force"),
            UpgradePayloadManager = parsed.Has("upgrade-payload-manager"),
            FtpPort = target.Options.FtpPort,
            ElfldrPort = target.Options.ElfldrPort,
            PayloadManagerPort = baseAddress.Port,
            VoidShellPort = target.Options.VoidShellPort,
        };

        using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var managerHttp = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(10) };
        using var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var voidShellHttp = new HttpClient { BaseAddress = new UriBuilder(Uri.UriSchemeHttp, target.Host, options.VoidShellPort).Uri, Timeout = TimeSpan.FromSeconds(5) };
        using var files = new FtpFileSystem(new PS5Options { Host = target.Host, FtpPort = options.FtpPort }, NullLogger<FtpFileSystem>.Instance);
        var services = new SetupServices(
            new PayloadManagerAdminClient(managerHttp),
            new HttpArtifactStore(downloads, cache),
            files,
            new ConsoleProbe(target.Host, probeHttp),
            new ElfldrSender(target.Host, options.ElfldrPort),
            new VoidShellClient(voidShellHttp),
            new ConsoleUi(),
            TimeProvider.System);

        var report = await new UpdateRunner(manifest, options, services).RunAsync(cancellationToken);
        CommandSupport.PrintReport(report.Lines);
        return report.ExitCode;
    }

    private static IConfiguration BuildConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings." + (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production") + ".json", optional: true)
        .AddEnvironmentVariables()
        .Build();
}

/// <summary>One console <c>update</c> will visit.</summary>
/// <param name="Label">Shown in the fleet summary. The host for <c>--host</c>, the console id for <c>--all-consoles</c>.</param>
internal readonly record struct ConsoleTarget(string Label, string Host, PS5Options Options);
