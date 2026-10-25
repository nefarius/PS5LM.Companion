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
}
