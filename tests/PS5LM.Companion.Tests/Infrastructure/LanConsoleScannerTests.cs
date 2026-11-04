using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class LanConsoleScannerTests
{
    [Fact]
    public async Task Ftp_confirmation_keeps_the_folders_and_a_resolved_name()
    {
        var factory = new FakeConnectorFactory();
        factory["10.0.0.8"].VoidShell.Snapshot = Available(["PPSA99575"], "1.1.1-nefarius.4");
        var scanner = Scanner(factory, new StubResolver("ps5.lan"));

        var found = await scanner.ConfirmAsync("10.0.0.8", true, true, true, false, None, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("10.0.0.8", found.Host);
        Assert.Equal("ps5.lan", found.HostName);
        Assert.True(found.Ps5Lm);
        Assert.False(found.Ps5LmPlus);
        Assert.True(found.Ftp);
        Assert.True(found.PayloadManager);
        Assert.True(found.VoidShell);
        Assert.False(found.Exporter);
        Assert.True(found.VoidShellFork);
    }

    [Fact]
    public async Task A_failed_ftp_login_falls_back_to_voidshell()
    {
        var factory = new FakeConnectorFactory();
        var console = factory.Replace("10.0.0.9", new FakeConsole(new FakeFileSystem { Unreachable = true }));
        console.VoidShell.Snapshot = Available(["PPSA99581"], semver: null);

        var found = await Scanner(factory).ConfirmAsync("10.0.0.9", true, true, false, true, None, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found.Ftp);
        Assert.False(found.Ps5Lm);
        Assert.True(found.Ps5LmPlus);
        Assert.True(found.VoidShell);
        Assert.True(found.Exporter);
        Assert.False(found.VoidShellFork);
        Assert.Null(found.HostName);
    }

    [Fact]
    public async Task A_closed_ftp_port_uses_voidshell_even_when_no_ps5lm_app_is_installed()
    {
        var factory = new FakeConnectorFactory();
        factory["10.0.0.10"].VoidShell.Snapshot = Available([]);

        var found = await Scanner(factory).ConfirmAsync("10.0.0.10", false, true, false, false, None, CancellationToken.None);

        Assert.NotNull(found);
        Assert.False(found.Ftp);
        Assert.False(found.Ps5Lm);
        Assert.False(found.Ps5LmPlus);
        Assert.True(found.VoidShell);
        Assert.False(found.VoidShellFork);
    }

    [Fact]
    public async Task Voidshell_that_does_not_answer_is_skipped()
    {
        var factory = new FakeConnectorFactory();
        factory.Replace("10.0.0.11", new FakeConsole(new FakeFileSystem { Unreachable = true }));

        var found = await Scanner(factory).ConfirmAsync("10.0.0.11", true, true, false, false, None, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task Ftp_that_lists_no_ps5lm_folder_does_not_fall_back_to_voidshell()
    {
        var factory = new FakeConnectorFactory();
        var console = factory.Replace("10.0.0.12", new FakeConsole(new FakeFileSystem()));
        console.VoidShell.Snapshot = Available(["PPSA99575"], "1.1.1-nefarius.4");

        var found = await Scanner(factory).ConfirmAsync("10.0.0.12", true, true, false, false, None, CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task A_resolved_name_that_is_already_configured_is_left_out()
    {
        using var registry = new ConsoleRegistry(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);
        Assert.True(registry.Add("den", new PS5Options { Host = "ps5.lan" }).Succeeded);
        var factory = new FakeConnectorFactory();
        var scanner = new LanConsoleScanner(factory, registry, new StubResolver("PS5.LAN"));
        var configured = registry.All.Select(console => console.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = await scanner.ConfirmAsync("10.0.0.8", true, false, false, false, configured, CancellationToken.None);

        Assert.Null(found);
    }

    private static LanConsoleScanner Scanner(FakeConnectorFactory factory, IHostNameResolver? hostNames = null) =>
        new(factory, new ConsoleRegistry(new ConsolesOptions(), factory, TimeProvider.System), hostNames ?? new StubResolver(null));

    private static AppProviderSnapshot Available(IEnumerable<string> titles, string? semver = null) =>
        new(AppProviderKind.VoidShell, true, "V1.1", titles.ToList(), false, null, null, SemVer: semver);

    private static IReadOnlySet<string> None { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private sealed class StubResolver(string? name) : IHostNameResolver
    {
        public Task<string?> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(name);
    }
}
