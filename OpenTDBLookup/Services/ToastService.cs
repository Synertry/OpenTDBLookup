using System;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Views;

namespace OpenTDBLookup.Services;

/// <summary>
/// Avalonia-window based toast that lives independent of the main window. A
/// single hidden <see cref="ToastWindow"/> is reused across calls; we just
/// swap content and reset the dismiss timer. The window has
/// <c>ShowActivated=false</c> + <c>Focusable=false</c> so showing it does not
/// steal focus from whatever app the user is currently typing into.
/// </summary>
public sealed class ToastService : IToastService, IDisposable
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);
    private const int EdgePadding = 16;

    private readonly ILogger<ToastService> _logger;
    private ToastWindow? _window;
    private DispatcherTimer? _hideTimer;
    private bool _disposed;

    public ToastService(ILogger<ToastService> logger) => _logger = logger;

    public void Show(string title, string body, TimeSpan? duration = null)
    {
        if (_disposed) { return; }
        var dur = duration ?? DefaultDuration;
        // Marshal to UI thread - Show may be called from a clipboard-watch
        // tick, which already runs there, but callers from background tasks
        // (e.g. refresh-finished hooks) must be safe too.
        Dispatcher.UIThread.Post(() => ShowOnUI(title, body, dur));
    }

    private void ShowOnUI(string title, string body, TimeSpan duration)
    {
        try
        {
            _window ??= new ToastWindow();
            _window.SetContent(title, body);
            PositionBottomRight(_window);
            // Show first then re-assert Topmost; Avalonia clears Topmost when a
            // window has just been hidden in some scenarios.
            _window.Show();
            _window.Topmost = true;

            _hideTimer?.Stop();
            _hideTimer = new DispatcherTimer { Interval = duration };
            _hideTimer.Tick += OnHideTick;
            _hideTimer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show toast notification");
        }
    }

    private void OnHideTick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer t)
        {
            t.Stop();
            t.Tick -= OnHideTick;
        }
        try { _window?.Hide(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to hide toast notification"); }
    }

    private static void PositionBottomRight(ToastWindow window)
    {
        var screens = window.Screens;
        var screen = screens.Primary
            ?? (screens.All.Count > 0 ? screens.All[0] : null);
        if (screen is null) { return; }

        var area = screen.WorkingArea;
        var width = (int)window.Width;
        var height = (int)window.Height;
        window.Position = new PixelPoint(
            area.X + area.Width - width - EdgePadding,
            area.Y + area.Height - height - EdgePadding);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;

        if (Dispatcher.UIThread.CheckAccess())
        {
            DisposeOnUI();
        }
        else
        {
            try { Dispatcher.UIThread.Invoke(DisposeOnUI); }
            catch (Exception ex) { _logger.LogWarning(ex, "Toast service disposal raised"); }
        }
    }

    private void DisposeOnUI()
    {
        if (_hideTimer is not null)
        {
            _hideTimer.Stop();
            _hideTimer.Tick -= OnHideTick;
            _hideTimer = null;
        }
        if (_window is not null)
        {
            try { _window.Close(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to close toast window during dispose"); }
            _window = null;
        }
    }
}
