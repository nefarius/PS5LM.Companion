using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>
/// Saves a console from the dashboard and the API. A rename is refused while work is still tied to the old id
/// in a way this companion cannot rewrite: a queued or running transfer, or a pool that comes from configuration.
/// </summary>
public sealed class ConsoleSettingsService(IConsoleRegistry registry, TransferService transfers, IInferencePoolSource pools)
{
    public ConsoleEditResult Update(string id, PS5Options options, string? newId = null)
    {
        var current = id.Trim().ToLowerInvariant();
        var target = string.IsNullOrWhiteSpace(newId) ? current : newId.Trim().ToLowerInvariant();
        if (!string.Equals(current, target, StringComparison.Ordinal))
        {
            if (transfers.HasActive(current))
            {
                return new ConsoleEditResult(ConsoleEditStatus.Conflict, "Cancel this console's transfers before renaming it.");
            }

            var blocking = pools.All
                .Where(pool => pool.Origin == InferencePoolOrigin.Configuration && Lists(pool, current))
                .Select(pool => pool.Alias)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (blocking.Count > 0)
            {
                return new ConsoleEditResult(
                    ConsoleEditStatus.Conflict,
                    $"Edit Inference:Pools before renaming this console. It is still listed by: {string.Join(", ", blocking)}.");
            }
        }

        return registry.Update(id, options, string.IsNullOrWhiteSpace(newId) ? null : newId);
    }

    private static bool Lists(InferencePool pool, string id) =>
        pool.Consoles.Any(console => string.Equals(console, id, StringComparison.OrdinalIgnoreCase));
}
