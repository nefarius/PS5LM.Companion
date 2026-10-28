namespace PS5LM.Companion.Tests.App;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1.0")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.1", "1.1.0")]
    [InlineData("1.1.0", "2.0.0")]
    [InlineData("1.0.1", "1.0.2-alpha.0.2")]
    [InlineData("1.0.2-alpha.0.2", "1.0.2")]
    [InlineData("v1.0.0", "1.0.1")]
    public void A_higher_precedence_version_is_newer(string older, string newer)
    {
        Assert.True(PS5LM.Companion.App.Updates.SemanticVersion.IsNewer(newer, older));
        Assert.False(PS5LM.Companion.App.Updates.SemanticVersion.IsNewer(older, newer));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("V1.0.0", "1.0.0+build.9")]
    [InlineData("1.0.0-alpha+sha", "1.0.0-alpha")]
    [InlineData(" 1.0.0 ", "1.0.0")]
    public void Equal_versions_are_not_newer(string left, string right)
    {
        Assert.False(PS5LM.Companion.App.Updates.SemanticVersion.IsNewer(left, right));
        Assert.False(PS5LM.Companion.App.Updates.SemanticVersion.IsNewer(right, left));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("01.0.0")]
    [InlineData("1.01.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-alpha.")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("not-a-version")]
    public void Unparseable_versions_are_not_newer(string? raw)
    {
        Assert.False(PS5LM.Companion.App.Updates.SemanticVersion.IsNewer(raw, "1.0.0"));
        Assert.False(PS5LM.Companion.App.Updates.SemanticVersion.IsNewer("1.0.0", raw));
        Assert.False(PS5LM.Companion.App.Updates.SemanticVersion.TryParse(raw, out _));
    }
}
