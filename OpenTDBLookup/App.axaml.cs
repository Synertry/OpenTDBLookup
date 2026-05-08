using System;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Services;
using OpenTDBLookup.ViewModels;
using OpenTDBLookup.Views;
using Serilog;
using Serilog.Extensions.Logging;

namespace OpenTDBLookup;

public partial class App : Application
{
    /// <summary>The container is built in <see cref="OnFrameworkInitializationCompleted"/> and reused for the life of the process.</summary>
    public static IServiceProvider? Services { get; private set; }

    private TrayIcon? _trayIcon;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            // Tray icon is always installed, but only meaningful when the
            // user toggles "minimize to tray". Open/Quit/Toggle commands live
            // here at the App level - the window owns the actual behaviour.
            _trayIcon = BuildTrayIcon(window, vm, desktop);
            TrayIcon.SetIcons(this, [_trayIcon]);

            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Dispose();
                _trayIcon?.Dispose();
                Log.CloseAndFlush();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Bridge Serilog (configured in Program.cs) to Microsoft.Extensions.Logging
        // so anything resolved from DI gets a logger that writes to the same sinks.
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new SerilogLoggerProvider(Log.Logger, dispose: false));
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Singletons:
        // - HttpClient is shared so the OpenTdbClient's per-request gate covers every call
        // - OpenTdbClient holds the Stopwatch enforcing the 5s rate limit
        // - QuestionRepository owns the in-memory cache and the lock around it
        // - QuestionMatcher reads from the repository
        // - ClipboardWatcher manages the DispatcherTimer and last-seen text
        // - RefreshService orchestrates client + repo
        services.AddSingleton<HttpClient>(_ => new HttpClient());
        services.AddSingleton<IOpenTdbClient, OpenTdbClient>();
        services.AddSingleton<IQuestionRepository, QuestionRepository>();
        services.AddSingleton<IQuestionMatcher, QuestionMatcher>();
        services.AddSingleton<IClipboardWatcher, ClipboardWatcher>();
        services.AddSingleton<IRefreshService, RefreshService>();

        // ViewModel is transient: a fresh instance per window. Today there is
        // only one window, but transient avoids surprises if the design grows.
        services.AddTransient<MainWindowViewModel>();
    }

    private static TrayIcon BuildTrayIcon(MainWindow window, MainWindowViewModel vm, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var openCommand = new RelayCommand(window.RestoreFromTray);
        var toggleWatchCommand = new RelayCommand(() => vm.IsClipboardWatchEnabled = !vm.IsClipboardWatchEnabled);
        var quitCommand = new RelayCommand(() =>
        {
            // Hard-quit bypasses the close-to-tray cancel.
            vm.IsTrayEnabled = false;
            desktop.Shutdown();
        });

        var icon = new TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = "OpenTDBLookup",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items =
                {
                    new NativeMenuItem("Open") { Command = openCommand },
                    new NativeMenuItem("Toggle clipboard watch") { Command = toggleWatchCommand },
                    new NativeMenuItemSeparator(),
                    new NativeMenuItem("Quit") { Command = quitCommand },
                },
            },
        };
        icon.Clicked += (_, _) => window.RestoreFromTray();
        return icon;
    }

    private static WindowIcon LoadTrayIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://OpenTDBLookup/Assets/tray-icon.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load tray icon");
            // Fall back to a transparent in-memory placeholder rather than crash.
            using var fallback = new MemoryStream();
            return new WindowIcon(fallback);
        }
    }
}
