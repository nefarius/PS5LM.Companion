using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.Tests.Core;

public sealed class ConsoleIdSuggestionTests
{
    [Theory]
    [InlineData("Den", "den")]
    [InlineData("Living room", "living-room")]
    [InlineData("PS5!!", "ps5")]
    [InlineData("  ", "")]
    [InlineData("---", "")]
    public void A_name_suggests_a_slug(string name, string expected) =>
        Assert.Equal(expected, ConsoleIds.Suggest(name));

    [Fact]
    public void A_long_name_stays_a_valid_slug()
    {
        var id = ConsoleIds.Suggest(new string('a', 39) + " b");

        Assert.InRange(id.Length, 1, 40);
        Assert.True(ConsoleIds.IsValid(id));
    }
}
