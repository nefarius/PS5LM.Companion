using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelDeletionServiceTests
{
    private const string Repo = "acme/tiny-GGUF";
    private const string File = "tiny-Q4.gguf";
    private const string Path = "/data/PS5LM/models/tiny-Q4.gguf";

    private static Transfer Transfer(string consoleId, string fileName, string target, TransferState state) =>
        new(Guid.NewGuid(), consoleId, Repo, fileName, null, 1, target, state, 0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public async Task An_active_transfer_of_the_same_file_blocks_deletion()
    {
        await using var harness = new TransferHarness();
        harness.FileSystem.AddFile(Path, new byte[1]);
        harness.TransferStore.Saved = [Transfer("ps5", File, "Internal storage", TransferState.Uploading)];
        await harness.PrimeAsync();
        await harness.Service.InitializeAsync(CancellationToken.None);

        var result = await new ModelDeletionService(harness.Service).DeleteAsync(harness.Runtime, Path, CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
        Assert.True(harness.FileSystem.Files.ContainsKey(Path));
    }

    [Fact]
    public async Task A_finished_transfer_or_another_file_or_root_does_not_block_deletion()
    {
        await using var harness = new TransferHarness();
        harness.FileSystem.AddFile(Path, new byte[1]);
        harness.TransferStore.Saved =
        [
            Transfer("ps5", File, "Internal storage", TransferState.Completed),
            Transfer("ps5", "other.gguf", "Internal storage", TransferState.Queued),
            Transfer("ps5", File, "USB 0", TransferState.Uploading),
        ];
        await harness.PrimeAsync();
        await harness.Service.InitializeAsync(CancellationToken.None);

        var result = await new ModelDeletionService(harness.Service).DeleteAsync(harness.Runtime, Path, CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.False(harness.FileSystem.Files.ContainsKey(Path));
    }

    [Fact]
    public async Task A_transfer_to_another_console_does_not_block_this_console()
    {
        await using var harness = new TransferHarness(null, "den");
        harness.FileSystem.AddFile(Path, new byte[1]);
        harness.Connectors["den"].FileSystem.AddFile(Path, new byte[1]);
        harness.TransferStore.Saved = [Transfer("den", File, "Internal storage", TransferState.Uploading)];
        await harness.PrimeAsync();
        await harness.Service.InitializeAsync(CancellationToken.None);

        var result = await new ModelDeletionService(harness.Service).DeleteAsync(harness.Runtime, Path, CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.False(harness.FileSystem.Files.ContainsKey(Path));
        Assert.True(harness.Connectors["den"].FileSystem.Files.ContainsKey(Path));
    }
}
