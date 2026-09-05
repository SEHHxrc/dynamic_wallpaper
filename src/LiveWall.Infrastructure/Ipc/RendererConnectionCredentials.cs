using System.Security.Cryptography;
using LiveWall.Contracts.Ipc;

namespace LiveWall.Infrastructure.Ipc;

public sealed record RendererConnectionCredentials(string PipeName, string AuthenticationToken)
{
    public static RendererConnectionCredentials Create(string installationScope)
    {
        string nonce = CreateLowerHex(byteCount: 16);
        string authenticationToken = CreateLowerHex(byteCount: 32);
        return new RendererConnectionCredentials(
            IpcEndpointNames.Renderer(installationScope, nonce),
            authenticationToken);
    }

    private static string CreateLowerHex(int byteCount) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));
}
