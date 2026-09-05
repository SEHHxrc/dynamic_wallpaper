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

    public void Dispose()
    {
        Directory.Delete(rootPath, recursive: true);
    }

    private sealed record SampleState(long StateRevision, string Status);
}
