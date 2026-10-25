using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
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

    private static ConsoleRegistry Empty() => new(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);
}
