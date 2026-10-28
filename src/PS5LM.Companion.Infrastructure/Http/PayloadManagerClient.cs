using System.Net;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

using Refit;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>
/// Reads and launches through Payload Manager. A manager that does not answer is a status result.
/// Caller cancellation still propagates, including a timeout that belongs to the caller.
/// </summary>
public sealed class PayloadManagerClient(IPayloadManagerApi api) : IPayloadCatalog, IPayloadLauncher
{
    public async Task<PayloadCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var list = await api.ListPayloadsAsync(cancellationToken);
            if (list.Error is not null && list.StatusCode is null || !list.IsSuccessStatusCode || list.Content is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PayloadCatalogSnapshot.Unreachable(TransportMessage(list.Error, list.StatusCode));
            }

            var installed = (list.Content.Items ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Slot))
                .Select(item => new InstalledPayload(item.Id!, item.Slot!, string.IsNullOrWhiteSpace(item.Name) ? item.Id! : item.Name!))
                .ToList();

            var recorded = new HashSet<string>(StringComparer.Ordinal);
            var history = await TryAsync(api.HistoryAsync, cancellationToken);
            RecordHistory(recorded, installed, history?.History);
            RecordAutoload(recorded, await TryAsync(api.AutoloadAsync, cancellationToken));
            var processes = await TryAsync(api.ProcessesAsync, cancellationToken);
            var running = processes?.Processes is null
                ? null
                : processes.Processes
                    .Where(process => !string.IsNullOrWhiteSpace(process.Name))
                    .Select(process => process.Name!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new PayloadCatalogSnapshot(true, true, null, installed, recorded, running);
        }
        catch (Exception ex)
        {
            ThrowIfCallerCancelled(ex, cancellationToken);
            return PayloadCatalogSnapshot.Unreachable($"Payload Manager did not answer: {ex.Message}");
        }
    }

    public async Task<PayloadLaunchResult> LaunchAsync(string slot, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await api.LoadPayloadAsync(slot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var body = string.IsNullOrWhiteSpace(response.Content) ? (response.Error as ApiException)?.Content : response.Content;
            if (response.IsSuccessStatusCode && string.Equals(body?.Trim(), "OK", StringComparison.Ordinal))
            {
                return new PayloadLaunchResult(true, (int)response.StatusCode!, "OK");
            }

            var detail = string.IsNullOrWhiteSpace(body)
                ? $"Payload Manager returned {(int?)response.StatusCode ?? 0}."
                : body.Trim();
            return new PayloadLaunchResult(false, (int?)response.StatusCode, detail);
        }
        catch (Exception ex)
        {
            ThrowIfCallerCancelled(ex, cancellationToken);
            return new PayloadLaunchResult(false, null, $"Payload Manager did not answer: {ex.Message}");
        }
    }

    private static async Task<T?> TryAsync<T>(Func<CancellationToken, Task<IApiResponse<T>>> call, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await call(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return response.IsSuccessStatusCode ? response.Content : null;
        }
        catch (Exception ex)
        {
            ThrowIfCallerCancelled(ex, cancellationToken);
            return null;
        }
    }

    private static void RecordHistory(HashSet<string> recorded, IReadOnlyList<InstalledPayload> installed, IReadOnlyList<string>? history)
    {
        if (history is null)
        {
            return;
        }

        foreach (var payload in installed)
        {
            var needle = "/" + payload.Slot.Trim('/') + "/";
            if (history.Any(path => path.Contains(needle, StringComparison.Ordinal)))
            {
                recorded.Add(payload.Slot);
            }
        }
    }

    private static void RecordAutoload(HashSet<string> recorded, PayloadAutoloadResponse? autoload)
    {
        if (autoload?.List is null)
        {
            return;
        }

        var slots = autoload.List.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outcomes = autoload.Outcomes ?? string.Empty;
        for (var i = 0; i < slots.Length && i < outcomes.Length; i++)
        {
            if (outcomes[i] == 's')
            {
                recorded.Add(slots[i]);
            }
        }
    }

    private static string TransportMessage(Exception? error, HttpStatusCode? status) =>
        status is { } code
            ? $"Payload Manager did not answer: it is running but returned HTTP {(int)code}{BodyHint(error)}."
            : error is null ? "Payload Manager returned no status." : $"Payload Manager did not answer: {error.Message}";

    /// <summary>The manager's own error text, when it sent one: it usually names the cause of a 500.</summary>
    private static string BodyHint(Exception? error)
    {
        var body = (error as ApiException)?.Content?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        body = body.ReplaceLineEndings(" ");
        return ": " + (body.Length > 200 ? body[..200] + "..." : body);
    }

    private static void ThrowIfCallerCancelled(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The payload check was cancelled.", exception, cancellationToken);
        }
    }
}
