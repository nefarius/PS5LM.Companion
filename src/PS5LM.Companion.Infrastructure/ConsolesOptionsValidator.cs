using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.Infrastructure;

/// <summary>Rejects an unusable console list at startup, with the console id in every message.</summary>
public sealed class ConsolesOptionsValidator : IValidateOptions<ConsolesOptions>
{
    public ValidateOptionsResult Validate(string? name, ConsolesOptions options)
    {
        var failures = new List<string>();
        foreach (var (id, console) in options.Consoles)
        {
            failures.AddRange(ConsoleSettings.Errors(id.ToLowerInvariant(), console).Select(error => $"PS5:Consoles:{id}: {error}"));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
