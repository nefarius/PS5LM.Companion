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
        ["ftpsrv.elf"] = (2121, null),
        ["shadowmountplus.elf"] = (0, "shadowmountplus.elf"),
        ["voidshell.elf"] = (7007, null),
        ["ps5-web-file-manager.elf"] = (8888, null),
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("setup-tests").FullName;

    public SetupHarness()
    {
        Manager = new FakeManager(this);
        Probe = new FakeProbe(this);
        Artifacts = new FakeStore(this);
        Elfldr = new FakeElfldr(this);
        VoidShell = new FakeAppProvider(AppProviderKind.VoidShell);
        Files = new FakeFileSystem();
    }

    public HashSet<int> Ports { get; } = [8084];

    public HashSet<string> Processes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Version { get; set; } = "0.6.3";

    public bool LegacyList => Version.StartsWith("0.5", StringComparison.Ordinal);

    public List<ManagedPayload> Stored { get; } = [];

    public List<string> Events { get; } = [];

    public AutoloadConfig Autoload { get; set; } = new(false, string.Empty);

    /// <summary>Running payload processes by pid, with the payload file each one runs.</summary>
    public Dictionary<int, string> Pids { get; } = [];

    /// <summary>The payload file a pid runs, when its process name is the loader's generic <c>payload.elf</c>.</summary>
    public Dictionary<int, string> PidPayloads { get; } = [];

    /// <summary>A kill that succeeds but leaves the payload answering, as when another copy holds the port.</summary>
    public bool KillsAreIneffective { get; set; }

    public HashSet<string> LoadFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upload names Payload Manager refuses, as it does for a payload file that is in use.</summary>
    public HashSet<string> UploadFails { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public void Dispose() => Directory.Delete(_directory, recursive: true);

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
            if (Effects.TryGetValue(stored.FileName, out var effect))
            {
                if (effect.Port > 0)
                {
                    owner.Ports.Add(effect.Port);
                }

                if (effect.Process is not null)
                {
                    owner.Processes.Add(effect.Process);
                }
            }

            return Task.FromResult(new PayloadLaunchResult(true, 200, "OK"));
        }

        public Task<IReadOnlySet<string>?> GetProcessesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>?>(owner.Processes.ToHashSet(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyDictionary<int, string>?> GetProcessIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, string>?>(owner.Pids.ToDictionary(p => p.Key, p => p.Value));

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

        public Task SetAutoloadAsync(bool enabled, string list, CancellationToken cancellationToken)
        {
            owner.Autoload = new AutoloadConfig(enabled, list);
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
