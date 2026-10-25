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

        var ftpPort = 2121;
        if (values.TryGetValue("ftp-port", out var ftp)
            && (!int.TryParse(ftp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ftpPort) || ftpPort is < 1 or > 65535))
        {
            throw new ArgumentException($"--ftp-port must be an integer from 1 to 65535, got '{ftp}'.");
        }

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
        Console.WriteLine();
        Console.WriteLine("Report");
        foreach (var line in report.Lines)
        {
            var (tag, color) = line.Status switch
            {
                SetupStatus.Ok => ("OK   ", Ansi.Green),
                SetupStatus.Warn => ("WARN ", Ansi.Yellow),
                _ => ("FAIL ", Ansi.Red),
            };
            Console.WriteLine($"  {Ansi.Paint(tag, color)} {line.Component}: {line.Message}");
        }

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

    private static Dictionary<string, string?> Parse(string[] args)
    {
        string[] flags = ["skip-app", "upgrade-payload-manager", "force", "verify-manifest", "help"];
        string[] valued = ["host", "variant", "payload-manager-url", "ftp-port", "manifest", "cache"];
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{arg}'.");
            }

            var name = arg[2..];
            if (flags.Contains(name))
            {
                result[name] = null;
            }
            else if (valued.Contains(name))
            {
                result[name] = ++i < args.Length ? args[i] : throw new ArgumentException($"--{name} needs a value.");
            }
            else
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return result;
    }

    private sealed class ConsoleUi : ISetupUi
    {
        public bool CanPrompt => !Console.IsInputRedirected;

        public void Info(string message) =>
            Console.WriteLine($"{Ansi.Paint(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), Ansi.Dim)} {Ansi.Highlight(message)}");

        public bool Confirm(string question)
        {
            Console.Write(question + " [y/N] ");
            return Console.ReadLine()?.Trim().StartsWith('y') == true;
        }
    }

    /// <summary>ANSI colours, only when stdout is a terminal that can show them (NO_COLOR and TERM=dumb switch them off).</summary>
    private static class Ansi
    {
        public const string Dim = "90";
        public const string Red = "31";
        public const string Green = "32";
        public const string Yellow = "33";

        private static readonly bool Enabled = Detect();

        public static string Paint(string text, string code) => Enabled ? $"\u001b[{code}m{text}\u001b[0m" : text;

        /// <summary>Colours a leading OK / WARN / FAIL word, as printed by <c>--verify-manifest</c>.</summary>
        public static string Highlight(string message)
        {
            foreach (var (word, code) in new[] { ("OK", Green), ("WARN", Yellow), ("FAIL", Red) })
            {
                if (message.StartsWith(word + " ", StringComparison.Ordinal))
                {
                    return Paint(word, code) + message[word.Length..];
                }
            }

            return message;
        }

        private static bool Detect()
        {
            if (Console.IsOutputRedirected || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            {
                return false;
            }

            var term = Environment.GetEnvironmentVariable("TERM");
            return !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)
                && (OperatingSystem.IsWindows() || !string.IsNullOrEmpty(term));
        }
    }
}
