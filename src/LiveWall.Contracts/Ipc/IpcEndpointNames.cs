namespace LiveWall.Contracts.Ipc;

/// <summary>
/// Shared, versioned names used to discover local LiveWall IPC endpoints.
/// </summary>
public static class IpcEndpointNames
{
    public const int CurrentProtocolMajor = 1;
    private const string Prefix = @"LOCAL\livewall";

    public static string Host(string installationScope, int protocolMajor = CurrentProtocolMajor)
    {
        ValidateInstallationScope(installationScope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protocolMajor);
        return $@"{Prefix}.{installationScope}.p{protocolMajor}.host";
    }

    public static string Renderer(
        string installationScope,
        string randomNonce,
        int protocolMajor = CurrentProtocolMajor)
    {
        ValidateInstallationScope(installationScope);
        ValidateLowerHex(randomNonce, expectedLength: 32, nameof(randomNonce));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(protocolMajor);
        return $@"{Prefix}.{installationScope}.p{protocolMajor}.renderer.{randomNonce}";
    }

    public static void ValidateInstallationScope(string installationScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationScope);
        if (installationScope.Length > 32 ||
            installationScope[0] is < 'a' or > 'z' ||
            installationScope.Any(character =>
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character != '-'))
        {
            throw new ArgumentException(
                "Installation scope must be 1..32 lowercase ASCII letters, digits, or hyphens and start with a letter.",
                nameof(installationScope));
        }
    }

    private static void ValidateLowerHex(string value, int expectedLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != expectedLength ||
            value.Any(character =>
                character is not (>= '0' and <= '9') &&
                character is not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                $"Value must contain exactly {expectedLength} lowercase hexadecimal characters.",
                parameterName);
        }
    }
}
