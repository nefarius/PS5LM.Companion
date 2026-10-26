using PS5LM.Companion.Core.Inference;

namespace PS5LM.Companion.Tests.Core;

public sealed class InferencePoolNamesTests
{
    private static InferencePoolNames Names(params (string Alias, string[] Names)[] pools) =>
        new(pools.Select(p => (p.Alias, (IReadOnlyList<string>)p.Names)));

    [Fact]
    public void Aliases_win_over_exact_names_and_exact_names_over_prefixes()
    {
        var names = Names(("qwen", ["gpt-4o", "claude-*"]), ("llama", ["claude-haiku-*", "claude-opus"]));

        Assert.Equal("qwen", names.Resolve("QWEN"));
        Assert.Equal("qwen", names.Resolve("gpt-4o"));
        Assert.Equal("llama", names.Resolve("claude-opus"));
        Assert.Equal("llama", names.Resolve("claude-haiku-4-5"));
        Assert.Equal("qwen", names.Resolve("claude-sonnet-4-5"));
        Assert.Null(names.Resolve("mistral"));
        Assert.Null(names.Resolve(" "));
    }

    [Fact]
    public void A_bare_star_catches_everything_else()
    {
        var names = Names(("qwen", ["*"]), ("llama", ["llama-*"]));

        Assert.Equal("llama", names.Resolve("llama-3"));
        Assert.Equal("qwen", names.Resolve("anything"));
    }

    [Fact]
    public void Conflicts_across_pools_are_reported()
    {
        Assert.Empty(InferencePoolNames.Conflicts([("a", ["x", "c-*"]), ("b", ["c-long-*"])]));
        Assert.Single(InferencePoolNames.Conflicts([("a", ["x"]), ("b", ["X"])]));
        Assert.Single(InferencePoolNames.Conflicts([("a", ["c-*"]), ("b", ["c-*"])]));
        Assert.Single(InferencePoolNames.Conflicts([("a", ["b"]), ("b", [])]));
    }

    [Theory]
    [InlineData("claude-*", true)]
    [InlineData("qwen3:8b", true)]
    [InlineData("anthropic/claude", true)]
    [InlineData("*", true)]
    [InlineData("a*b", false)]
    [InlineData("-bad", false)]
    [InlineData("has space", false)]
    public void Name_pattern(string name, bool valid)
    {
        var errors = InferencePoolRules.Errors("alias", "m.gguf", ["a"], _ => true, [name]);
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void Own_alias_and_duplicates_are_rejected()
    {
        Assert.Single(InferencePoolRules.Errors("alias", "m.gguf", ["a"], _ => true, ["ALIAS"]));
        Assert.Single(InferencePoolRules.Errors("alias", "m.gguf", ["a"], _ => true, ["x", "X"]));
    }
}
