using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Persistence;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class JsonConsoleStoreTests
{
    [Fact]
    public void A_round_trip_keeps_the_settings_and_skips_an_invalid_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"), "consoles.json");
        var store = new JsonConsoleStore(path, NullLogger<JsonConsoleStore>.Instance);
        store.Save([new("den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den", FtpPort = 2122 })]);

        var reloaded = new JsonConsoleStore(path, NullLogger<JsonConsoleStore>.Instance).Load();

        var console = Assert.Single(reloaded);
        Assert.Equal("den", console.Id);
        Assert.Equal("Den", console.Options.DisplayName);
        Assert.Equal(2122, console.Options.FtpPort);
    }

    [Fact]
    public void An_invalid_entry_is_skipped_and_a_corrupt_file_loads_as_empty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "consoles.json");
        File.WriteAllText(path, """
            [
              { "id": "den", "options": { "host": "10.0.0.8" } },
              { "id": "nope!", "options": { "host": "10.0.0.9" } },
              { "id": "blank", "options": { "host": "" } }
            ]
            """);
        var store = new JsonConsoleStore(path, NullLogger<JsonConsoleStore>.Instance);

        var loaded = store.Load();
        File.WriteAllText(path, "{ this is not json");

        Assert.Equal("den", Assert.Single(loaded).Id);
        Assert.Empty(store.Load());
    }
}
