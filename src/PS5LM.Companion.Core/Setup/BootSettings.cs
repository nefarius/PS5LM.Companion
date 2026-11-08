using System.Text;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.Core.Setup;

/// <summary>Read-only boot-settings comparison, plus the report lines that are not themselves drift.</summary>
public sealed record BootSettingsOutcome(IReadOnlyList<SettingDrift> Drift, IReadOnlyList<ReportLine> Lines);

/// <summary>
/// The autoload list, Payload Manager countdown and OnionHEN bundled-kstuff switch that <c>setup</c> writes.
/// <c>update --settings</c> and the dashboard check the same rules and apply the same fix. A console with no
/// stored manifest payload is left alone. Extra autoload entries the operator added are kept.
/// </summary>
public static class BootSettings
{
    /// <summary>Shortest Payload Manager countdown setup leaves in place. A longer one already set is kept.</summary>
    public const int MinimumAutoloadDelaySeconds = 10;

    public const string KstuffPayloadId = "kstuff-lite";
    public const string OnionHenPayloadId = "onionhen";
    public const string OnionConfigPath = "/data/OnionHEN/config.ini";
    public const string NoKstuffPath = "/data/OnionHEN/no_kstuff";

    /// <summary>Payload Manager 0.6.0 is the first release whose <c>get_config</c> reports <c>AUTOLOAD_DELAY</c>.</summary>
    public static readonly Version AutoloadDelaySince = new(0, 6, 0);

    /// <summary>Shown when a fix is refused because FTP is in use or a model load is in progress.</summary>
    public const string BusyMessage = "A transfer or model load is running. Wait for it to finish before changing boot settings.";

    /// <summary>True while a transfer is active or a model load is in progress. Boot settings are left alone then.</summary>
    public static bool IsBusy(CompanionStatus status, bool transferActive) =>
        transferActive || status.CommandPending || status.Lifecycle == CompanionLifecycle.Loading;

    private const int MaxTextBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Compares the console with the recommendation. Older Payload Manager builds are not asked about the
    /// countdown, and an OnionHEN slot older than 0.9.0 is not asked about <c>[kstuff] autoload</c>. A missing
    /// config or a closed FTP port is reported as not confirmed and is not drift.
    /// </summary>
    public static async Task<BootSettingsOutcome> CheckAsync(
        SetupManifest manifest,
        SetupServices services,
        int ftpPort,
        CancellationToken cancellationToken)
    {
        var inventory = await services.Manager.ListPayloadsAsync(cancellationToken);
        var resolved = Resolve(manifest, inventory);
        if (resolved.Count == 0)
        {
            return new BootSettingsOutcome([], []);
        }

        var versionText = await services.Manager.GetVersionAsync(cancellationToken);
        var delayApi = PayloadManagerVersion.TryParse(versionText, out var version) && version >= AutoloadDelaySince;
        var autoload = delayApi ? await services.Manager.GetAutoloadAsync(cancellationToken) : null;
        var ftpOpen = await services.Probe.PortOpenAsync(ftpPort, cancellationToken);
        string? config = null;
        if (ftpOpen && manifest.Payloads.Any(payload => payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase)))
        {
            config = await services.Files.ReadTextAsync(OnionConfigPath, MaxTextBytes, cancellationToken);
        }

        return Inspect(manifest, resolved, autoload, delayApi, ftpOpen, config);
    }

    /// <summary>
    /// Switches bundled kstuff off first, then writes the autoload list from that result. Setup, <c>update --settings</c>
    /// and the dashboard fix all go through here, so the list cannot name kstuff-lite while the switch is unconfirmed.
    /// </summary>
    public static async Task ApplyAsync(
        SetupManifest manifest,
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        SetupServices services,
        int ftpPort,
        OnionConfigSession backup,
        Action<string, SetupStatus, string> report,
        CancellationToken cancellationToken,
        bool onlyWhenKstuffIsStored = false)
    {
        // setup edits the switch from the manifest, including when the upload did not stick.
        // update and the dashboard leave a console alone when kstuff-lite is not stored there.
        var stored = resolved.Any(item => item.Payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase));
        var kstuff = onlyWhenKstuffIsStored && !stored
            ? KstuffAutoload.Include
            : await ApplyKstuffAsync(manifest, resolved, services, ftpPort, backup, report, cancellationToken);
        await ApplyAutoloadAsync(resolved, services, report, cancellationToken, kstuff);
    }

    /// <summary>Writes the autoload list and countdown. An already matching list is left as it is.</summary>
    public static async Task ApplyAutoloadAsync(
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        SetupServices services,
        Action<string, SetupStatus, string> report,
        CancellationToken cancellationToken,
        KstuffAutoload kstuff = KstuffAutoload.Include)
    {
        if (resolved.Count == 0)
        {
            report("Autoload", SetupStatus.Warn, "No pinned payloads are stored, so the autoload list was not changed. Run setup to install them.");
            return;
        }

        var versionText = await services.Manager.GetVersionAsync(cancellationToken);
        if (PayloadManagerVersion.TryParse(versionText, out var version) && version < AutoloadDelaySince)
        {
            report("Autoload", SetupStatus.Warn, $"Payload Manager {versionText} does not store AUTOLOAD_DELAY, so the autoload list was not changed.");
            return;
        }

        var current = await services.Manager.GetAutoloadAsync(cancellationToken);
        var includeKstuff = kstuff switch
        {
            KstuffAutoload.Include => true,
            KstuffAutoload.Exclude => false,
            _ => ListHasPayload(current?.List, KstuffPayloadId),
        };
        var selected = includeKstuff
            ? resolved
            : resolved.Where(item => !item.Payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase)).ToList();
        var existing = includeKstuff ? current?.List : StripPayload(current?.List, KstuffPayloadId);
        var list = AutoloadListBuilder.Build(selected.Select(item => (item.Stored.LaunchTarget, item.Payload.DelayAfterMs)), existing);
        if (!AutoloadListBuilder.Fits(list))
        {
            report(
                "Autoload",
                SetupStatus.Fail,
                $"The autoload list is {Encoding.UTF8.GetByteCount(list)} bytes. Payload Manager accepts at most {AutoloadListBuilder.MaxListLength} bytes, so it was not saved. Shorten the existing autoload list and run setup again.");
            return;
        }

        if (DuplicateDetail(Parse(list)) is { } duplicates)
        {
            report(
                "Autoload",
                SetupStatus.Warn,
                "The autoload list still has duplicates: " + duplicates + " Remove the extra entries by hand and run again. The list was not saved.");
            return;
        }

        var delay = Math.Max(current?.DelaySeconds ?? 0, MinimumAutoloadDelaySeconds);
        if (current is { Enabled: true } && current.List == list && current.DelaySeconds == delay)
        {
            report("Autoload", SetupStatus.Ok, $"Enabled with {selected.Count} payloads after a {delay} s countdown.");
            return;
        }

        services.Ui.Info("Enabling autoload: " + list);
        await services.Manager.SetAutoloadAsync(true, list, delay, cancellationToken);
        var check = await services.Manager.GetAutoloadAsync(cancellationToken);
        if (check is { Enabled: true } && check.List == list && check.DelaySeconds == delay)
        {
            report("Autoload", SetupStatus.Ok, $"Enabled with {selected.Count} payloads after a {delay} s countdown.");
        }
        else
        {
            report("Autoload", SetupStatus.Warn, "Payload Manager did not report the saved autoload list back.");
        }
    }

    /// <summary>
    /// Switches off OnionHEN's bundled kstuff when the manifest ships kstuff-lite.
    /// <see cref="KstuffAutoload.Include"/> when the switch is confirmed off, the manifest has no external kstuff, or
    /// the stored OnionHEN is older than 0.9.0 and the key does not apply.
    /// <see cref="KstuffAutoload.Exclude"/> when <c>config.ini</c> is absent.
    /// <see cref="KstuffAutoload.Leave"/> when FTP is not answering, so a list that already names kstuff-lite is kept.
    /// </summary>
    public static async Task<KstuffAutoload> ApplyKstuffAsync(
        SetupManifest manifest,
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        SetupServices services,
        int ftpPort,
        OnionConfigSession backup,
        Action<string, SetupStatus, string> report,
        CancellationToken cancellationToken)
    {
        if (!manifest.Payloads.Any(payload => payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase)))
        {
            return KstuffAutoload.Include;
        }

        var onion = resolved.FirstOrDefault(item => item.Payload.Id.Equals(OnionHenPayloadId, StringComparison.OrdinalIgnoreCase)).Stored;
        if (!SupportsBundledKstuffSwitch(onion))
        {
            report("OnionHEN kstuff", SetupStatus.Ok, $"OnionHEN {SlotRevision(onion!.Slot)} does not read [kstuff] autoload, so bundled kstuff was left unchanged.");
            return KstuffAutoload.Include;
        }

        if (!await services.Probe.PortOpenAsync(ftpPort, cancellationToken))
        {
            report("OnionHEN kstuff", SetupStatus.Warn, $"ftpsrv is not answering, so {OnionConfigPath} was not checked. Bundled kstuff is not confirmed off.");
            return KstuffAutoload.Leave;
        }

        services.Ui.Info("Checking OnionHEN's bundled kstuff.");
        var text = await services.Files.ReadTextAsync(OnionConfigPath, MaxTextBytes, cancellationToken);
        if (text is null)
        {
            report("OnionHEN kstuff", SetupStatus.Warn, $"{OnionConfigPath} does not exist yet, so bundled kstuff is not confirmed off. Boot settings stay pending until OnionHEN has created the file.");
            return KstuffAutoload.Exclude;
        }

        var edit = OnionHenConfigEditor.SetValue(text, OnionHenConfigEditor.KstuffSection, OnionHenConfigEditor.KstuffAutoloadKey, "false");
        if (edit.Status != ConfigValueStatus.Unchanged)
        {
            await backup.WriteAsync(services.Files, text, edit.Text, cancellationToken);
        }

        await services.Files.WriteTextAsync(NoKstuffPath, string.Empty, cancellationToken);
        report(
            "OnionHEN kstuff",
            SetupStatus.Ok,
            edit.Status == ConfigValueStatus.Unchanged
                ? "Bundled kstuff is already off (/data/OnionHEN/no_kstuff)."
                : "Set [kstuff] autoload=false and wrote /data/OnionHEN/no_kstuff (backup: config.ini.bak).");
        return KstuffAutoload.Include;
    }

    /// <summary>
    /// False when the slot names an OnionHEN older than 0.9.0. A missing revision stays lenient and is treated as supported,
    /// because a <c>local/onionhen</c> slot does not say which build it is.
    /// </summary>
    public static bool SupportsBundledKstuffSwitch(ManagedPayload? onion)
    {
        if (onion is null || !TrySlotVersion(onion.Slot, out var version))
        {
            return true;
        }

        return version.Major > 0 || version.Minor >= 9;
    }

    internal static BootSettingsOutcome Inspect(
        SetupManifest manifest,
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        AutoloadConfig? autoload,
        bool delayApi,
        bool ftpOpen,
        string? onionConfig)
    {
        if (resolved.Count == 0)
        {
            return new BootSettingsOutcome([], []);
        }

        var drift = new List<SettingDrift>();
        var lines = new List<ReportLine>();
        var includeKstuff = RecommendKstuff(resolved, ftpOpen, onionConfig);
        var forList = includeKstuff
            ? resolved
            : resolved.Where(item => !item.Payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (delayApi && autoload is not null)
        {
            drift.AddRange(AutoloadDrift(forList, autoload));
            if (!includeKstuff && ListHasPayload(autoload.List, KstuffPayloadId))
            {
                drift.Add(new SettingDrift(
                    "autoload-kstuff",
                    "Autoload kstuff",
                    "kstuff-lite left out until bundled kstuff is confirmed off",
                    "kstuff-lite is in the autoload list while OnionHEN's bundled kstuff is not confirmed off. The next boot would load two kstuffs."));
            }
        }
        else if (!delayApi)
        {
            lines.Add(new ReportLine("Autoload", SetupStatus.Ok, "Payload Manager is older than 0.6.0, so AUTOLOAD_DELAY was not compared."));
        }
        else
        {
            lines.Add(new ReportLine("Autoload", SetupStatus.Warn, "Payload Manager did not report its autoload settings, so they were not compared."));
        }

        drift.AddRange(KstuffDrift(manifest, resolved, ftpOpen, onionConfig, lines));
        if (drift.Count == 0 && lines.Count == 0)
        {
            lines.Add(new ReportLine("Boot settings", SetupStatus.Ok, "Boot settings match the recommendation."));
        }

        return new BootSettingsOutcome(drift, lines);
    }

    internal static List<(ManifestPayload Payload, ManagedPayload Stored)> Resolve(SetupManifest manifest, IReadOnlyList<ManagedPayload> inventory)
    {
        var resolved = new List<(ManifestPayload, ManagedPayload)>();
        foreach (var payload in manifest.Payloads)
        {
            if (SetupRunner.FindStored(inventory, payload) is { } stored)
            {
                resolved.Add((payload, stored));
            }
        }

        return resolved;
    }

    private static IEnumerable<SettingDrift> AutoloadDrift(
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        AutoloadConfig autoload)
    {
        if (!autoload.Enabled)
        {
            yield return new SettingDrift("autoload-enabled", "Autoload", "on", "Autoload is off, so the list does not run at boot.");
        }

        var entries = Parse(autoload.List);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Slot is { } slot && IsOurs(slot, resolved, out var payload) && present.Add(payload.Id))
            {
                seen.Add(payload.Id);
            }
        }

        var missing = resolved.Where(item => !present.Contains(item.Payload.Id)).Select(item => item.Payload.Name).ToList();
        var expected = resolved.Where(item => present.Contains(item.Payload.Id)).Select(item => item.Payload.Id).ToList();
        if (missing.Count > 0 || !seen.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
        {
            var detail = missing.Count > 0
                ? string.Join(", ", missing) + (missing.Count == 1 ? " is" : " are") + " stored but not in the autoload list."
                : "Pinned payloads run as " + string.Join(", ", seen) + ".";
            yield return new SettingDrift("autoload-order", "Autoload order", string.Join(", ", resolved.Select(item => item.Payload.Id)), detail);
        }

        var shorts = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Slot is not { } slot || !IsOurs(slot, resolved, out var payload) || i == entries.Count - 1)
            {
                continue;
            }

            var required = Math.Max(payload.DelayAfterMs, AutoloadListBuilder.StepGapMs);
            var actual = entries[i + 1].PauseMs ?? 0;
            if (actual < required)
            {
                shorts.Add($"{payload.Name} waits {actual} ms and needs at least {required} ms");
            }
        }

        if (shorts.Count > 0)
        {
            yield return new SettingDrift("autoload-pause", "Autoload pauses", "the manifest pause after each pinned payload", string.Join("; ", shorts) + ".");
        }

        if (DuplicateDetail(entries) is { } duplicates)
        {
            yield return new SettingDrift("autoload-duplicate", "Autoload duplicates", "one copy of each payload", duplicates);
        }

        if (autoload.DelayReported && autoload.DelaySeconds < MinimumAutoloadDelaySeconds)
        {
            yield return new SettingDrift(
                "autoload-delay",
                "Autoload countdown",
                MinimumAutoloadDelaySeconds + " s",
                $"The countdown is {autoload.DelaySeconds} s. The recommendation is at least {MinimumAutoloadDelaySeconds} s.");
        }
    }

    private static IEnumerable<SettingDrift> KstuffDrift(
        SetupManifest manifest,
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        bool ftpOpen,
        string? onionConfig,
        List<ReportLine> lines)
    {
        if (!resolved.Any(item => item.Payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase)))
        {
            yield break;
        }

        var onion = resolved.FirstOrDefault(item => item.Payload.Id.Equals(OnionHenPayloadId, StringComparison.OrdinalIgnoreCase)).Stored;
        if (!SupportsBundledKstuffSwitch(onion))
        {
            lines.Add(new ReportLine("OnionHEN kstuff", SetupStatus.Ok, $"OnionHEN {SlotRevision(onion!.Slot)} does not read [kstuff] autoload, so it was not compared."));
            yield break;
        }

        if (!ftpOpen)
        {
            lines.Add(new ReportLine("OnionHEN kstuff", SetupStatus.Warn, $"ftpsrv is not answering, so {OnionConfigPath} was not checked. Bundled kstuff is not confirmed off."));
            yield break;
        }

        if (onionConfig is null)
        {
            lines.Add(new ReportLine("OnionHEN kstuff", SetupStatus.Warn, $"{OnionConfigPath} does not exist yet, so bundled kstuff is not confirmed off. Boot settings stay pending until OnionHEN has created the file."));
            yield break;
        }

        if (!OnionHenConfigEditor.TryGetValue(onionConfig, OnionHenConfigEditor.KstuffSection, OnionHenConfigEditor.KstuffAutoloadKey, out var value)
            || !value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            yield return new SettingDrift(
                "onionhen-kstuff",
                "OnionHEN bundled kstuff",
                "off",
                "OnionHEN still loads its bundled kstuff. [kstuff] autoload is not false.");
        }
    }

    /// <summary>
    /// False only when FTP answered and <c>config.ini</c> is missing, so the recommended list omits kstuff-lite.
    /// A closed FTP port leaves the recommendation unchanged: the file might already say the bundled copy is off.
    /// </summary>
    private static bool RecommendKstuff(
        IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved,
        bool ftpOpen,
        string? onionConfig)
    {
        if (!resolved.Any(item => item.Payload.Id.Equals(KstuffPayloadId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var onion = resolved.FirstOrDefault(item => item.Payload.Id.Equals(OnionHenPayloadId, StringComparison.OrdinalIgnoreCase)).Stored;
        if (!SupportsBundledKstuffSwitch(onion) || !ftpOpen)
        {
            return true;
        }

        return onionConfig is not null;
    }

    private static bool ListHasPayload(string? list, string payloadId) =>
        (list ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => !entry.StartsWith('!') && AutoloadListBuilder.PayloadId(entry) is { } id && id.Equals(payloadId, StringComparison.OrdinalIgnoreCase));

    private static string? StripPayload(string? list, string payloadId)
    {
        if (string.IsNullOrEmpty(list))
        {
            return list;
        }

        var kept = list
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => entry.StartsWith('!') || AutoloadListBuilder.PayloadId(entry) is not { } id || !id.Equals(payloadId, StringComparison.OrdinalIgnoreCase));
        return string.Join(',', kept);
    }

    private static string? DuplicateDetail(List<ListEntry> entries)
    {
        var byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var kstuff = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Slot is not { } slot || AutoloadListBuilder.PayloadId(slot) is not { } id)
            {
                continue;
            }

            if (!byId.TryGetValue(id, out var slots))
            {
                byId[id] = slots = [];
            }

            slots.Add(slot);
            if (id.Contains("kstuff", StringComparison.OrdinalIgnoreCase))
            {
                kstuff.Add(slot);
            }
        }

        var parts = new List<string>();
        foreach (var (id, slots) in byId)
        {
            if (slots.Count > 1)
            {
                parts.Add($"{id} is listed {slots.Count} times ({string.Join(", ", slots)})");
            }
        }

        var kstuffIds = kstuff.Select(AutoloadListBuilder.PayloadId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kstuffIds.Count > 1)
        {
            parts.Add("more than one kstuff is listed (" + string.Join(", ", kstuff) + ")");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }

    private static List<ListEntry> Parse(string list)
    {
        var entries = new List<ListEntry>();
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('!') && int.TryParse(raw[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pause) && pause >= 0)
            {
                entries.Add(new ListEntry(null, pause));
            }
            else if (!raw.StartsWith('!'))
            {
                entries.Add(new ListEntry(raw, null));
            }
        }

        return entries;
    }

    private static bool IsOurs(string slot, IReadOnlyList<(ManifestPayload Payload, ManagedPayload Stored)> resolved, out ManifestPayload payload)
    {
        foreach (var (item, stored) in resolved)
        {
            if (slot.Equals(stored.LaunchTarget, StringComparison.OrdinalIgnoreCase)
                || (AutoloadListBuilder.PayloadId(slot) is { } id && id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
            {
                payload = item;
                return true;
            }
        }

        payload = null!;
        return false;
    }

    private static bool TrySlotVersion(string? slot, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(slot))
        {
            return false;
        }

        var at = slot.LastIndexOf('@');
        return at >= 0 && at < slot.Length - 1 && SemanticVersion.TryParse(slot[(at + 1)..], out version);
    }

    private static string SlotRevision(string slot)
    {
        var at = slot.LastIndexOf('@');
        return at >= 0 && at < slot.Length - 1 ? slot[(at + 1)..] : slot;
    }

    private readonly record struct ListEntry(string? Slot, int? PauseMs);
}

/// <summary>Whether the autoload list may name kstuff-lite.</summary>
public enum KstuffAutoload
{
    /// <summary>Bundled kstuff is confirmed off, or this OnionHEN does not load one. The list names kstuff-lite.</summary>
    Include,

    /// <summary><c>config.ini</c> is absent, so OnionHEN would load its bundled copy. The list must not name kstuff-lite.</summary>
    Exclude,

    /// <summary>The config could not be read. A kstuff-lite entry that is already there stays; one that is not there is not added.</summary>
    Leave,
}

/// <summary>The first config.ini edit in a run keeps the previous file; a later edit does not replace that backup.</summary>
public sealed class OnionConfigSession
{
    private bool _backedUp;

    public async Task WriteAsync(IPS5FileSystem files, string previous, string updated, CancellationToken cancellationToken)
    {
        if (!_backedUp)
        {
            await files.WriteTextAsync(BootSettings.OnionConfigPath + ".bak", previous, cancellationToken);
            _backedUp = true;
        }

        await files.WriteTextAsync(BootSettings.OnionConfigPath, updated, cancellationToken);
    }
}
