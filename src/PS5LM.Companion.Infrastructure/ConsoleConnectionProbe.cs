using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Infrastructure;

/// <summary>Runs each console check through the same connectors the companion uses, with a short timeout.</summary>
public sealed class ConsoleConnectionProbe(IConsoleConnectorFactory factory) : IConsoleProbe
{
    /// <summary>Id passed to the connector factory. It is not stored and does not name a console.</summary>
    public const string ProbeId = "probe";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(4);

    public async Task<ConsoleProbeReport> TestAsync(PS5Options options, CancellationToken cancellationToken)
    {
        var connectors = factory.Create(ProbeId, options);
        try
        {
            return new ConsoleProbeReport(
            [
                await Check("FTP", token => FtpAsync(connectors, options, token), cancellationToken),
                await Check("Payload Manager", token => PayloadManagerAsync(connectors, token), cancellationToken),
                await Check("elfldr", token => ElfldrAsync(connectors, options, token), cancellationToken),
                await Check("VoidShell", token => AppAsync(connectors, options.VoidShellPort, AppProviderKind.VoidShell, "VoidShell", token), cancellationToken),
                await Check("websrv", token => AppAsync(connectors, options.WebsrvPort, AppProviderKind.Websrv, "websrv", token), cancellationToken),
                await Check("ps5-exporter", token => ExporterAsync(connectors, token), cancellationToken),
                await Check("llama-server", token => LlamaAsync(connectors, token), cancellationToken),
            ]);
        }
        finally
        {
            (connectors.FileSystem as IDisposable)?.Dispose();
        }
    }

    private static async Task<ProbeCheck> FtpAsync(ConsoleConnectors connectors, PS5Options options, CancellationToken cancellationToken)
    {
        await connectors.FileSystem.ListAsync("/", cancellationToken);
        var installation = await InstallationDetector.DetectAsync(connectors.FileSystem, options.Variant, cancellationToken);
        if (installation.Profile is not { } profile)
        {
            return new ProbeCheck("FTP", ProbeOutcome.Fail, installation.Message ?? "FTP answered, but neither PS5LM nor PS5LM Plus is installed.");
        }

        var state = installation.StateRoot is null ? " Its state folder is not there yet." : string.Empty;
        return new ProbeCheck("FTP", ProbeOutcome.Pass, $"FTP answered. {profile.DisplayName} is installed.{state}");
    }

    private static async Task<ProbeCheck> PayloadManagerAsync(ConsoleConnectors connectors, CancellationToken cancellationToken)
    {
        var catalog = await connectors.Payloads.ReadAsync(cancellationToken);
        return catalog.Available
            ? new ProbeCheck("Payload Manager", ProbeOutcome.Pass, "Payload Manager answered.")
            : new ProbeCheck("Payload Manager", ProbeOutcome.Fail, catalog.Message ?? "Payload Manager did not answer.");
    }

    private static async Task<ProbeCheck> ElfldrAsync(ConsoleConnectors connectors, PS5Options options, CancellationToken cancellationToken)
    {
        var listening = await connectors.Elfldr.IsListeningAsync(cancellationToken);
        return listening
            ? new ProbeCheck("elfldr", ProbeOutcome.Pass, $"elfldr is accepting connections on port {options.ElfldrPort}.")
            : new ProbeCheck("elfldr", ProbeOutcome.Fail, $"elfldr is not accepting connections on port {options.ElfldrPort}.");
    }

    private static async Task<ProbeCheck> AppAsync(
        ConsoleConnectors connectors,
        int port,
        AppProviderKind kind,
        string name,
        CancellationToken cancellationToken)
    {
        if (port <= 0)
        {
            return new ProbeCheck(name, ProbeOutcome.Skipped, $"{name} is disabled.");
        }

        var provider = connectors.AppControl?.FirstOrDefault(item => item.Kind == kind);
        if (provider is null)
        {
            return new ProbeCheck(name, ProbeOutcome.Fail, $"{name} could not be checked.");
        }

        var snapshot = await provider.ProbeAsync(cancellationToken);
        return snapshot.Available
            ? new ProbeCheck(name, ProbeOutcome.Pass, $"{name} answered on port {port}.")
            : new ProbeCheck(name, ProbeOutcome.Fail, snapshot.Message ?? $"{name} did not answer on port {port}.");
    }

    private static async Task<ProbeCheck> ExporterAsync(ConsoleConnectors connectors, CancellationToken cancellationToken)
    {
        var sensors = await connectors.Sensors.ReadAsync(cancellationToken);
        return sensors is null
            ? new ProbeCheck("ps5-exporter", ProbeOutcome.Fail, "ps5-exporter is not reachable.")
            : new ProbeCheck("ps5-exporter", ProbeOutcome.Pass, "ps5-exporter answered.");
    }

    private static async Task<ProbeCheck> LlamaAsync(ConsoleConnectors connectors, CancellationToken cancellationToken)
    {
        var probe = await connectors.Llama.ProbeAsync(cancellationToken);
        return probe.State switch
        {
            LlamaServerState.Closed => new ProbeCheck("llama-server", ProbeOutcome.Skipped, "Closed until a model is loaded."),
            LlamaServerState.Loading => new ProbeCheck("llama-server", ProbeOutcome.Pass, "llama-server is loading a model."),
            _ => new ProbeCheck("llama-server", ProbeOutcome.Pass, probe.LoadedModel is { } model
                ? $"llama-server is serving {model}."
                : "llama-server is serving a model."),
        };
    }

    private static async Task<ProbeCheck> Check(string name, Func<CancellationToken, Task<ProbeCheck>> action, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            return await action(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeCheck(name, ProbeOutcome.Fail, "Timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeCheck(name, ProbeOutcome.Fail, string.IsNullOrWhiteSpace(ex.Message) ? "The check failed." : ex.Message);
        }
    }
}
