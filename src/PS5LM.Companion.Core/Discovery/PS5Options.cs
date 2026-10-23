using System.ComponentModel.DataAnnotations;

using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Discovery;

/// <summary>How to reach the console. Bound from the <c>PS5</c> configuration section.</summary>
public sealed class PS5Options
{
    public const string SectionName = "PS5";

    /// <summary>The console's IP address or host name.</summary>
    [Required]
    public string Host { get; set; } = string.Empty;

    /// <summary>ftpsrv's port.</summary>
    [Range(1, 65535)]
    public int FtpPort { get; set; } = 2121;

    /// <summary>The app's llama-server port; closed until a model is loaded.</summary>
    [Range(1, 65535)]
    public int LlamaPort { get; set; } = 8081;

    /// <summary>ps5-exporter's port.</summary>
    [Range(1, 65535)]
    public int ExporterPort { get; set; } = 9100;

    /// <summary>Forces a variant; <see cref="PS5Variant.Unknown"/> detects it from the installed title folders.</summary>
    public PS5Variant Variant { get; set; } = PS5Variant.Unknown;

    /// <summary>Seconds between status refreshes.</summary>
    [Range(1, 300)]
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>How long an installation lookup is reused, in seconds.</summary>
    [Range(0, 3600)]
    public int DiscoveryCacheSeconds { get; set; } = 30;

    /// <summary>How long after a load request the lifecycle stays "loading" while port 8081 is still closed.</summary>
    [Range(1, 3600)]
    public int LoadGraceSeconds { get; set; } = 600;
}
