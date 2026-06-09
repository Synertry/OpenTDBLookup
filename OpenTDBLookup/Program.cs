using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Serilog;
using Serilog.Events;

namespace OpenTDBLookup;

internal static class Program
{
    /// <summary>Resolved at startup; <see cref="App"/> reads it to attach Serilog to the logging pipeline.</summary>
    public static ILogger? RootLogger { get; private set; }

    // WinExe binaries detach from the parent console on Windows, so Console.WriteLine
    // writes to a null sink. AttachConsole(ATTACH_PARENT_PROCESS) re-binds stdout/stderr
    // to the calling shell so `OpenTDBLookup.exe --version` actually prints something.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
    private const int AttachParentProcess = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        // Handle --version / -v before any Avalonia or Serilog setup so the
        // flag works as a fast CLI query (no log files written, no window
        // shown). Matches the release pipeline's /p:Version injection so the
        // shipped binary reports the tag it was built from.
        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            if (OperatingSystem.IsWindows())
            {
                AttachConsole(AttachParentProcess);
            }
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0-dev";
            Console.WriteLine($"OpenTDBLookup v{version}");
            return 0;
        }

        ConfigureSerilog();

        try
        {
            Log.Information("OpenTDBLookup starting up");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Log.Information("OpenTDBLookup exited cleanly");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception during startup");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static void ConfigureSerilog()
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("Application", "OpenTDBLookup")
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(logsDir, "opentdb-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        RootLogger = Log.Logger;
    }
}
