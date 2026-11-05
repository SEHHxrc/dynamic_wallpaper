using System.Runtime.Loader;
using LiveWall.Host.Bootstrap;
using LiveWall.Infrastructure.Ipc;
using LiveWall.Platform.Windows.Lifecycle;
using LiveWall.Contracts.Ipc;
using LiveWall.Infrastructure.FileSystem;

namespace LiveWall.Host;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        HostProgramOptions options;
        try
        {
            options = HostProgramOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            return 64;
        }

        using HostInstanceMutex? instance = HostInstanceMutex.TryAcquire(options.InstallationScope);
        if (instance is null)
        {
            return 2;
        }

        return RunHost(options);
    }

    private static int RunHost(HostProgramOptions options)
    {
        WindowsHostRuntime? runtime = null;
        HostProcessLifetime? lifetime = null;
        WindowsProcessExitMonitor? exitMonitor = null;
        WindowsProcessShutdownOrder? shutdownOrder = null;
        int exitCode = 1;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _ = lifetime?.RequestStop("ConsoleCancel");
        };
        Action<AssemblyLoadContext> unloadingHandler = _ =>
        {
            try { lifetime?.RequestStop("ProcessUnloading").Wait(TimeSpan.FromSeconds(4)); }
            catch (Exception) { /* No exception may escape a native process-exit callback. */ }
        };
        Console.CancelKeyPress += cancelHandler;
        AssemblyLoadContext.Default.Unloading += unloadingHandler;
        try
        {
            shutdownOrder = WindowsProcessShutdownOrder.ForHost();
            runtime = WindowsHostCompositionRoot.Create(
                options.ConfigurationDirectory);
            lifetime = new HostProcessLifetime(runtime, receipt => AtomicFileWriter.WriteJsonAsync(
                Path.Combine(options.ConfigurationDirectory, "host-exit-receipt.json"), receipt),
                (receipt, token) => AtomicFileWriter.WriteJsonAsync(
                    Path.Combine(options.ConfigurationDirectory, "host-native-exit-receipt.json"), receipt, cancellationToken: token));
            exitMonitor = new WindowsProcessExitMonitor(lifetime.RequestStop,
                persistObservationAsync: lifetime.PersistNativeObservationAsync);
            exitMonitor.Ready.GetAwaiter().GetResult();
            runtime.StartAsync(lifetime.Token).GetAwaiter().GetResult();
            if (!lifetime.IsStopRequested) exitMonitor.MarkRunning();
            Console.WriteLine("HOST_RUNNING");
            if (options.ValidateStartupOnly)
            {
                runtime.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                runtime.RunAsync(lifetime.Token).GetAwaiter().GetResult();
            }
            exitCode = runtime.LastFault is null ? 0 : 1;
        }
        catch (OperationCanceledException) when (lifetime?.IsStopRequested == true)
        {
            exitCode = runtime?.LastFault is null ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Host failed: {exception.GetType().Name}: {exception.Message}");
            exitCode = 1;
        }
        finally
        {
            AssemblyLoadContext.Default.Unloading -= unloadingHandler;
            Console.CancelKeyPress -= cancelHandler;
            try
            {
                if (lifetime is not null) lifetime.RequestStop("ProgramExit").GetAwaiter().GetResult();
                else runtime?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception) { exitCode = 1; }
            if (runtime?.LastFault is not null) exitCode = 1;
            try { exitMonitor?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { exitCode = 1; }
            if (exitMonitor?.WaitOutcome is WindowsExitWaitOutcome.Failed or WindowsExitWaitOutcome.TimedOut)
                exitCode = 1;
            try { shutdownOrder?.Dispose(); }
            catch (Exception) { exitCode = 1; }
            Console.WriteLine($"HOST_STOPPED reason={lifetime?.Reason} | windowWait={exitMonitor?.WaitOutcome} | exitCode={exitCode}");
            lifetime?.Dispose();
        }
        return exitCode;
    }
}

internal sealed record HostProgramOptions(
    string InstallationScope,
    string ConfigurationDirectory,
    bool ValidateStartupOnly)
{
    public static HostProgramOptions Parse(IReadOnlyList<string> args)
    {
        string installationScope = "default";
        string configurationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWall");
        bool validateStartupOnly = false;
        for (int index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--installation-scope" when index + 1 < args.Count:
                    installationScope = args[++index];
                    break;
                case "--configuration-directory" when index + 1 < args.Count:
                    configurationDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "--validate-startup":
                    validateStartupOnly = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete Host argument '{args[index]}'.");
            }
        }

        IpcEndpointNames.ValidateInstallationScope(installationScope);
        return new HostProgramOptions(
            installationScope,
            configurationDirectory,
            validateStartupOnly);
    }
}

