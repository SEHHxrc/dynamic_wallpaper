using System.Runtime.Loader;
using LiveWall.Host.Bootstrap;
using LiveWall.Infrastructure.Ipc;

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

        using CancellationTokenSource lifetime = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };
        Action<AssemblyLoadContext> unloadingHandler = _ => lifetime.Cancel();
        Console.CancelKeyPress += cancelHandler;
        AssemblyLoadContext.Default.Unloading += unloadingHandler;
        try
        {
            WindowsHostRuntime runtime = WindowsHostCompositionRoot.Create(
                options.ConfigurationDirectory);
            runtime.StartAsync(lifetime.Token).GetAwaiter().GetResult();
            if (options.ValidateStartupOnly)
            {
                runtime.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                runtime.RunAsync(lifetime.Token).GetAwaiter().GetResult();
            }
            return runtime.LastFault is null ? 0 : 1;
        }
        catch
        {
            return 1;
        }
        finally
        {
            AssemblyLoadContext.Default.Unloading -= unloadingHandler;
            Console.CancelKeyPress -= cancelHandler;
        }
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

        return new HostProgramOptions(
            installationScope,
            configurationDirectory,
            validateStartupOnly);
    }
}

