using FluentAssertions;
using LiveWall.Infrastructure.FileSystem;

namespace LiveWall.Package.Tests;

public sealed class CanonicalPathResolverTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), $"livewall-path-tests-{Guid.NewGuid():N}");

    public CanonicalPathResolverTests()
    {
        Directory.CreateDirectory(rootPath);
    }

    [Fact]
    public void ResolvesANestedPackagePathInsideTheTrustedRoot()
    {
        CanonicalPathResolver resolver = new(rootPath);

        string result = resolver.ResolvePackagePath("assets/video/intro.mp4");

        result.Should().Be(Path.Combine(rootPath, "assets", "video", "intro.mp4"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("assets/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("assets\\windows.txt")]
    [InlineData("assets//empty.txt")]
    [InlineData("assets/./same.txt")]
    [InlineData("assets/trailing./file.txt")]
    [InlineData("assets/CON/file.txt")]
    [InlineData("assets/file.txt/")]
    public void RejectsUnsafePackagePaths(string packagePath)
    {
        CanonicalPathResolver resolver = new(rootPath);

        Action act = () => resolver.ResolvePackagePath(packagePath);

        act.Should().Throw<PathSecurityException>();
    }

    [Fact]
    public void RejectsAReparsePointInAnExistingPath()
    {
        string targetPath = Path.Combine(rootPath, "target");
        string linkPath = Path.Combine(rootPath, "linked");
        Directory.CreateDirectory(targetPath);

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        CanonicalPathResolver resolver = new(rootPath);
        Action act = () => resolver.ResolvePackagePath("linked/content.txt");

        act.Should().Throw<PathSecurityException>();
    }

    public void Dispose()
    {
        Directory.Delete(rootPath, recursive: true);
    }
}
