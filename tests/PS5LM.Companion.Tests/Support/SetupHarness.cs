using System.IO.Compression;
using System.Text;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Tests.Support;

/// <summary>A console that only runs Payload Manager, simulated well enough to drive <see cref="SetupRunner"/>.</summary>
internal sealed class SetupHarness : IDisposable
{
    public const string TitleId = "PPSA99581";
    public const string ParamV1 = "{\"titleId\":\"PPSA99581\",\"v\":1}";

    private static readonly Dictionary<string, (int Port, string? Process)> Effects = new()
    {
        ["elfldr.elf"] = (9021, null),
        ["kstuff-lite.elf"] = (0, "kstuff.elf"),
        ["onionhen.elf"] = (0, "onion_daemon.elf"),
        ["ftpsrv.elf"] = (2121, "ftpsrv.elf"),
        ["shadowmountplus.elf"] = (0, "shadowmountplus.elf"),
        ["voidshell.elf"] = (7007, null),
        ["ps5-web-file-manager.elf"] = (8888, null),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("setup-tests").FullName;

    /// <summary>What a first OnionHEN start writes before setup has edited it.</summary>
    public const string OnionCreatedConfig = "[app_jailbreak]\nexact_title_ids =\n";

    /// <summary>When OnionHEN creates <c>config.ini</c>. The default writes it as the payload starts.</summary>
    public OnionConfigCreation ConfigCreation { get; set; } = OnionConfigCreation.WhenLaunched;

    /// <summary>True when <c>config.ini</c> was already on the console at the moment OnionHEN was sent.</summary>
    public bool? ConfigExistedBeforeOnionLaunch { get; private set; }

    /// <summary>Live kstuffs. kstuff-lite and OnionHEN's bundled copy each add one.</summary>
    public int KstuffCount { get; set; }

    public List<LoadKstuff> Loads { get; } = [];

    private int? _configReadsRemaining;

    public SetupHarness()
    {
        Manager = new FakeManager(this);
        Probe = new FakeProbe(this);
        Artifacts = new FakeStore(this);
        Elfldr = new FakeElfldr(this);
        VoidShell = new FakeAppProvider(AppProviderKind.VoidShell);
        Files = new FakeFileSystem();
        Files.BeforeRead = OnRead;
    }

    /// <summary>A console with nothing but Payload Manager. OnionHEN has never written a config.</summary>
    public static SetupHarness Fresh() => new();

    /// <summary>A console that already has OnionHEN's config, the way the older tests started.</summary>
    public static SetupHarness Prepared(string config) 
    {
        var harness = new SetupHarness();
        harness.Files.AddFile(BootSettings.OnionConfigPath, config);
        return harness;
    }

    public HashSet<int> Ports { get; } = [8084];

    public HashSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Version { get; set; } = "0.6.3";

    public bool LegacyList => Version.StartsWith("0.5", StringComparison.Ordinal);

    public List<ManagedPayload> Stored { get; } = [];

    public List<string> Events { get; } = [];

    /// <summary>Launch targets passed to <see cref="IPayloadManagerAdmin.LoadAsync"/>, in order.</summary>
    public List<string> LoadTargets { get; } = [];

    public AutoloadConfig Autoload { get; set; } = new(false, string.Empty);

    /// <summary>Makes <c>/processes_list</c> unreadable (the fake answers null).</summary>
    public bool ProcessListUnreadable { get; set; }

    /// <summary>Running payload processes by pid, with the payload file each one runs.</summary>
    public Dictionary<int, string> Pids { get; } = [];

    /// <summary>The payload file a pid runs, when its process name is the loader's generic <c>payload.elf</c>.</summary>
    public Dictionary<int, string> PidPayloads { get; } = [];

    /// <summary>A kill that succeeds but leaves the payload answering, as when another copy holds the port.</summary>
    public bool KillsAreIneffective { get; set; }

    /// <summary><see cref="IPayloadManagerAdmin.GetProcessIdsAsync"/> answers null while the name list still works.</summary>
    public bool ProcessIdsUnreadable { get; set; }

    /// <summary>
    /// When a launched payload has a process name, drop the pids that already use it and record this new one.
    /// Leave unset when the launch should not change the process list.
    /// </summary>
    public int? HandoffPid { get; set; }

    /// <summary>A launch drops the payload's port instead of opening it (the new copy never starts listening).</summary>
    public bool DropPortOnLoad { get; set; }

    public HashSet<string> LoadFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upload names Payload Manager refuses, as it does for a payload file that is in use.</summary>
    public HashSet<string> UploadFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Uploads the manager accepts but then does not list, so the payload never resolves.</summary>
    public HashSet<string> UploadDropped { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Consent { get; set; }

    public bool CanPrompt { get; set; }

    public List<string> Messages { get; } = [];

    public FakeManager Manager { get; }

    public FakeProbe Probe { get; }

    public FakeStore Artifacts { get; }

    public FakeElfldr Elfldr { get; }

    public FakeAppProvider VoidShell { get; }

    public FakeFileSystem Files { get; }

    public SetupManifest Manifest { get; set; } = SetupManifest.LoadEmbedded();

    public static SetupOptions Options(string? app = "ps5lmplus") => new("ps5.test", app) { WaitOverride = TimeSpan.FromMilliseconds(2), UpgradePayloadManager = false };

    public SetupRunner Runner(SetupOptions options) => new(
        Manifest,
        options,
        new SetupServices(Manager, Artifacts, Files, Probe, Elfldr, VoidShell, new FakeUi(this), TimeProvider.System));

    public void Dispose()
    {
        try
        {
            KstuffSafety.AssertLaunches(this);
        }
        finally
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public bool BundledWouldLoad(string? slot)
    {
        if (!BootSettings.SupportsBundledKstuffSwitch(slot is null ? null : new ManagedPayload(slot, "onionhen", "onionhen.elf", null)))
        {
            return false;
        }

        var text = Files.Peek(BootSettings.OnionConfigPath);
        return text is null
            || !OnionHenConfigEditor.TryGetValue(text, OnionHenConfigEditor.KstuffSection, OnionHenConfigEditor.KstuffAutoloadKey, out var value)
            || !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replays the saved autoload list against the saved config, the way the next boot would.</summary>
    public BootSimulation SimulateBoot()
    {
        var kstuff = 0;
        var conflict = false;
        var started = new List<string>();
        foreach (var raw in Autoload.List.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith('!'))
            {
                continue;
            }

            var id = AutoloadListBuilder.PayloadId(raw);
            if (id is null)
            {
                continue;
            }

            started.Add(id);
            if (id.Contains("kstuff", StringComparison.OrdinalIgnoreCase))
            {
                if (kstuff > 0)
                {
                    conflict = true;
                }

                kstuff++;
            }
            else if (id.Equals(BootSettings.OnionHenPayloadId, StringComparison.OrdinalIgnoreCase))
            {
                var stored = Stored.FirstOrDefault(item => item.FileName.Equals("onionhen.elf", StringComparison.OrdinalIgnoreCase));
                var versionSlot = raw.Contains('@', StringComparison.Ordinal) ? raw : stored?.Slot ?? raw;
                if (BundledWouldLoad(versionSlot))
                {
                    if (kstuff > 0)
                    {
                        conflict = true;
                    }

                    kstuff++;
                }
            }
        }

        return new BootSimulation(kstuff, conflict, started);
    }

    private void OnRead(string path)
    {
        if (!path.Equals(BootSettings.OnionConfigPath, StringComparison.Ordinal) || _configReadsRemaining is not int remaining)
        {
            return;
        }

        if (remaining > 1)
        {
            _configReadsRemaining = remaining - 1;
            return;
        }

        _configReadsRemaining = null;
        if (!Files.Has(path))
        {
            Files.AddFile(path, OnionCreatedConfig);
        }
    }

    private void NoteKstuffLoad(string fileName, string? slot, bool kstuffAlready)
    {
        var before = kstuffAlready ? Math.Max(KstuffCount, 1) : 0;
        var bundled = false;
        if (fileName.Equals("kstuff-lite.elf", StringComparison.OrdinalIgnoreCase))
        {
            if (kstuffAlready)
            {
                Events.Add("kstuff-conflict");
            }

            KstuffCount = kstuffAlready ? KstuffCount + 1 : 1;
        }
        else if (fileName.Equals("onionhen.elf", StringComparison.OrdinalIgnoreCase))
        {
            ConfigExistedBeforeOnionLaunch = Files.Has(BootSettings.OnionConfigPath);
            if (BundledWouldLoad(slot))
            {
                bundled = true;
                if (kstuffAlready)
                {
                    Events.Add("kstuff-conflict");
                }

                KstuffCount = kstuffAlready ? KstuffCount + 1 : 1;
                Processes.Add("kstuff.elf");
            }

            switch (ConfigCreation)
            {
                case OnionConfigCreation.WhenLaunched when !Files.Has(BootSettings.OnionConfigPath):
                    Files.AddFile(BootSettings.OnionConfigPath, OnionCreatedConfig);
                    break;
                case OnionConfigCreation.OnSecondRead when !Files.Has(BootSettings.OnionConfigPath):
                    // The decision already observed the missing file. The next read, from the wait, is when it appears.
                    _configReadsRemaining = 1;
                    break;
            }
        }

        Loads.Add(new LoadKstuff(fileName, before, bundled));
    }

    internal sealed class FakeUi(SetupHarness owner) : ISetupUi
    {
        public bool CanPrompt => owner.CanPrompt;

        public void Info(string message) => owner.Messages.Add(message);

        public bool Confirm(string question) => owner.Consent;
    }

    internal sealed class FakeManager(SetupHarness owner) : IPayloadManagerAdmin
    {
        public Task<string?> GetVersionAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(owner.Ports.Contains(8084) ? owner.Version : null);

        public Task<IReadOnlyList<ManagedPayload>> ListPayloadsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ManagedPayload>>(owner.Stored.ToList());

        public async Task UploadAsync(string fileName, Stream content, CancellationToken cancellationToken)
        {
            if (owner.UploadFails.Contains(fileName))
            {
                throw new IOException($"Payload Manager refused the upload of {fileName} (500).");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            owner.Events.Add("upload:" + fileName);
            if (owner.UploadDropped.Contains(fileName))
            {
                return;
            }

            owner.Stored.RemoveAll(p => p.FileName == fileName);
            if (!fileName.StartsWith("pldmgr", StringComparison.Ordinal))
            {
                var slot = owner.LegacyList ? string.Empty : "local/" + Path.GetFileNameWithoutExtension(fileName);
                owner.Stored.Add(new ManagedPayload(slot, null, fileName, "/data/pldmgr/payloads/" + fileName));
            }
        }

        public Task<PayloadLaunchResult> LoadAsync(string target, CancellationToken cancellationToken)
        {
            var stored = owner.Stored.FirstOrDefault(p => p.LaunchTarget == target);
            if (stored is null || owner.LoadFails.Contains(stored.FileName))
            {
                return Task.FromResult(new PayloadLaunchResult(false, 500, "Failed to launch payload"));
            }

            owner.Events.Add("load:" + stored.FileName);
            owner.LoadTargets.Add(target);
            var kstuffAlready = owner.Processes.Contains("kstuff.elf") || owner.Processes.Contains("kstuff-lite.elf");
            if (Effects.TryGetValue(stored.FileName, out var effect))
            {
                if (effect.Port > 0)
                {
                    if (owner.DropPortOnLoad)
                    {
                        owner.Ports.Remove(effect.Port);
                    }
                    else
                    {
                        owner.Ports.Add(effect.Port);
                    }
                }

                if (effect.Process is not null)
                {
                    owner.Processes.Add(effect.Process);
                    if (owner.HandoffPid is int next)
                    {
                        foreach (var pid in owner.Pids.Where(p => p.Value.Equals(effect.Process, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
                        {
                            owner.Pids.Remove(pid);
                        }

                        owner.Pids[next] = effect.Process;
                    }
                }
            }

            owner.NoteKstuffLoad(stored.FileName, stored.Slot, kstuffAlready);
            return Task.FromResult(new PayloadLaunchResult(true, 200, "OK"));
        }

        public Task<IReadOnlySet<string>?> GetProcessesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>?>(owner.ProcessListUnreadable ? null : owner.Processes.ToHashSet(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyDictionary<int, string>?> GetProcessIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, string>?>(owner.ProcessIdsUnreadable ? null : owner.Pids.ToDictionary(p => p.Key, p => p.Value));

        public Task<bool> KillProcessAsync(int pid, CancellationToken cancellationToken)
        {
            if (!owner.Pids.Remove(pid, out var fileName))
            {
                return Task.FromResult(false);
            }

            owner.Events.Add("kill:" + pid);
            if (!owner.KillsAreIneffective && Effects.TryGetValue(owner.PidPayloads.GetValueOrDefault(pid, fileName), out var effect))
            {
                owner.Ports.Remove(effect.Port);
            }

            return Task.FromResult(true);
        }

        public Task<AutoloadConfig?> GetAutoloadAsync(CancellationToken cancellationToken) => Task.FromResult<AutoloadConfig?>(owner.Autoload);

        public Task SetAutoloadAsync(bool enabled, string list, int delaySeconds, CancellationToken cancellationToken)
        {
            owner.Autoload = new AutoloadConfig(enabled, list, delaySeconds, DelayReported: true);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            owner.Events.Add("shutdown");
            owner.Ports.Remove(8084);
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeProbe(SetupHarness owner) : IConsoleProbe
    {
        public bool DashboardServed { get; set; } = true;

        public List<string> Posts { get; } = [];

        public Task<bool> PostAsync(int port, string path, CancellationToken cancellationToken)
        {
            Posts.Add($"{port}{path}");
            return Task.FromResult(owner.Ports.Contains(port));
        }

        public Task<bool> PortOpenAsync(int port, CancellationToken cancellationToken) => Task.FromResult(owner.Ports.Contains(port));

        public Task<bool> HtmlAsync(int port, string path, CancellationToken cancellationToken) =>
            Task.FromResult(owner.Ports.Contains(port) && DashboardServed);
    }

    internal sealed class FakeElfldr(SetupHarness owner) : IElfldrSender
    {
        public Task SendAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!owner.Ports.Contains(9021))
            {
                throw new IOException("elfldr is not listening.");
            }

            owner.Events.Add("elfldr-send:" + Path.GetFileName(filePath));
            owner.Ports.Add(8084);
            owner.Version = "0.6.3";
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeStore(SetupHarness owner) : IArtifactStore
    {
        public static readonly byte[] DashboardBytes = Encoding.UTF8.GetBytes("<html>dashboard</html>");

        public List<string> Fetched { get; } = [];

        public Task<string> FetchAsync(string url, string sha256, CancellationToken cancellationToken)
        {
            Fetched.Add(url);
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            var path = Path.Combine(owner._directory, name);
            if (name.EndsWith(".zip", StringComparison.Ordinal))
            {
                File.WriteAllBytes(path, AppZip(ParamV1));
            }
            else if (name == "index.html")
            {
                File.WriteAllBytes(path, DashboardBytes);
            }
            else
            {
                File.WriteAllText(path, "ELF:" + name);
            }

            return Task.FromResult(path);
        }

        public static byte[] AppZip(string param)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, TitleId + "/sce_sys/param.json", param);
                Add(zip, TitleId + "/eboot.bin", "ELF");
                Add(zip, TitleId + "/assets/logo.png", "PNG");
            }

            return stream.ToArray();
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }
}

/// <summary>When the fake OnionHEN writes <c>config.ini</c>.</summary>
internal enum OnionConfigCreation
{
    WhenLaunched,
    OnSecondRead,
    Never,
}

/// <summary>One payload send and how many kstuffs were already running.</summary>
internal sealed record LoadKstuff(string File, int KstuffBefore, bool StartedBundled);

/// <summary>What the saved autoload list would start on the next boot.</summary>
internal sealed record BootSimulation(int Kstuff, bool Conflict, IReadOnlyList<string> Started);
