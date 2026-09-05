using System.IO.Pipes;
using FluentAssertions;
using LiveWall.Contracts.Ipc;
using LiveWall.Infrastructure.Ipc;

namespace LiveWall.Renderer.ContractTests;

public sealed class IpcEndpointTests
{
    [Fact]
    public void BuildsStableAndVersionedHostEndpointName()
    {
        IpcEndpointNames.Host("stable").Should().Be(@"LOCAL\livewall.stable.p1.host");
        IpcEndpointNames.Host("preview", protocolMajor: 2).Should().Be(@"LOCAL\livewall.preview.p2.host");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Stable")]
    [InlineData("1stable")]
    [InlineData("stable_scope")]
    [InlineData("stable.scope")]
    public void RejectsInvalidInstallationScopes(string scope)
    {
        Action act = () => IpcEndpointNames.Host(scope);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreatesUnpredictableRendererCredentials()
    {
        RendererConnectionCredentials first = RendererConnectionCredentials.Create("stable");
        RendererConnectionCredentials second = RendererConnectionCredentials.Create("stable");

        first.PipeName.Should().StartWith(@"LOCAL\livewall.stable.p1.renderer.");
        first.PipeName.Should().NotBe(second.PipeName);
        first.AuthenticationToken.Should().HaveLength(64).And.NotBe(second.AuthenticationToken);
    }

    [Fact]
    public void AuthenticationTokenCanOnlyBeConsumedOnce()
    {
        RendererConnectionCredentials credentials = RendererConnectionCredentials.Create("stable");
        using OneTimeAuthenticationToken token = new(credentials.AuthenticationToken);

        token.TryConsume(credentials.AuthenticationToken).Should().BeTrue();
        token.TryConsume(credentials.AuthenticationToken).Should().BeFalse();
    }

    [Fact]
    public void AFailedAuthenticationAttemptConsumesTheToken()
    {
        RendererConnectionCredentials credentials = RendererConnectionCredentials.Create("stable");
        using OneTimeAuthenticationToken token = new(credentials.AuthenticationToken);

        token.TryConsume("not-hex").Should().BeFalse();
        token.TryConsume(credentials.AuthenticationToken).Should().BeFalse();
    }

    [Fact]
    public async Task LocalFactoryConnectsOnlyThroughTheLocalEndpointContract()
    {
        string nonce = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        string pipeName = IpcEndpointNames.Renderer("test", nonce);
        await using NamedPipeServerStream server = LocalNamedPipeFactory.CreateServer(pipeName, 1, isFirstInstance: true);
        await using NamedPipeClientStream client = LocalNamedPipeFactory.CreateClient(pipeName);

        Task accepting = server.WaitForConnectionAsync();
        await client.ConnectAsync(timeout: 5_000);
        await accepting;

        await using LengthPrefixedJsonChannel serverChannel = new(server, leaveOpen: true);
        await using LengthPrefixedJsonChannel clientChannel = new(client, leaveOpen: true);
        await clientChannel.WriteAsync(new TestMessage("connected"));

        TestMessage message = await serverChannel.ReadAsync<TestMessage>();
        message.Value.Should().Be("connected");
    }

    [Fact]
    public void LocalFactoryRejectsArbitraryPipeNames()
    {
        Action act = () => LocalNamedPipeFactory.CreateClient("other-product");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HostInstanceMutexAllowsOnlyOneOwnerPerScope()
    {
        string scope = $"test-{Guid.NewGuid():N}"[..21];
        using HostInstanceMutex? first = HostInstanceMutex.TryAcquire(scope);
        using HostInstanceMutex? second = HostInstanceMutex.TryAcquire(scope);

        first.Should().NotBeNull();
        second.Should().BeNull();
        first!.Name.Should().Be($@"Local\LiveWall.{scope}.Host");
    }

    private sealed record TestMessage(string Value);
}
