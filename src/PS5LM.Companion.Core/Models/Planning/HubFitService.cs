using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Core.Models.Planning;

/// <summary>
/// Decides whether files from a model hub are likely to load. A file whose weights already exceed the budget
/// is rejected from its size. The others are planned from a GGUF header, a few at a time, and remembered by
/// content hash (or by repository and name when the hub has no hash).
/// </summary>
public sealed class HubFitService
{
    public const int MaxConcurrent = 2;

    private readonly IModelHub _hub;

    private readonly double _defaultBudgetGib;

    private readonly ConcurrentDictionary<string, HubFileFit> _cache = new(StringComparer.Ordinal);

    public HubFitService(IModelHub hub, double defaultBudgetGib = ModelBudget.CalibratedGib)
    {
        _hub = hub;
        _defaultBudgetGib = defaultBudgetGib > 0 && !double.IsNaN(defaultBudgetGib) && !double.IsInfinity(defaultBudgetGib)
            ? defaultBudgetGib
            : ModelBudget.CalibratedGib;
    }

    public ModelBudget AssumedBudget => ModelBudget.Assumed(_defaultBudgetGib);

    /// <summary>The budget for one console. A missing log, a missing installation or an unreachable console uses the assumption.</summary>
    public async Task<ModelBudget> ReadBudgetAsync(IPS5FileSystem fileSystem, StorageRoot? stateRoot, CancellationToken cancellationToken)
    {
        if (stateRoot is null)
        {
            return AssumedBudget;
        }

        try
        {
            var root = stateRoot.FtpPath.TrimEnd('/');
            var log = await fileSystem.ReadTextAsync($"{root}/app.log", ModelBudget.MaxLogBytes, cancellationToken);
            var settings = await fileSystem.ReadTextAsync($"{root}/{VariantProfile.SettingsFile}", AutoLoadSettingsService.MaxSettingsBytes, cancellationToken);
            return ModelBudget.Parse(log, settings, _defaultBudgetGib);
        }
        catch (ConsoleUnreachableException)
        {
            return AssumedBudget;
        }
    }

    public async IAsyncEnumerable<HubFileFit> CheckAsync(
        string repo,
        IReadOnlyList<HubFile> files,
        ModelBudget budget,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var loadable = files.Where(file => file.IsLoadableModel).ToList();
        var pending = new List<HubFile>();
        foreach (var file in loadable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SplitGguf.IsLaterPart(file.Name))
            {
                yield return HubFileFit.LaterPart(file.Name);
                continue;
            }

            var weight = SplitGguf.WeightBytes(file, loadable);
            if (ModelPlanner.WeightsAloneExceed(weight.Bytes, budget.Gib))
            {
                yield return HubFileFit.TooBig(file.Name, "too big: the weights alone need more than is free");
                continue;
            }

            if (!weight.Complete)
            {
                yield return HubFileFit.Unknown(file.Name, "not all parts of this split model are listed");
                continue;
            }

            pending.Add(file);
        }

        if (pending.Count == 0)
        {
            yield break;
        }

        var channel = Channel.CreateUnbounded<HubFileFit>();
        var gate = new SemaphoreSlim(MaxConcurrent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = pending.Select(file => CheckQueuedAsync(repo, file, loadable, budget, gate, channel.Writer, linked.Token)).ToArray();
        var completing = Task.WhenAll(tasks).ContinueWith(
            antecedent =>
            {
                _ = antecedent.Exception;
                channel.Writer.TryComplete();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var fit in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return fit;
            }
        }
        finally
        {
            await linked.CancelAsync();
            try
            {
                await completing;
            }
            finally
            {
                gate.Dispose();
            }
        }
    }

    private async Task CheckQueuedAsync(
        string repo,
        HubFile file,
        IReadOnlyList<HubFile> files,
        ModelBudget budget,
        SemaphoreSlim gate,
        ChannelWriter<HubFileFit> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var fit = await CheckHeaderAsync(repo, file, files, budget, cancellationToken);
                await writer.WriteAsync(fit, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The page moved on.
        }
        catch (Exception ex)
        {
            try
            {
                await writer.WriteAsync(HubFileFit.Unknown(file.Name, ex.Message), CancellationToken.None);
            }
            catch (ChannelClosedException)
            {
                // The page stopped reading.
            }
        }
    }

    private async Task<HubFileFit> CheckHeaderAsync(
        string repo,
        HubFile file,
        IReadOnlyList<HubFile> files,
        ModelBudget budget,
        CancellationToken cancellationToken)
    {
        var weight = SplitGguf.WeightBytes(file, files);
        if (!weight.Complete)
        {
            return HubFileFit.Unknown(file.Name, "not all parts of this split model are listed");
        }

        var bytes = weight.Bytes;
        var key = CacheKey(repo, file, bytes, budget);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached with { Name = file.Name };
        }

        try
        {
            await using var stream = await _hub.OpenHeaderAsync(repo, file.Name, cancellationToken);
            var info = await GgufHeader.ReadAsync(stream, file.Name, (ulong)Math.Max(0, bytes), cancellationToken);
            var fit = FromHeader(file.Name, info, budget);
            _cache.TryAdd(key, fit);
            return fit;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HubFileFit.Unknown(file.Name, ex.Message);
        }
    }

    private static HubFileFit FromHeader(string name, GgufModelInfo info, ModelBudget budget)
    {
        if (!info.Ok)
        {
            return HubFileFit.Unknown(name, info.Error ?? "The GGUF header could not be read.");
        }

        var plan = ModelPlanner.Plan(info, budget.Gib, budget.CtxCap, budget.ForcedKvType);
        return plan.Fits ? HubFileFit.Fits(name, plan) : HubFileFit.TooBig(name, plan.Why);
    }

    private static string CacheKey(string repo, HubFile file, long weightBytes, ModelBudget budget) =>
        string.Join(
            '\u001f',
            string.IsNullOrEmpty(file.Sha256) ? $"{repo}/{file.Name}" : file.Sha256,
            file.Name,
            weightBytes.ToString(CultureInfo.InvariantCulture),
            budget.Gib.ToString("R", CultureInfo.InvariantCulture),
            budget.CtxCap.ToString(CultureInfo.InvariantCulture),
            budget.KvType);
}
