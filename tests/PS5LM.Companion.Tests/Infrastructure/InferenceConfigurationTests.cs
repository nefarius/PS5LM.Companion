using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Infrastructure;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class InferenceConfigurationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["PS5:Consoles:a:Host"] = "10.0.0.2", ["PS5:Consoles:b:Host"] = "10.0.0.3" }
                    .Concat(settings.ToDictionary(s => s.Key, s => (string?)s.Value)))
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddPS5Connectivity(configuration)
            .AddInferenceRouting(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    private static string Failure(params (string Key, string Value)[] settings)
    {
        using var provider = Build(settings);
        return Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<InferenceOptions>>().Value).Message;
    }

    [Fact]
    public void Without_pools_the_gateway_is_off()
    {
        using var provider = Build();

        Assert.False(provider.GetRequiredService<IOptions<InferenceOptions>>().Value.Enabled);
        Assert.False(provider.GetRequiredService<InferenceRouter>().Enabled);
    }

    [Fact]
    public void A_valid_pool_binds()
    {
        using var provider = Build(("Inference:Pools:qwen:Model", "qwen.gguf"), ("Inference:Pools:qwen:Consoles:0", "a"), ("Inference:Pools:qwen:Consoles:1", "B"));

        var pool = provider.GetRequiredService<IOptions<InferenceOptions>>().Value.Pools["qwen"];

        Assert.Equal("qwen.gguf", pool.Model);
        Assert.Equal(["a", "B"], pool.Consoles);
        Assert.Equal(["qwen"], provider.GetRequiredService<InferenceRouter>().Aliases);
    }

    [Fact]
    public void A_pool_needs_a_model_and_consoles()
    {
        var message = Failure(("Inference:Pools:qwen:Model", " "));

        Assert.Contains("Model is required", message, StringComparison.Ordinal);
        Assert.Contains("at least one console", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pool_consoles_must_exist_and_not_repeat()
    {
        var message = Failure(
            ("Inference:Pools:qwen:Model", "qwen.gguf"),
            ("Inference:Pools:qwen:Consoles:0", "ghost"),
            ("Inference:Pools:qwen:Consoles:1", "a"),
            ("Inference:Pools:qwen:Consoles:2", "A"));

        Assert.Contains("'ghost' is not configured", message, StringComparison.Ordinal);
        Assert.Contains("'a' is listed twice", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alias_must_be_a_plain_name_and_limits_must_be_sane()
    {
        var message = Failure(
            ("Inference:Pools:bad alias:Model", "m.gguf"),
            ("Inference:Pools:bad alias:Consoles:0", "a"),
            ("Inference:MaxConcurrentPerConsole", "0"),
            ("Inference:QueueTimeoutSeconds", "0"));

        Assert.Contains("bad alias", message, StringComparison.Ordinal);
        Assert.Contains("MaxConcurrentPerConsole", message, StringComparison.Ordinal);
        Assert.Contains("QueueTimeoutSeconds", message, StringComparison.Ordinal);
    }
}
