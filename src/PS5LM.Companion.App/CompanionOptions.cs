using System.ComponentModel.DataAnnotations;

namespace PS5LM.Companion.App;

/// <summary>Settings of the companion itself (not of the console). Bound from the <c>Companion</c> section.</summary>
public sealed class CompanionOptions
{
    public const string SectionName = "Companion";

    /// <summary>Where the companion keeps its own state: model cache and transfer metadata.</summary>
    [Required]
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Shared secret for the dashboard and the REST API. Required when the companion listens on anything but
    /// loopback, unless <see cref="AllowAnonymous"/> is set.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Explicitly allows an unauthenticated dashboard on the LAN (for trusted networks only).</summary>
    public bool AllowAnonymous { get; set; }
}
