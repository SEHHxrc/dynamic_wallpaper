using System.Text;
using FluentAssertions;
using LiveWall.Infrastructure.FileSystem;

namespace LiveWall.Package.Tests;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), $"livewall-atomic-tests-{Guid.NewGuid():N}");

    public AtomicFileWriterTests()
    {
        Directory.CreateDirectory(rootPath);
    }

    [Fact]
    public async Task CreatesAFileUsingUtf8WithoutBom()
    {
        string destination = Path.Combine(rootPath, "nested", "settings.json");

        await AtomicFileWriter.WriteAllTextAsync(destination, "动态壁纸");

        byte[] bytes = await File.ReadAllBytesAsync(destination);
        bytes.Should().Equal(Encoding.UTF8.GetBytes("动态壁纸"));
        Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task ReplacesAnExistingFileAndLeavesNoTemporaryFile()
    {
        string destination = Path.Combine(rootPath, "state.json");
        await File.WriteAllTextAsync(destination, "old");

        await AtomicFileWriter.WriteAllTextAsync(destination, "new");

        (await File.ReadAllTextAsync(destination)).Should().Be("new");
        Directory.EnumerateFiles(rootPath, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task WritesJsonUsingWebNamingDefaults()
    {
        string destination = Path.Combine(rootPath, "state.json");

        await AtomicFileWriter.WriteJsonAsync(destination, new SampleState(7, "ready"));

        (await File.ReadAllTextAsync(destination)).Should().Be("{\"stateRevision\":7,\"status\":\"ready\"}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWriteDoesNotCommitReceiptOrLeaveTemporaryFiles(bool existing)
    {
        string destination = Path.Combine(rootPath, "native-exit-receipt.json");
        if (existing) await File.WriteAllTextAsync(destination, "old");
        using CancellationTokenSource expired = new();
        expired.Cancel();
        Func<Task> write = () => AtomicFileWriter.WriteAllTextAsync(destination, "new", expired.Token);
        await write.Should().ThrowAsync<OperationCanceledException>();
        if (existing) (await File.ReadAllTextAsync(destination)).Should().Be("old");
        else File.Exists(destination).Should().BeFalse();
        Directory.EnumerateFiles(rootPath, "*.tmp").Should().BeEmpty();
    }

    public void Dispose()
    {
        Directory.Delete(rootPath, recursive: true);
    }

    private sealed record SampleState(long StateRevision, string Status);
}
