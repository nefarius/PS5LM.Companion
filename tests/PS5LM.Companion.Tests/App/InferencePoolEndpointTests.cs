using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class InferencePoolEndpointTests
{
    [Fact]
    public async Task A_user_pool_can_be_added_and_a_configuration_pool_cannot()
    {
        await using var factory = new CompanionFactory
        {
            Settings = new Dictionary<string, string>
            {
                ["Inference:Pools:chat:Model"] = "qwen.gguf",
                ["Inference:Pools:chat:Consoles:0"] = "ps5",
            },
        };
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/inference/pools", new { alias = "Extra", model = "qwen.gguf", consoles = new[] { "ps5" } });
        using var blocked = await client.PutAsJsonAsync("/api/inference/pools/chat", new { model = "other.gguf", consoles = new[] { "ps5" } });
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/inference/pools");
        var stored = File.ReadAllText(Path.Combine(factory.DataDirectory, "inference-pools.json"));
        using var removed = await client.DeleteAsync("/api/inference/pools/extra");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(["chat", "extra"], listed.GetProperty("pools").EnumerateArray().Select(pool => pool.GetProperty("alias").GetString()).Order());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Contains("\"alias\": \"extra\"", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_put_without_alternate_names_keeps_them()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/inference/pools", new { alias = "extra", model = "a.gguf", consoles = new[] { "ps5" }, alternateNames = new[] { "claude-*" } });
        using var kept = await client.PutAsJsonAsync("/api/inference/pools/extra", new { model = "b.gguf", consoles = new[] { "ps5" } });
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/inference/pools");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal("claude-*", listed.GetProperty("pools")[0].GetProperty("alternateNames")[0].GetString());
    }

    [Fact]
    public async Task An_explicit_null_alias_or_console_is_a_bad_request()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent("""{"alias":null,"model":"qwen.gguf","consoles":[null]}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/inference/pools", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
