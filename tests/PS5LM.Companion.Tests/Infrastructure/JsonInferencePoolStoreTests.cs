using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Infrastructure.Persistence;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class JsonInferencePoolStoreTests
{
    [Fact]
    public void A_corrupt_file_is_set_aside_before_an_empty_load()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "inference-pools.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{ this is not json");
        var store = new JsonInferencePoolStore(path, NullLogger<JsonInferencePoolStore>.Instance, Empty());

        Assert.Empty(store.Load());
        Assert.False(File.Exists(path));
        var preserved = Assert.Single(Directory.GetFiles(directory, "inference-pools.json.*.unreadable"));
        Assert.Contains("{ this is not json", File.ReadAllText(preserved), StringComparison.Ordinal);

        store.Save([]);
        Assert.Equal("[]", File.ReadAllText(path).Trim());
        Assert.Contains("{ this is not json", File.ReadAllText(preserved), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_whose_console_is_missing_is_kept_when_another_pool_is_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"), "inference-pools.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            [
              { "alias": "kept", "model": "qwen.gguf", "consoles": ["a"] },
              { "alias": "later", "model": "other.gguf", "consoles": ["missing"] }
            ]
            """);
        var store = new JsonInferencePoolStore(path, NullLogger<JsonInferencePoolStore>.Instance, OneConsole());

        var loaded = store.Load();
        store.Save(loaded);

        var saved = File.ReadAllText(path);
        Assert.Equal("kept", Assert.Single(loaded).Alias);
        Assert.Contains("\"alias\": \"later\"", saved, StringComparison.Ordinal);
        Assert.Contains("missing", saved, StringComparison.Ordinal);

        store.Save([loaded[0], new StoredInferencePool("later", "new.gguf", ["a"])]);
        store.Save(loaded);

        var replaced = File.ReadAllText(path);
        Assert.DoesNotContain("other.gguf", replaced, StringComparison.Ordinal);
        Assert.Contains("\"alias\": \"kept\"", replaced, StringComparison.Ordinal);
    }

    [Fact]
    public void Aliases_that_differ_only_by_case_load_as_one_pool()
    {
        var path = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"), "inference-pools.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            [
              { "alias": "Foo", "model": "first.gguf", "consoles": ["a"] },
              { "alias": "foo", "model": "second.gguf", "consoles": ["a"] }
            ]
            """);
        var store = new JsonInferencePoolStore(path, NullLogger<JsonInferencePoolStore>.Instance, OneConsole());

        var loaded = store.Load();
        store.Save(loaded);

        var pool = Assert.Single(loaded);
        Assert.Equal("foo", pool.Alias);
        Assert.Equal("first.gguf", pool.Model);
        Assert.DoesNotContain("second.gguf", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static ConsoleRegistry Empty() => new(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);

    private static ConsoleRegistry OneConsole()
    {
        var options = new ConsolesOptions();
        options.Consoles["a"] = new PS5Options { Host = "127.0.0.1" };
        return new ConsoleRegistry(options, new FakeConnectorFactory(), TimeProvider.System);
    }
}
