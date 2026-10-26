using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelSourcesTests
{
    [Fact]
    public void Keeps_finished_downloads_with_their_repository_file_name()
    {
        var sources = ModelSources.Parse("""
            [
              {"repo":"unsloth/Qwen3.8-27B-GGUF","file":"Qwen3.8-27B-UD-Q2_K_XL.gguf","sha256":"abc","bytes":123,"state":"done","save_as":""},
              {"repo":"acme/tiny-GGUF","file":"tiny-Q4.gguf","state":"DONE","save_as":"tiny-Q4.gguf"}
            ]
            """);

        Assert.Collection(
            sources,
            first => Assert.Equal(new ModelSource("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8-27B-UD-Q2_K_XL.gguf", "abc", 123), first),
            second => Assert.Equal(new ModelSource("acme/tiny-GGUF", "tiny-Q4.gguf", null, 0), second));
    }

    [Theory]
    [InlineData("""[{"repo":"a/b","file":"x.gguf","state":"paused"}]""")]
    [InlineData("""[{"repo":"a/b","file":"x.gguf","state":"failed"}]""")]
    [InlineData("""[{"repo":"a/b","file":"x.gguf","state":"done","save_as":"renamed.gguf"}]""")]
    [InlineData("""[{"repo":"a/b","file":"sub/x.gguf","state":"done"}]""")]
    [InlineData("""[{"repo":"a/b","file":"x.bin","state":"done"}]""")]
    [InlineData("""[{"repo":"nonsense","file":"x.gguf","state":"done"}]""")]
    [InlineData("""[{"file":"x.gguf","state":"done"}, 5, null]""")]
    [InlineData("""{"repo":"a/b"}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_entries_that_cannot_be_reproduced(string? json) => Assert.Empty(ModelSources.Parse(json));
}
