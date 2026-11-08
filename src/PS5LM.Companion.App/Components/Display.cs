using System.Globalization;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Components;

using MudBlazor;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.App.Components;

internal static class Display
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string Lifecycle(CompanionLifecycle lifecycle) => lifecycle switch
    {
        CompanionLifecycle.Disconnected => "Console unreachable",
        CompanionLifecycle.AppAvailableNoModel => "No model loaded",
        CompanionLifecycle.Loading => "Loading model",
        CompanionLifecycle.Ready => "Ready",
        CompanionLifecycle.Generating => "Generating",
        _ => lifecycle.ToString(),
    };

    public static Color LifecycleColor(CompanionLifecycle lifecycle) => lifecycle switch
    {
        CompanionLifecycle.Disconnected => Color.Error,
        CompanionLifecycle.AppAvailableNoModel => Color.Warning,
        CompanionLifecycle.Loading => Color.Info,
        CompanionLifecycle.Ready => Color.Success,
        CompanionLifecycle.Generating => Color.Primary,
        _ => Color.Default,
    };

    public static string Requirement(DependencyRequirement requirement) => requirement switch
    {
        DependencyRequirement.Required => "Required",
        DependencyRequirement.Recommended => "Recommended",
        DependencyRequirement.Bundled => "Bundled",
        _ => requirement.ToString(),
    };

    public static string Presence(DependencyPresence presence) => presence switch
    {
        DependencyPresence.Unknown => "Unknown",
        DependencyPresence.NotInstalled => "Not installed",
        DependencyPresence.Installed => "Installed",
        DependencyPresence.Bundled => "Bundled",
        _ => presence.ToString(),
    };

    public static string Health(DependencyHealth health) => health switch
    {
        DependencyHealth.Unavailable => "Unavailable",
        DependencyHealth.Unknown => "Unknown",
        DependencyHealth.LaunchRecorded => "Launch recorded",
        DependencyHealth.Verified => "Verified",
        DependencyHealth.NotRunning => "Not running",
        _ => health.ToString(),
    };

    public static Color HealthColor(DependencyHealth health) => health switch
    {
        DependencyHealth.Verified => Color.Success,
        DependencyHealth.LaunchRecorded => Color.Info,
        DependencyHealth.Unknown or DependencyHealth.NotRunning => Color.Warning,
        _ => Color.Default,
    };

    public static ComponentUpdate? UpdateFor(CompanionStatus status, string id) =>
        status.Updates?.Outdated.FirstOrDefault(update => update.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string UpdateTooltip(ComponentUpdate update) =>
        $"Pinned {update.Pinned}. {update.Detail}.";

    public static string? AppUpdateId(PS5Variant variant) => ComponentUpdates.AppId(variant);

    /// <summary>
    /// The detected app and its installed version: <c>PS5LM 0.11.0</c>, <c>PS5LM (version unknown)</c>
    /// when the variant is known but <c>param.json</c> was unreadable, or <c>Not detected</c>.
    /// </summary>
    public static string AppText(CompanionStatus status)
    {
        if (status.Variant == PS5Variant.Unknown)
        {
            return "Not detected";
        }

        var name = VariantProfile.For(status.Variant)?.DisplayName ?? status.Variant.ToString();
        return status.AppVersion is { Length: > 0 } version ? $"{name} {version}" : $"{name} (version unknown)";
    }

    internal enum CommandShell
    {
        Posix,
        Cmd,
    }

    internal readonly record struct ShellCommand(string Command, string Label);

    /// <summary>
    /// The <c>update</c> command for this console. Payload Manager URL and FTP port are added only when they
    /// differ from the CLI defaults (empty URL, port 2121).
    /// </summary>
    public static string UpdateCommand(string host, PS5Options options, bool apps, bool settings = false) =>
        TryUpdateCommand(host, options, apps, CommandShell.Posix, settings)
        ?? throw new ArgumentException("The host or Payload Manager URL cannot be represented in a POSIX shell.");

    /// <summary>
    /// Labeled <c>update</c> commands for each shell that can represent the host and Payload Manager URL.
    /// Identical POSIX and cmd.exe lines are shown once.
    /// </summary>
    internal static IReadOnlyList<ShellCommand> UpdateCommandLines(string host, PS5Options options, bool apps, bool settings = false)
    {
        var posix = TryUpdateCommand(host, options, apps, CommandShell.Posix, settings);
        var cmd = TryUpdateCommand(host, options, apps, CommandShell.Cmd, settings);
        if (posix is not null && posix == cmd)
        {
            return [new ShellCommand(posix, "POSIX or cmd.exe")];
        }

        var lines = new List<ShellCommand>(2);
        if (posix is not null)
        {
            lines.Add(new ShellCommand(posix, "POSIX"));
        }

        if (cmd is not null)
        {
            lines.Add(new ShellCommand(cmd, "cmd.exe"));
        }

        return lines;
    }

    /// <summary>Dashboard sentence that labels each copy-paste <c>update</c> command with its target shell.</summary>
    internal static MarkupString UpdateCommandGuidance(string host, PS5Options options, bool apps, bool settings = false)
    {
        var lines = UpdateCommandLines(host, options, apps, settings);
        if (lines.Count == 0)
        {
            return new MarkupString();
        }

        var first = Code(lines[0].Command) + " (" + Html(lines[0].Label) + ")";
        var html = lines.Count == 1
            ? "Run " + first + " to bring them in line."
            : "Run " + first + " or " + Code(lines[1].Command) + " (" + Html(lines[1].Label) + ") to bring them in line.";
        return new MarkupString(html);
    }

    private static string Code(string value) => "<code>" + Html(value) + "</code>";

    private static string Html(string value) => HtmlEncoder.Default.Encode(value);

    internal static string? TryUpdateCommand(string host, PS5Options options, bool apps, CommandShell shell, bool settings = false)
    {
        var quotedHost = QuoteForShell(host, shell);
        if (quotedHost is null)
        {
            return null;
        }

        var command = "update --host " + quotedHost;
        if (apps)
        {
            command += " --apps";
        }

        if (settings)
        {
            command += " --settings";
        }

        if (!string.IsNullOrWhiteSpace(options.PayloadManagerUrl))
        {
            var quotedUrl = QuoteForShell(options.PayloadManagerUrl.Trim(), shell);
            if (quotedUrl is null)
            {
                return null;
            }

            command += " --payload-manager-url " + quotedUrl;
        }

        if (options.FtpPort != 2121)
        {
            command += " --ftp-port " + options.FtpPort.ToString(CultureInfo.InvariantCulture);
        }

        return command;
    }

    /// <summary>
    /// Quotes a value so copy-paste keeps it as one argument. Typical hosts and
    /// <c>http://host:port/</c> URLs stay unquoted. Returns <see langword="null"/> when the selected
    /// shell cannot represent the value (NUL, CR/LF, or <c>%</c> on cmd.exe).
    /// </summary>
    internal static string? QuoteForShell(string value, CommandShell shell = CommandShell.Posix)
    {
        if (value.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
        {
            return null;
        }

        if (value.Length > 0 && IsSafeUnquoted(value))
        {
            return value;
        }

        return shell switch
        {
            CommandShell.Posix => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
            CommandShell.Cmd when value.Contains('%') => null,
            CommandShell.Cmd => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            _ => null,
        };
    }

    private static bool IsSafeUnquoted(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or ':' or '/' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Why the Models entry should show a spinner, or <see langword="null"/> when nothing is happening.
    /// A load shows as <see cref="CompanionLifecycle.Loading"/>; an unload only shows while its command file
    /// is still waiting on the console.
    /// </summary>
    public static string? ModelActivity(CompanionStatus status)
    {
        if (status.Lifecycle == CompanionLifecycle.Loading)
        {
            return "Loading model";
        }

        return status.CommandPending ? "Command pending (load or unload)" : null;
    }

    /// <summary>Whether a transfer is moving data, and how many more are waiting behind it.</summary>
    public static (bool Running, int Queued) TransferActivity(IReadOnlyList<Transfer> transfers)
    {
        var running = false;
        var queued = 0;
        foreach (var transfer in transfers)
        {
            if (transfer.State == TransferState.Queued)
            {
                queued++;
            }
            else if (transfer.IsActive)
            {
                running = true;
            }
        }

        return (running, queued);
    }

    /// <summary>
    /// Tooltip for the Downloads indicator. A running transfer may be downloading, verifying or uploading,
    /// so the label names the transfer rather than one phase.
    /// </summary>
    public static string? TransferActivityLabel(bool running, int queued)
    {
        if (running)
        {
            return queued > 0 ? $"Transfer in progress, {queued} queued" : "Transfer in progress";
        }

        return queued > 0 ? $"{queued} queued" : null;
    }

    public static Color TransferColor(TransferState state) => state switch
    {
        TransferState.Completed => Color.Success,
        TransferState.Failed => Color.Error,
        TransferState.Cancelled => Color.Default,
        _ => Color.Info,
    };

    /// <summary>The hub repository, or the console a direct copy is coming from.</summary>
    public static string TransferOrigin(Transfer transfer, IConsoleRegistry consoles)
    {
        if (transfer.SourceKind == TransferSourceKind.Console)
        {
            var name = consoles.Find(transfer.SourceConsoleId)?.Name;
            return string.IsNullOrWhiteSpace(name) ? "From another console" : $"From {name}";
        }

        return transfer.Repo;
    }

    /// <summary>Overall progress: acquiring the file counts for the first half and the upload for the second.</summary>
    public static double Progress(Transfer transfer)
    {
        if (transfer.State == TransferState.Completed)
        {
            return 100;
        }

        if (transfer.SizeBytes <= 0)
        {
            return 0;
        }

        var done = (transfer.DownloadedBytes + transfer.UploadedBytes) / (2.0 * transfer.SizeBytes);
        return Math.Clamp(done * 100, 0, 100);
    }
}
