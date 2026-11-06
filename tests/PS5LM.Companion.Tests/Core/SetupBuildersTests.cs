using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Tests.Core;

public sealed class SetupBuildersTests
{
    [Fact]
    public void Embedded_manifest_is_valid_and_pins_every_artifact()
    {
        var manifest = SetupManifest.LoadEmbedded();

        Assert.Equal(
            ["elfldr", "kstuff-lite", "onionhen", "ftpsrv", "shadowmountplus", "voidshell", "ps5-web-file-manager"],
            manifest.Payloads.Select(p => p.Id).ToArray());
        Assert.Equal(["ps5lm", "ps5lmplus"], manifest.Apps.Select(a => a.Id).ToArray());
        Assert.All(manifest.Artifacts(), a => Assert.StartsWith("https://github.com/", a.Url, StringComparison.Ordinal));
        Assert.Equal(3000, manifest.Payloads.Single(p => p.Id == "kstuff-lite").DelayAfterMs);
        Assert.Equal(3000, manifest.Payloads.Single(p => p.Id == "onionhen").DelayAfterMs);
        var voidshell = manifest.Payloads.Single(p => p.Id == "voidshell");
        Assert.False(voidshell.Optional);
        Assert.Equal("v1.1.1-nefarius.4", voidshell.Version);
        Assert.StartsWith("https://github.com/nefarius/voidshell/releases/download/v1.1.1-nefarius.4/", voidshell.Url, StringComparison.Ordinal);
        Assert.Equal("/data/voidshell/www/index.html", voidshell.Files!.Single().Target);
        Assert.False(voidshell.SelfReplacing);
        var shadow = manifest.Payloads.Single(p => p.Id == "shadowmountplus");
        Assert.True(shadow.SelfReplacing);
        Assert.Null(shadow.PidFile);
        Assert.False(shadow.HandoffWhileTitleRuns);
        var ftpsrv = manifest.Payloads.Single(p => p.Id == "ftpsrv");
        Assert.Equal("v0.21.1-nefarius.1", ftpsrv.Version);
        Assert.StartsWith("https://github.com/nefarius/ftpsrv/releases/download/v0.21.1-nefarius.1/", ftpsrv.Url, StringComparison.Ordinal);
        Assert.True(ftpsrv.SelfReplacing);
        Assert.True(ftpsrv.HandoffWhileTitleRuns);
        Assert.Equal(2121, ftpsrv.Ready.Port);
        Assert.Equal(["ftpsrv.elf"], ftpsrv.Ready.Processes);
    }

    [Theory]
    [InlineData("\"id\": \"elfldr\"", "\"id\": \"elfldr\", \"selfReplacing\": true", "ready process list")]
    [InlineData("\"pidFile\":", "\"selfReplacing\": true, \"pidFile\":", "both selfReplacing and pidFile")]
    [InlineData("\"id\": \"voidshell\"", "\"id\": \"voidshell\", \"handoffWhileTitleRuns\": true", "needs selfReplacing")]
    public void Manifest_rejects_a_self_replacing_payload_that_cannot_be_handed_over(string from, string to, string message)
    {
        using var stream = typeof(SetupManifest).Assembly.GetManifestResourceStream("PS5LM.Companion.Core.Setup.setup-manifest.json")!;
        var json = new StreamReader(stream).ReadToEnd();

        var ex = Assert.Throws<InvalidDataException>(() => SetupManifest.Parse(json.Replace(from, to, StringComparison.Ordinal)));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sha256\": \"2a42054fd9725e1d9595ff4add95abc30e5b49446fcdcb96c06775a376a841c4\"", "\"sha256\": \"XYZ\"")]
    [InlineData("\"url\": \"https://github.com/itsPLK/ps5-payload-manager", "\"url\": \"http://github.com/itsPLK/ps5-payload-manager")]
    [InlineData("pldmgr_v0.6.3.elf\",", "pldmgr.elf\",")]
    [InlineData("    \"fileName\": \"pldmgr_v0.6.3.elf\",\n", "")]
    [InlineData("/data/voidshell/www/index.html", "/etc/index.html")]
    [InlineData("\"titleId\": \"PPSA99575\"", "\"titleId\": \"PP/../.._\"")]
    [InlineData("/data/voidshell/voidshell.pid", "/data/voidshell/")]
    [InlineData("/data/voidshell/voidshell.pid", "/etc/voidshell.pid")]
    public void Manifest_rejects_unpinned_or_unsafe_entries(string from, string to)
    {
        using var stream = typeof(SetupManifest).Assembly.GetManifestResourceStream("PS5LM.Companion.Core.Setup.setup-manifest.json")!;
        var json = new StreamReader(stream).ReadToEnd();

        Assert.Throws<InvalidDataException>(() => SetupManifest.Parse(json.Replace(from, to, StringComparison.Ordinal)));
    }

    [Fact]
    public void Autoload_list_orders_payloads_adds_pauses_and_keeps_foreign_entries_once()
    {
        var list = AutoloadListBuilder.Build(
            [("a", 0), ("b", 3000), ("c", 0)],
            "!1000, x ,a,x,!2000");

        Assert.Equal("a,!3000,b,!3000,c,!3000,x", list);
    }

    [Fact]
    public void Autoload_list_drops_another_slot_of_a_managed_payload_and_keeps_other_ids()
    {
        var list = AutoloadListBuilder.Build(
            [("local/ftpsrv", 0), ("local/elfldr", 0)],
            "repo/ftpsrv@e8cce5d1,local/mine,repo/websrv@ab12,!500");

        Assert.Equal("local/ftpsrv,!3000,local/elfldr,!3000,local/mine,!3000,repo/websrv@ab12", list);
    }

    [Fact]
    public void Autoload_list_keeps_a_longer_delay_and_adds_no_trailing_pause()
    {
        var list = AutoloadListBuilder.Build([("a", 5000), ("b", 0)], null);

        Assert.Equal("a,!5000,b", list);
    }

    [Fact]
    public void Autoload_list_is_stable_across_repeats()
    {
        (string, int)[] ours = [("a", 0), ("b", 3000)];
        var first = AutoloadListBuilder.Build(ours, null);

        Assert.Equal(first, AutoloadListBuilder.Build(ours, first));
    }

    [Theory]
    [InlineData("0.6.3", true, "0.6.3")]
    [InlineData("v0.5.2\n", true, "0.5.2")]
    [InlineData("0.6.3-beta", true, "0.6.3")]
    [InlineData("<!doctype html>", false, "0.0")]
    public void Version_parsing(string text, bool ok, string expected)
    {
        Assert.Equal(ok, PayloadManagerVersion.TryParse(text, out var version));
        Assert.Equal(new Version(expected), version);
    }

    [Fact]
    public void Onion_editor_appends_with_the_existing_separator_and_keeps_everything_else()
    {
        var edit = OnionHenConfigEditor.AllowTitle("[a]\r\nx=1\r\n[App_Jailbreak]\r\nexact_title_ids = PPSA1, PPSA2 ; note\r\n[b]\r\ny=2\r\n", "PPSA3");

        Assert.Equal(AllowListStatus.Added, edit.Status);
        Assert.Equal("[a]\r\nx=1\r\n[App_Jailbreak]\r\nexact_title_ids = PPSA1, PPSA2, PPSA3 ; note\r\n[b]\r\ny=2\r\n", edit.Text);
    }

    [Fact]
    public void Onion_editor_fills_an_empty_value_and_recognises_a_present_id_case_insensitively()
    {
        Assert.Equal("[app_jailbreak]\nexact_title_ids=PPSA9\n", OnionHenConfigEditor.AllowTitle("[app_jailbreak]\nexact_title_ids=\n", "PPSA9").Text);
        Assert.Equal(AllowListStatus.AlreadyPresent, OnionHenConfigEditor.AllowTitle("[app_jailbreak]\nexact_title_ids=ppsa9,PPSA1\n", "PPSA9").Status);
    }

    [Theory]
    [InlineData("[general]\nx=1\n", AllowListStatus.SectionMissing)]
    [InlineData("[app_jailbreak]\nenabled=1\n[other]\nexact_title_ids=PPSA1\n", AllowListStatus.KeyMissing)]
    [InlineData("[app_jailbreak]\nexact_title_ids=[\"PPSA1\"]\n", AllowListStatus.UnsupportedFormat)]
    public void Onion_editor_leaves_unknown_layouts_untouched(string config, AllowListStatus status)
    {
        var edit = OnionHenConfigEditor.AllowTitle(config, "PPSA9");

        Assert.Equal(status, edit.Status);
        Assert.Equal(config, edit.Text);
    }
}
