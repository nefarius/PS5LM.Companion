using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Inference;

namespace PS5LM.Companion.App.Inference;

public enum SelfTestKind
{
    /// <summary>A tiny non-streaming request: does each console answer with text?</summary>
    Check = 0,

    /// <summary>A fixed prompt, streamed: time to first token and tokens per second.</summary>
    Benchmark = 1,
}

public enum CheckOutcome
{
    Passed = 0,
    Failed = 1,

    /// <summary>The console was not eligible, so nothing was sent.</summary>
    Skipped = 2,
}

/// <summary>What one console did with the test request. Timings are milliseconds; unknown values are <see langword="null"/>.</summary>
public sealed record ConsoleCheck(
    string ConsoleId,
    CheckOutcome Outcome,
    string? Message,
    string? Reply,
    double? FirstByteMs,
    double? TimeToFirstTokenMs,
    double? TotalMs,
    int? PromptTokens,
    int? CompletionTokens,
    double? PromptTokensPerSecond,
    double? GenerationTokensPerSecond);

public sealed record SelfTestRun(string Alias, SelfTestKind Kind, DateTimeOffset StartedAt, double DurationMs, IReadOnlyList<ConsoleCheck> Consoles)
{
    /// <summary>True when at least one console passed and none failed. Skipped consoles do not count against it.</summary>
    public bool Passed => Consoles.Any(c => c.Outcome == CheckOutcome.Passed) && Consoles.All(c => c.Outcome != CheckOutcome.Failed);
}

public enum SelfTestStart
{
    Started = 0,
    UnknownAlias = 1,
    AlreadyRunning = 2,
}

/// <summary>
/// Sends real requests to each console of a pool to show whether the gateway can serve it and how fast.
/// Every request holds a normal lease, so it waits behind live traffic instead of overtaking it.
/// Results are kept in memory only.
/// </summary>
public sealed class InferenceSelfTest(InferenceRouter router, InferenceUpstream upstream)
{
    internal const string CheckPrompt = "Reply with the single word: ok";

    internal const string BenchmarkPrompt =
        "Write a short paragraph about why the sky is blue, mentioning Rayleigh scattering, the colours of sunset, " +
        "and how the atmosphere changes what we see during the day. Keep it plain and factual.";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BenchmarkTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Alias, SelfTestKind Kind), SelfTestRun> _last = new();

    public SelfTestRun? Last(string alias, SelfTestKind kind) =>
        _last.TryGetValue((alias.ToLowerInvariant(), kind), out var run) ? run : null;

    public async Task<(SelfTestStart Start, SelfTestRun? Run)> RunAsync(string alias, SelfTestKind kind, CancellationToken cancellationToken)
    {
        var pool = router.Snapshot().FirstOrDefault(p => p.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
        {
            return (SelfTestStart.UnknownAlias, null);
        }

        var gate = _gates.GetOrAdd(pool.Alias, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            return (SelfTestStart.AlreadyRunning, null);
        }

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            var checks = await Task.WhenAll(pool.Replicas.Select(replica => replica.Eligible
                ? TestConsoleAsync(pool.Alias, replica.ConsoleId, kind, cancellationToken)
                : Task.FromResult(Failure(replica.ConsoleId, CheckOutcome.Skipped, replica.Reason))));
            var run = new SelfTestRun(pool.Alias, kind, startedAt, clock.Elapsed.TotalMilliseconds, checks);
            _last[(pool.Alias.ToLowerInvariant(), kind)] = run;
            return (SelfTestStart.Started, run);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ConsoleCheck> TestConsoleAsync(string alias, string consoleId, SelfTestKind kind, CancellationToken cancellationToken)
    {
        var admission = await router.AcquireAsync(alias, cancellationToken, consoleId);
        if (admission.Lease is not { } lease)
        {
            return Failure(consoleId, admission.Failure == AdmissionFailure.NoReplica ? CheckOutcome.Skipped : CheckOutcome.Failed, admission.Message);
        }

        try
        {
            using var timeout = new CancellationTokenSource(kind == SelfTestKind.Benchmark ? BenchmarkTimeout : CheckTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                return await SendAsync(lease.Console, kind, linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return Failure(consoleId, CheckOutcome.Failed, "The console did not finish in time.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                return Failure(consoleId, CheckOutcome.Failed, ex.Message);
            }
        }
        finally
        {
            lease.Release();
        }
    }

    private async Task<ConsoleCheck> SendAsync(ConsoleRuntime console, SelfTestKind kind, CancellationToken cancellationToken)
    {
        var stream = kind == SelfTestKind.Benchmark;
        // Whole-number temperature as an integer: older PS5LM builds stop on "0.0".
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["model"] = "ps5",
            ["messages"] = new[] { new { role = "user", content = stream ? BenchmarkPrompt : CheckPrompt } },
            ["max_tokens"] = stream ? 64 : 8,
            ["temperature"] = 0,
            ["stream"] = stream,
            ["stream_options"] = new { include_usage = true },
        });
        var address = new UriBuilder(Uri.UriSchemeHttp, console.Host, console.Options.LlamaPort, "/v1/chat/completions").Uri;
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        var clock = Stopwatch.StartNew();
        using var response = await upstream.Invoker.SendAsync(request, cancellationToken);
        var firstByte = clock.Elapsed.TotalMilliseconds;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return Failure(console.Id, CheckOutcome.Failed, $"HTTP {(int)response.StatusCode}: {(detail.Length > 200 ? detail[..200] : detail)}") with { FirstByteMs = firstByte };
        }

        return stream
            ? await ReadStreamAsync(console.Id, response, clock, firstByte, cancellationToken)
            : await ReadJsonAsync(console.Id, response, clock, firstByte, cancellationToken);
    }

    private static async Task<ConsoleCheck> ReadJsonAsync(string consoleId, HttpResponseMessage response, Stopwatch clock, double firstByte, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var total = clock.Elapsed.TotalMilliseconds;
        var text = string.Empty;
        if (document.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message))
        {
            text = Text(message);
        }

        var (promptTokens, completionTokens) = Usage(document.RootElement);
        var answered = text.Length > 0;
        return new ConsoleCheck(
            consoleId,
            answered ? CheckOutcome.Passed : CheckOutcome.Failed,
            answered ? null : "The console answered without any text.",
            text.Length == 0 ? null : text,
            firstByte,
            null,
            total,
            promptTokens,
            completionTokens,
            null,
            null);
    }

    private static async Task<ConsoleCheck> ReadStreamAsync(string consoleId, HttpResponseMessage response, Stopwatch clock, double firstByte, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        var reply = new StringBuilder();
        double? firstToken = null;
        int? promptTokens = null, completionTokens = null;
        double? promptRate = null, generationRate = null;
        var done = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                done = true;
                break;
            }

            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta))
            {
                var piece = Text(delta);
                if (piece.Length > 0)
                {
                    firstToken ??= clock.Elapsed.TotalMilliseconds;
                    reply.Append(piece);
                }
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                (promptTokens, completionTokens) = Usage(root);
            }

            if (root.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
            {
                promptRate = Number(timings, "prompt_per_second");
                generationRate = Number(timings, "predicted_per_second");
            }
        }

        var total = clock.Elapsed.TotalMilliseconds;
        if (!done)
        {
            // The connection ended mid-answer: partial numbers would pass off a cut-off generation as a result.
            return Failure(consoleId, CheckOutcome.Failed, "The stream ended before the console finished ([DONE] missing).")
                with { Reply = reply.Length == 0 ? null : reply.ToString(), FirstByteMs = firstByte, TimeToFirstTokenMs = firstToken, TotalMs = total };
        }

        if (firstToken is null)
        {
            return Failure(consoleId, CheckOutcome.Failed, "The console streamed no text.") with { FirstByteMs = firstByte, TotalMs = total };
        }

        // llama-server reports its own rates; otherwise derive the generation rate from the stream.
        if (generationRate is null && completionTokens > 1 && total > firstToken)
        {
            generationRate = (completionTokens.Value - 1) / ((total - firstToken.Value) / 1000);
        }

        return new ConsoleCheck(consoleId, CheckOutcome.Passed, null, reply.ToString(), firstByte, firstToken, total, promptTokens, completionTokens, promptRate, generationRate);
    }

    /// <summary>The text of a message or delta; reasoning models may answer in <c>reasoning_content</c> first.</summary>
    private static string Text(JsonElement message)
    {
        foreach (var name in new[] { "content", "reasoning_content" })
        {
            if (message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static (int? Prompt, int? Completion) Usage(JsonElement root) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? (Integer(usage, "prompt_tokens"), Integer(usage, "completion_tokens"))
            : (null, null);

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static ConsoleCheck Failure(string consoleId, CheckOutcome outcome, string? message) =>
        new(consoleId, outcome, message, null, null, null, null, null, null, null, null);
}
