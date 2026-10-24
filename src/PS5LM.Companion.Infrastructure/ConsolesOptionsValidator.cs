using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.Infrastructure;

/// <summary>Rejects an unusable console list at startup, with the console id in every message.</summary>
public sealed class ConsolesOptionsValidator : IValidateOptions<ConsolesOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsolesOptions options)
    {
        var failures = new List<string>();
        if (options.Consoles.Count == 0)
        {
            failures.Add("Configure at least one console, for example PS5__Consoles__ps5__Host=192.168.1.50.");
        }

        foreach (var (id, console) in options.Consoles)
        {
            if (!ConsoleIds.IsValid(id.ToLowerInvariant()))
            {
                failures.Add($"PS5:Consoles:{id}: the id must be 1-40 lowercase letters, digits or hyphens, and not start or end with a hyphen.");
            }

            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(console, new ValidationContext(console), results, validateAllProperties: true))
            {
                failures.AddRange(results.Select(r => $"PS5:Consoles:{id}: {r.ErrorMessage}"));
            }

            if (!string.IsNullOrWhiteSpace(console.PayloadManagerUrl)
                && !(Uri.TryCreate(console.PayloadManagerUrl.Trim(), UriKind.Absolute, out var uri)
                     && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            {
                failures.Add($"PS5:Consoles:{id}: PayloadManagerUrl must be an absolute http or https URL.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultConsoleId) && !options.Consoles.ContainsKey(options.DefaultConsoleId))
        {
            failures.Add($"PS5:DefaultConsoleId '{options.DefaultConsoleId}' is not one of the configured consoles.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
