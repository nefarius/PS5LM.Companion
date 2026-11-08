namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// Refuses a launch that would put two kstuffs on the kernel. Read fresh on every call: a payload can start or
/// stop between an upload and the launch that follows it.
/// </summary>
public static class KstuffGuard
{
    public static readonly string[] KstuffProcesses = ["kstuff.elf", "kstuff-lite.elf"];

    public static readonly string[] OnionProcesses = ["onion_daemon.elf", "onion_util.elf"];

    /// <summary>Null when <paramref name="payload"/> may be sent. Otherwise the reason it must not be.</summary>
    public static async Task<string?> RefusalAsync(
        ManifestPayload payload,
        SetupServices services,
        int ftpPort,
        CancellationToken cancellationToken)
    {
        var kind = Kind(payload.Id);
        if (kind == PayloadKind.Other)
        {
            return null;
        }

        var processes = await services.Manager.GetProcessesAsync(cancellationToken);
        if (processes is null)
        {
            return $"{payload.Name} was not launched: Payload Manager's process list could not be read, so it is unknown whether another kstuff is running.";
        }

        var kstuffRunning = Running(processes, KstuffProcesses);
        var onionRunning = Running(processes, OnionProcesses);
        if (kind == PayloadKind.Kstuff)
        {
            if (kstuffRunning)
            {
                return "kstuff-lite was not launched: a kstuff process is already running. A second kstuff panics the kernel.";
            }

            if (onionRunning && await BundledMayLoadAsync(services, ftpPort, cancellationToken))
            {
                return "kstuff-lite was not launched: OnionHEN is running and its bundled kstuff is not confirmed off. A second kstuff panics the kernel.";
            }

            return null;
        }

        if (kstuffRunning && await BundledMayLoadAsync(services, ftpPort, cancellationToken))
        {
            return "OnionHEN was not launched: kstuff is already running and OnionHEN's bundled kstuff is not confirmed off. Reboot the console, then run setup again.";
        }

        return null;
    }

    private static async Task<bool> BundledMayLoadAsync(SetupServices services, int ftpPort, CancellationToken cancellationToken)
    {
        var inventory = await services.Manager.ListPayloadsAsync(cancellationToken);
        var onion = inventory.FirstOrDefault(item =>
            item.Id?.Equals(BootSettings.OnionHenPayloadId, StringComparison.OrdinalIgnoreCase) == true
            || item.FileName.Contains("onion", StringComparison.OrdinalIgnoreCase)
            || (AutoloadListBuilder.PayloadId(item.Slot)?.Equals(BootSettings.OnionHenPayloadId, StringComparison.OrdinalIgnoreCase) == true));
        if (!BootSettings.SupportsBundledKstuffSwitch(onion))
        {
            return false;
        }

        if (!await services.Probe.PortOpenAsync(ftpPort, cancellationToken))
        {
            return true;
        }

        var text = await services.Files.ReadTextAsync(BootSettings.OnionConfigPath, 4 * 1024 * 1024, cancellationToken);
        return text is null
            || !OnionHenConfigEditor.TryGetValue(text, OnionHenConfigEditor.KstuffSection, OnionHenConfigEditor.KstuffAutoloadKey, out var value)
            || !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Running(IReadOnlySet<string> processes, IReadOnlyList<string> names) =>
        names.Any(name => processes.Contains(name) || processes.Any(process => process.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static PayloadKind Kind(string id)
    {
        if (id.Equals(BootSettings.KstuffPayloadId, StringComparison.OrdinalIgnoreCase))
        {
            return PayloadKind.Kstuff;
        }

        if (id.Equals(BootSettings.OnionHenPayloadId, StringComparison.OrdinalIgnoreCase))
        {
            return PayloadKind.OnionHen;
        }

        return PayloadKind.Other;
    }

    private enum PayloadKind
    {
        Other,
        Kstuff,
        OnionHen,
    }
}
