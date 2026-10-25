using System.ComponentModel.DataAnnotations;

using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>Checks one console's id and options. Messages name the problem, not the configuration key.</summary>
public static class ConsoleSettings
{
    public static IReadOnlyList<string> Errors(string id, PS5Options options)
    {
        var failures = new List<string>();
        if (!ConsoleIds.IsValid(id))
        {
            failures.Add("The id must be 1-40 lowercase letters, digits or hyphens, and not start or end with a hyphen.");
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
        {
            failures.AddRange(results.Select(result => result.ErrorMessage ?? "Invalid value."));
        }

        if (!string.IsNullOrWhiteSpace(options.PayloadManagerUrl)
            && !(Uri.TryCreate(options.PayloadManagerUrl.Trim(), UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            failures.Add("PayloadManagerUrl must be an absolute http or https URL.");
        }

        return failures;
    }
}
