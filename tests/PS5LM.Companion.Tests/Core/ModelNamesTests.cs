using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelNamesTests
{
    [Theory]
    [InlineData("Qwen3.5-9B-Q4_K_M.gguf", true)]
    [InlineData("model (1).GGUF", true)]
    [InlineData("a.gguf", true)]
    [InlineData("../evil.gguf", false)]
    [InlineData("dir/model.gguf", false)]
    [InlineData("dir\\model.gguf", false)]
    [InlineData("..gguf.gguf", false)]
    [InlineData(".hidden.gguf", false)]
    [InlineData("model.bin", false)]
    [InlineData("model.gguf\n", false)]
    [InlineData("mod\0el.gguf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_plain_gguf_file_names(string? name, bool expected) =>
        Assert.Equal(expected, ModelNames.IsValidFileName(name));

    [Fact]
    public void Rejects_overlong_names() =>
        Assert.False(ModelNames.IsValidFileName(new string('a', 300) + ".gguf"));

    [Theory]
    [InlineData("unsloth/Qwen3.8-27B-GGUF", true)]
    [InlineData("a/b", true)]
    [InlineData("noslash", false)]
    [InlineData("a/b/c", false)]
    [InlineData("../x/y", false)]
    [InlineData("a/..", false)]
    [InlineData("a b/c", false)]
    [InlineData(null, false)]
    public void Validates_repository_ids(string? repo, bool expected) => Assert.Equal(expected, ModelNames.IsValidRepoId(repo));

    [Theory]
    [InlineData("/data/PS5LM/models/a.gguf", "a.gguf", "/data/PS5LM/models/a.gguf", true)]
    [InlineData("A.GGUF", "a.gguf", "/data/PS5LM/models/a.gguf", true)]
    [InlineData("/other/dir/a.gguf", "a.gguf", "/data/PS5LM/models/a.gguf", true)]
    [InlineData("b.gguf", "a.gguf", "/data/PS5LM/models/a.gguf", false)]
    [InlineData(null, "a.gguf", "/data/PS5LM/models/a.gguf", false)]
    public void Matches_the_loaded_model_id_by_path_or_file_name(string? id, string file, string appPath, bool expected) =>
        Assert.Equal(expected, ModelNames.IsSameModel(id, file, appPath));
}
