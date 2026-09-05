using LiveWall.Application.Sessions;
using System.Runtime.ExceptionServices;
using LiveWall.Domain.Displays;
using LiveWall.Domain.Layouts;
using LiveWall.Platform.Windows.Desktop;
using LiveWall.Platform.Windows.Displays;

namespace LiveWall.DesktopHostDiagnostics;

internal static class Program
{
    private const int DefaultColorBlockSeconds = 10;
    private const int MaximumColorBlockSeconds = 30;
    private static readonly uint[] TestColors =
    [
        0x00FF00FF,
        0x0000FFFF,
        0x00FFFF00,
        0x000080FF,
    ];

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            DiagnosticOptions options = DiagnosticOptions.Parse(args);
            using WindowsDisplayTopologySource displaySource = new();
            DisplayTopology topology = displaySource.Current;
            PrintTopology(topology);
            if (!options.ColorBlockEnabled)
            {
                return topology.Displays.Count > 0 ? 0 : 2;
            }

            return await RunColorBlockAsync(topology, options.DurationSeconds)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Color block diagnostic canceled; temporary windows were removed.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Desktop Host diagnostics failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static void PrintTopology(DisplayTopology topology)
    {
        Console.WriteLine($"Topology revision: {topology.Revision}");
        Console.WriteLine($"Displays: {topology.Displays.Count}");
        foreach (DisplayDescriptor display in topology.Displays)
        {
            Console.WriteLine(
                $"{display.Id} | {display.Bounds.X},{display.Bounds.Y} " +
                $"{display.Bounds.Width}x{display.Bounds.Height} | " +
                $"scale={display.ScaleFactor:0.##} | {display.RefreshRateHz} Hz | " +
                $"primary={display.IsPrimary}");
        }
    }

    private static async Task<int> RunColorBlockAsync(
        DisplayTopology topology,
        int durationSeconds)
    {
        if (topology.Displays.Count == 0)
        {
            return 2;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        WindowsShellSnapshot shell = new(
            Environment.OSVersion.VersionString,
            Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RaisedDesktopEnabled: false);
        await using WindowsDesktopHost desktopHost = new(shell);
        List<DesktopSurface> surfaces = [];
        List<DesktopColorBlockSession> colorBlocks = [];
        Exception? operationFailure = null;

        try
        {
            DesktopTopology desktopTopology = await desktopHost.EnsureTopologyAsync(
                    topology,
                    cancellation.Token)
                .ConfigureAwait(false);
            for (int index = 0; index < topology.Displays.Count; index++)
            {
                DisplayDescriptor display = topology.Displays[index];
                DesktopSurface surface = await desktopHost.CreateSurfaceAsync(
                        new SurfaceRequest([display.Id], display.Bounds, FitMode.Cover),
                        cancellation.Token)
                    .ConfigureAwait(false);
                surfaces.Add(surface);
                colorBlocks.Add(await DesktopColorBlockSession.CreateAsync(
                        surface.WindowHandle,
                        display.Bounds.Width,
                        display.Bounds.Height,
                        TestColors[index % TestColors.Length],
                        cancellation.Token)
                    .ConfigureAwait(false));
            }

            await desktopHost.ReplaceSurfacesAsync(
                    new SurfaceReplacement(
                        surfaces.Select(surface => surface.Id).ToArray(),
                        [],
                        desktopTopology.Revision),
                    cancellation.Token)
                .ConfigureAwait(false);
            Console.WriteLine(
                $"LIVEWALL TEST color blocks are active for {durationSeconds} seconds. " +
                "Confirm that desktop icons and the taskbar remain above them.");
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cancellation.Token)
                .ConfigureAwait(false);
            Console.WriteLine("Color block interval completed; cleaning up temporary windows.");
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        Console.CancelKeyPress -= cancelHandler;
        List<Exception> cleanupFailures = [];
        for (int index = colorBlocks.Count - 1; index >= 0; index--)
        {
            try
            {
                await colorBlocks[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        foreach (DesktopSurface surface in surfaces)
        {
            try
            {
                await desktopHost.DestroySurfaceAsync(surface.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            if (operationFailure is not null)
            {
                cleanupFailures.Insert(0, operationFailure);
            }

            throw new AggregateException(
                "One or more diagnostic windows could not be removed.",
                cleanupFailures);
        }

        if (operationFailure is not null)
        {
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        return 0;
    }

    private sealed record DiagnosticOptions(bool ColorBlockEnabled, int DurationSeconds)
    {
        public static DiagnosticOptions Parse(string[] args)
        {
            bool colorBlock = false;
            bool durationSpecified = false;
            int duration = DefaultColorBlockSeconds;
            for (int index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--color-block":
                        colorBlock = true;
                        break;
                    case "--duration-seconds" when index + 1 < args.Length:
                        durationSpecified = true;
                        if (!int.TryParse(
                                args[++index],
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out duration) ||
                            duration is < 1 or > MaximumColorBlockSeconds)
                        {
                            throw new ArgumentException(
                                $"Color block duration must be between 1 and {MaximumColorBlockSeconds} seconds.");
                        }

                        break;
                    default:
                        throw new ArgumentException($"Unknown diagnostics option '{args[index]}'.");
                }
            }

            if (!colorBlock && durationSpecified)
            {
                throw new ArgumentException("--duration-seconds requires --color-block.");
            }

            return new DiagnosticOptions(colorBlock, duration);
        }
    }
}
