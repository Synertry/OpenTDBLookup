using Avalonia;
using Serilog;
using Serilog.Events;

namespace OpenTDBLookup;

internal static class Program
{
    /// <summary>Resolved at startup; <see cref="App"/> reads it to attach Serilog to the logging pipeline.</summary>
    public static ILogger? RootLogger { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
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
