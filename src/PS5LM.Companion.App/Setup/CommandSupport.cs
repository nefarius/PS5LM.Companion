using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.App.Setup;

internal sealed class ConsoleUi : ISetupUi
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
internal static class Ansi
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

/// <summary>Argument parsing and report printing shared by <c>setup</c> and <c>update</c>.</summary>
internal static class CommandSupport
{
    public static Dictionary<string, string?> Parse(string[] args, string[] flags, string[] valued)
    {
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

    public static int ParseFtpPort(Dictionary<string, string?> values)
    {
        var ftpPort = 2121;
        if (values.TryGetValue("ftp-port", out var ftp)
            && (!int.TryParse(ftp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ftpPort) || ftpPort is < 1 or > 65535))
        {
            throw new ArgumentException($"--ftp-port must be an integer from 1 to 65535, got '{ftp}'.");
        }

        return ftpPort;
    }

    public static void PrintReport(IReadOnlyList<ReportLine> lines)
    {
        Console.WriteLine();
        Console.WriteLine("Report");
        foreach (var line in lines)
        {
            var (tag, color) = line.Status switch
            {
                SetupStatus.Ok => ("OK   ", Ansi.Green),
                SetupStatus.Warn => ("WARN ", Ansi.Yellow),
                _ => ("FAIL ", Ansi.Red),
            };
            Console.WriteLine($"  {Ansi.Paint(tag, color)} {line.Component}: {line.Message}");
        }
    }
}
