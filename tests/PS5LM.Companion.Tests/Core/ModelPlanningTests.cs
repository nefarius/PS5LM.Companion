using PS5LM.Companion.Core.Models.Planning;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelPlanningTests
{
    private const double Budget = 11.38;

    [Fact]
    public async Task A_dense_header_exposes_its_layer_shape()
    {
        var info = await Read("llama-3b.gguf", 100, Llama());

        Assert.True(info.Ok);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal("Llama 3", info.Name);
        Assert.Equal(4u, info.LayerCount);
        Assert.Equal(4u, info.KeyValueHeads);
        Assert.Equal(32u, info.KeyLength);
        Assert.Equal(32u, info.ValueLength);
        Assert.Equal(8192u, info.TrainedContext);
        Assert.Null(info.Error);
    }

    [Fact]
    public async Task A_mixture_of_experts_keeps_its_expert_count()
    {
        var info = await Read("moe.gguf", 100, Llama().Kv("llama.expert_count", 64u));

        Assert.True(info.Ok);
        Assert.Equal(64u, info.ExpertCount);
    }

    [Fact]
    public async Task A_decision_model_gets_a_wide_batch_and_a_4k_cap()
    {
        var header = new GgufBuilder()
            .Kv("general.architecture", "qwen35")
            .Kv("general.name", "Clef Flash Qwen3.5 9B")
            .Kv("qwen35.block_count", 32u)
            .Kv("qwen35.attention.head_count", 16u)
            .Kv("qwen35.attention.head_count_kv", 4u)
            .Kv("qwen35.embedding_length", 4096u)
            .Kv("qwen35.context_length", 262144u)
            .Kv("qwen35.decision.type", "clef");
        var info = await Read("clef-flash-Q4_K_M.gguf", 5L << 30, header);

        Assert.True(info.Ok);
        Assert.Equal("clef", info.Decision);
        var plan = ModelPlanner.Plan(info, Budget);
        Assert.True(plan.Fits);
        Assert.Equal(4096u, plan.Ctx);
        Assert.Equal(2048u, plan.Ubatch);
        Assert.StartsWith("decision model (clef)", plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_model_without_a_decision_head_keeps_the_narrow_batch()
    {
        var info = await Read("Qwen3.5-9B-Q4_K_M.gguf", 5L << 30, Qwen35Chat());

        var plan = ModelPlanner.Plan(info, Budget);
        Assert.True(plan.Fits);
        Assert.True(plan.Ctx > 4096);
        Assert.Equal(ModelPlanner.PromptUbatch, plan.Ubatch);
    }

    [Fact]
    public async Task A_larger_prompt_batch_costs_compute_buffer()
    {
        var info = await Read("Qwen3.5-9B-Q4_K_M.gguf", 5L << 30, Qwen35Chat().Kv("qwen35.feed_forward_length", 12288u));

        var narrow = ModelPlanner.Plan(info, Budget);
        var wide = ModelPlanner.Plan(info, Budget, ubatch: 512);
        Assert.Equal(12288u, info.FeedForwardLength);
        Assert.Equal(64u, narrow.Ubatch);
        Assert.Equal(512u, wide.Ubatch);
        var extra = wide.ReserveGib - narrow.ReserveGib;
        Assert.InRange(extra, 0.18, 0.21);
    }

    [Fact]
    public async Task Bonsai_stays_at_64k_and_prism_packings_are_refused()
    {
        var ok = await Read("Ternary-Bonsai-2-27B-Q2_0.gguf", 7626008928L, Bonsai(41));
        var plan = ModelPlanner.Plan(ok, 12.0, ctxCap: 131072);
        Assert.True(ok.Ok);
        Assert.True(plan.Fits);
        Assert.Equal(65536u, plan.Ctx);

        var pq = await Read("Ternary-Bonsai-2-27B-PQ2_0.gguf", 7206168928L, Bonsai(141));
        Assert.False(pq.Ok);
        Assert.Contains("bonsai-pq2-to-q2.py", pq.Error, StringComparison.Ordinal);

        var pt = await Read("Ternary-Bonsai-2-27B-PTQ1_0.gguf", 5946648928L, Bonsai(143));
        Assert.False(pt.Ok);
        Assert.Contains("PTQ1_0", pt.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sliding_window_layers_are_not_counted_as_full_attention()
    {
        var pattern = new bool[36];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = i % 4 != 3;
        }

        var header = new GgufBuilder()
            .Kv("general.architecture", "spark2_5")
            .Kv("spark2_5.block_count", 36u)
            .Kv("spark2_5.attention.head_count", 16u)
            .Kv("spark2_5.attention.head_count_kv", 4u)
            .Kv("spark2_5.embedding_length", 2560u)
            .Kv("spark2_5.attention.key_length", 256u)
            .Kv("spark2_5.attention.value_length", 256u)
            .Kv("spark2_5.context_length", 1048576u)
            .Kv("spark2_5.attention.sliding_window", 512u)
            .Kv("spark2_5.attention.sliding_window_pattern", pattern);
        var info = await Read("Spark-X2.6-4B-Q8_0.gguf", 4375021824L, header);

        Assert.True(info.Ok);
        Assert.Equal(27u, info.SlidingWindowLayers);
        Assert.Equal(512u, info.SlidingWindow);
        Assert.Equal(9 * 4 * 512 * 34.0 / 32, ModelPlanner.KvBytesPerToken(info, "q8_0"));
        var plan = ModelPlanner.Plan(info, Budget, ctxCap: 262144);
        Assert.True(plan.Fits);
        Assert.Equal(262144u, plan.Ctx);
        Assert.Equal("q8_0", plan.KvType);

        var full = info with { SlidingWindowLayers = 0 };
        Assert.Equal(131072u, ModelPlanner.Plan(full, Budget, ctxCap: 262144).Ctx);
    }

    [Fact]
    public async Task Qwen3_8_27b_at_the_calibrated_budget_gets_64k_with_a_q4_0_cache()
    {
        // 16 of 64 layers keep a KV cache, 4 heads of 256, and a recurrent state in the measured range.
        // 9 GiB of weights fits 64k at q4_0 in 11.38 GiB and does not fit a longer context or a q8_0 cache.
        var header = new GgufBuilder()
            .Kv("general.architecture", "qwen35")
            .Kv("general.name", "Qwen3.8 27B")
            .Kv("qwen35.block_count", 65u)
            .Kv("qwen35.nextn_predict_layers", 1u)
            .Kv("qwen35.attention.head_count", 24u)
            .Kv("qwen35.attention.head_count_kv", 4u)
            .Kv("qwen35.embedding_length", 5120u)
            .Kv("qwen35.attention.key_length", 256u)
            .Kv("qwen35.attention.value_length", 256u)
            .Kv("qwen35.full_attention_interval", 4u)
            .Kv("qwen35.context_length", 262144u)
            .Kv("qwen35.ssm.inner_size", 6144u)
            .Kv("qwen35.ssm.state_size", 128u);
        var info = await Read("Qwen3.8-27B-UD-Q2_K_XL.gguf", 9L * 1073741824, header);

        Assert.True(info.Ok);
        Assert.Equal(64u, info.LayerCount);
        Assert.Equal(16u, info.KeyValueLayers);
        Assert.InRange(info.StateBytes / 1048576.0, 140, 170);
        var plan = ModelPlanner.Plan(info, Budget);
        Assert.True(plan.Fits);
        Assert.Equal(65536u, plan.Ctx);
        Assert.Equal("q4_0", plan.KvType);
    }

    [Fact]
    public async Task Gpt_oss_is_capped_at_16k()
    {
        var header = new GgufBuilder()
            .Kv("general.architecture", "gpt-oss")
            .Kv("general.name", "gpt-oss 20B")
            .Kv("gpt-oss.block_count", 24u)
            .Kv("gpt-oss.attention.head_count", 8u)
            .Kv("gpt-oss.attention.head_count_kv", 2u)
            .Kv("gpt-oss.embedding_length", 1024u)
            .Kv("gpt-oss.context_length", 131072u);
        var info = await Read("gpt-oss-20b.gguf", 100 * 1024 * 1024, header);

        var plan = ModelPlanner.Plan(info, Budget, ctxCap: 65536);
        Assert.True(plan.Fits);
        Assert.Equal(16384u, plan.Ctx);
    }

    [Fact]
    public void Nothing_fits_in_too_little_memory()
    {
        var info = new GgufModelInfo
        {
            Ok = true,
            FileBytes = 20ul << 30,
            LayerCount = 32,
            KeyValueHeads = 8,
            KeyLength = 128,
            ValueLength = 128,
        };

        var plan = ModelPlanner.Plan(info, Budget);
        Assert.False(plan.Fits);
        Assert.Contains("needs", plan.Why, StringComparison.Ordinal);
        Assert.Contains("11.4 GiB is free", plan.Why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_stream_and_a_non_gguf_are_refused()
    {
        var truncated = await GgufHeader.ReadAsync(new MemoryStream("GGUF"u8.ToArray()), "m.gguf", 10, CancellationToken.None);
        Assert.False(truncated.Ok);
        Assert.Equal("metadata could not be read", truncated.Error);

        var other = await GgufHeader.ReadAsync(new MemoryStream("nope"u8.ToArray()), "m.gguf", 10, CancellationToken.None);
        Assert.False(other.Ok);
        Assert.Equal("not a GGUF file", other.Error);
    }

    [Fact]
    public void The_budget_comes_from_the_last_gpu_line_and_the_settings()
    {
        var log = """
            22:56:01 ps5lm-app: device 0 CPU (CPU): 1.00 / 2.00 GiB free
            22:56:01 ps5lm-app: device 1 Vulkan0 (AMD Radeon Graphics (RADV RAPHAEL)): 11.38 / 14.00 GiB free
            22:57:01 ps5lm-app: device 1 Vulkan0 (AMD Radeon Graphics (RADV RAPHAEL)): 10.50 / 14.00 GiB free
            """;
        var budget = ModelBudget.Parse(log, """{"ctx_cap":8192,"kv_type":"q4_0","auto_load":false}""", ModelBudget.CalibratedGib);

        Assert.True(budget.FromAppLog);
        Assert.Equal(10.50, budget.Gib);
        Assert.Equal(8192u, budget.CtxCap);
        Assert.Equal("q4_0", budget.KvType);
        Assert.Equal("10.5 GiB free on this console, from app.log", budget.Caption);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("22:56:01 ps5lm-app: start")]
    [InlineData("device 0 CPU (CPU): 1.00 / 2.00 GiB free")]
    public void A_log_without_a_gpu_line_uses_the_assumption(string? log)
    {
        var budget = ModelBudget.Parse(log, "not json", 11.38);

        Assert.False(budget.FromAppLog);
        Assert.Equal(11.38, budget.Gib);
        Assert.Equal(ModelBudget.DefaultCtxCap, budget.CtxCap);
        Assert.Equal(ModelBudget.AutoKv, budget.KvType);
        Assert.Equal("assumed 11.4 GiB", budget.Caption);
    }

    [Fact]
    public void An_unknown_cache_type_stays_automatic()
    {
        var budget = ModelBudget.Parse(null, """{"kv_type":"q5_0","ctx_cap":"nope"}""", 11.38);

        Assert.Equal(ModelBudget.AutoKv, budget.KvType);
        Assert.Equal(ModelBudget.DefaultCtxCap, budget.CtxCap);
        Assert.Null(budget.ForcedKvType);
    }

    [Theory]
    [InlineData(1_000_000_000L, RepoFitKind.LikelyFits, "likely fits")]
    [InlineData(20_000_000_000L, RepoFitKind.LowBitOnly, "low-bit only")]
    [InlineData(200_000_000_000L, RepoFitKind.TooBig, "too big")]
    public void A_repository_hint_compares_a_small_and_a_comfortable_quant(long parameters, RepoFitKind kind, string label)
    {
        var hint = RepoFit.Estimate(parameters, "llama", 11.38);

        Assert.NotNull(hint);
        Assert.Equal(kind, hint.Kind);
        Assert.Equal(label, hint.Label);
        Assert.Contains("llama", hint.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_without_a_parameter_count_has_no_hint() =>
        Assert.Null(RepoFit.Estimate(null, "llama", 11.38));

    private static GgufBuilder Llama() => new GgufBuilder()
        .Kv("general.architecture", "llama")
        .Kv("general.name", "Llama 3")
        .Kv("llama.block_count", 4u)
        .Kv("llama.attention.head_count", 4u)
        .Kv("llama.attention.head_count_kv", 4u)
        .Kv("llama.embedding_length", 128u)
        .Kv("llama.context_length", 8192u);

    private static GgufBuilder Qwen35Chat() => new GgufBuilder()
        .Kv("general.architecture", "qwen35")
        .Kv("general.name", "Qwen3.5 9B")
        .Kv("qwen35.block_count", 32u)
        .Kv("qwen35.attention.head_count", 16u)
        .Kv("qwen35.attention.head_count_kv", 4u)
        .Kv("qwen35.embedding_length", 4096u)
        .Kv("qwen35.context_length", 262144u);

    private static GgufBuilder Bonsai(uint fileType) => new GgufBuilder()
        .Kv("general.architecture", "qwen35")
        .Kv("general.name", "Ternary-Bonsai-2-27B")
        .Kv("qwen35.block_count", 64u)
        .Kv("qwen35.attention.head_count", 24u)
        .Kv("qwen35.attention.head_count_kv", 4u)
        .Kv("qwen35.embedding_length", 5120u)
        .Kv("qwen35.attention.key_length", 256u)
        .Kv("qwen35.attention.value_length", 256u)
        .Kv("qwen35.full_attention_interval", 4u)
        .Kv("qwen35.context_length", 262144u)
        .Kv("prism.hadamard.version", 1u)
        .Kv("general.file_type", fileType);

    private static Task<GgufModelInfo> Read(string name, long bytes, GgufBuilder header) =>
        GgufHeader.ReadAsync(new MemoryStream(header.Bytes()), name, (ulong)bytes, CancellationToken.None);
}
