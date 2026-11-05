using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Tests.Core;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("01.009.001", 0, 9, 1)]
    [InlineData("01.010.000", 0, 10, 0)]
    [InlineData("01.011.000", 0, 11, 0)]
    [InlineData("02.000.000", 1, 0, 0)]
    public void Content_version_decodes_to_major_minor_patch(string raw, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParseContentVersion(raw, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal($"{major}.{minor}.{patch}", AppVersion.Format(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01.011")]
    [InlineData("00.011.000")]
    [InlineData("01.011.000-beta")]
    public void A_content_version_that_is_not_three_numbers_is_rejected(string? raw) =>
        Assert.False(AppVersion.TryParseContentVersion(raw, out _));

    [Theory]
    [InlineData("v0.11.0")]
    [InlineData("V0.9.1")]
    public void A_manifest_tag_parses(string tag)
    {
        Assert.True(AppVersion.TryParseTag(tag, out var version));
        Assert.Equal(0, version.Major);
    }

    [Fact]
    public void Param_json_uses_its_content_version()
    {
        const string Json = """{"titleId":"PPSA99575","contentVersion":"01.011.000"}""";

        Assert.True(AppVersion.TryParseParam(Json, out var version));
        Assert.Equal(AppVersion.OwnStorage, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"titleId":"PPSA99575"}""")]
    [InlineData("""{"contentVersion":11011}""")]
    public void Param_json_without_a_content_version_string_is_rejected(string? json) =>
        Assert.False(AppVersion.TryParseParam(json, out _));

    [Fact]
    public void An_older_app_sorts_before_the_pin()
    {
        Assert.True(AppVersion.TryParseContentVersion("01.009.001", out var installed));
        Assert.True(AppVersion.TryParseTag("v0.11.0", out var pinned));

        Assert.True(installed.CompareTo(pinned) < 0);
        Assert.Equal(0, AppVersion.OwnStorage.CompareTo(pinned));
    }
}
