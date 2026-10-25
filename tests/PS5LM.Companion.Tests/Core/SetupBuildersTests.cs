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
        Assert.Equal("/data/voidshell/www/index.html", manifest.Payloads.Single(p => p.Id == "voidshell").Files!.Single().Target);
    }

    [Theory]
    [InlineData("\"sha256\": \"2a42054fd9725e1d9595ff4add95abc30e5b49446fcdcb96c06775a376a841c4\"", "\"sha256\": \"XYZ\"")]
    [InlineData("\"url\": \"https://github.com/itsPLK/ps5-payload-manager", "\"url\": \"http://github.com/itsPLK/ps5-payload-manager")]
    [InlineData("pldmgr_v0.6.3.elf\",", "pldmgr.elf\",")]
    [InlineData("    \"fileName\": \"pldmgr_v0.6.3.elf\",\n", "")]
    [InlineData("/data/voidshell/www/index.html", "/etc/index.html")]
    [InlineData("\"titleId\": \"PPSA99575\"", "\"titleId\": \"PP/../.._\"")]
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

        Assert.Equal("a,b,!3000,c,x", list);
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
