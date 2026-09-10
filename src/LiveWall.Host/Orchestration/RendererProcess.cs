using System.Diagnostics;

namespace LiveWall.Host.Orchestration;

internal sealed record RendererProcessStartRequest(
    string ExecutablePath,
    string PipeName,
    string SessionId,
    string AuthenticationToken,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

internal interface IRendererProcessLauncher
{
    IRendererProcessHandle Start(RendererProcessStartRequest request);
}

internal interface IRendererProcessHandle : IAsyncDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Terminate();
}

internal sealed class RendererProcessLauncher : IRendererProcessLauncher
{
    public IRendererProcessHandle Start(RendererProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProcessStartInfo startInfo = new()
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(request.PipeName);
        startInfo.ArgumentList.Add(request.SessionId);
        if (request.EnvironmentVariables is not null)
        {
            foreach ((string name, string value) in request.EnvironmentVariables)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                if (StringComparer.Ordinal.Equals(name, "LIVEWALL_RENDERER_AUTH_TOKEN"))
                {
                    throw new InvalidOperationException(
                        "Renderer process environment cannot override the authentication token.");
                }
                startInfo.Environment[name] = value;
            }
        }
        startInfo.Environment["LIVEWALL_RENDERER_AUTH_TOKEN"] = request.AuthenticationToken;
        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The Renderer process could not be started.");
        return new RendererProcessHandle(process);
    }
}

internal sealed class RendererProcessHandle(Process process) : IRendererProcessHandle
{
    private readonly Process process = process ?? throw new ArgumentNullException(nameof(process));

    public int Id => process.Id;

    public bool HasExited => process.HasExited;

    public int? ExitCode => process.HasExited ? process.ExitCode : null;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public void Terminate()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
