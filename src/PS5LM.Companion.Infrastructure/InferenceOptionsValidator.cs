using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Inference;

namespace PS5LM.Companion.Infrastructure;

/// <summary>Rejects an unusable inference gateway configuration at startup.</summary>
public sealed partial class InferenceOptionsValidator(IOptions<ConsolesOptions> consoles) : IValidateOptions<InferenceOptions>
{
    public ValidateOptionsResult Validate(string? name, InferenceOptions options)
    {
        var failures = new List<string>();
        var known = new HashSet<string>(consoles.Value.Consoles.Keys.Select(k => k.ToLowerInvariant()), StringComparer.Ordinal);

        if (options.MaxConcurrentPerConsole is < 1 or > 64)
        {
            failures.Add("Inference:MaxConcurrentPerConsole must be between 1 and 64.");
        }

        if (options.MaxQueuedRequests is < 0 or > 10_000)
        {
            failures.Add("Inference:MaxQueuedRequests must be between 0 and 10000.");
        }

        if (options.QueueTimeoutSeconds is < 1 or > 3600)
        {
            failures.Add("Inference:QueueTimeoutSeconds must be between 1 and 3600.");
        }

        if (options.UpstreamTimeoutSeconds is < 1 or > 86_400)
        {
            failures.Add("Inference:UpstreamTimeoutSeconds must be between 1 and 86400.");
        }

        if (options.MaxRequestBodyMegabytes is < 1 or > 512)
        {
            failures.Add("Inference:MaxRequestBodyMegabytes must be between 1 and 512.");
        }

        if (options.AffinityMinutes is < 0 or > 1440)
        {
            failures.Add("Inference:AffinityMinutes must be between 0 and 1440.");
        }

        foreach (var (alias, pool) in options.Pools)
        {
            if (!AliasPattern().IsMatch(alias))
            {
                failures.Add($"Inference:Pools:{alias}: the alias must be 1-100 letters, digits, '.', '_' or '-', starting with a letter or digit.");
            }

            if (string.IsNullOrWhiteSpace(pool.Model))
            {
                failures.Add($"Inference:Pools:{alias}: Model is required (a file name such as qwen.gguf, or the full path llama-server reports).");
            }

            if (pool.Consoles.Count == 0)
            {
                failures.Add($"Inference:Pools:{alias}: list at least one console id under Consoles.");
            }

            foreach (var error in InferencePoolRules.Errors(alias, pool.Model, pool.Consoles, _ => true, pool.AlternateNames).Where(e => e.StartsWith("The alternate name", StringComparison.Ordinal)))
            {
                failures.Add($"Inference:Pools:{alias}: {error}");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in pool.Consoles.Select(c => (c ?? string.Empty).ToLowerInvariant()))
            {
                if (!known.Contains(id))
                {
                    failures.Add($"Inference:Pools:{alias}: console '{id}' is not configured under PS5:Consoles.");
                }
                else if (!seen.Add(id))
                {
                    failures.Add($"Inference:Pools:{alias}: console '{id}' is listed twice.");
                }
            }
        }

        failures.AddRange(InferencePoolNames.Conflicts(options.Pools.Select(p => (p.Key, (IReadOnlyList<string>)p.Value.AlternateNames))).Select(e => $"Inference:Pools: {e}"));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$")]
    private static partial Regex AliasPattern();
}
