using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace OpenTDBLookup.Services;

/// <summary>
/// Polls Avalonia's clipboard every 250 ms via a <see cref="DispatcherTimer"/>
/// and raises <see cref="ClipboardChanged"/> when the text differs from the
/// previous tick. The watcher only emits text changes; it does not match -
/// the ViewModel decides what to do on each emission.
/// </summary>
public sealed class ClipboardWatcher : IClipboardWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly ILogger<ClipboardWatcher> _logger;
    private readonly DispatcherTimer _timer;
    private string? _lastSeen;
    private bool _isReadingClipboard;

    public ClipboardWatcher(ILogger<ClipboardWatcher> logger)
    {
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += OnTick;
    }

    public event EventHandler<string>? ClipboardChanged;

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (_timer.IsEnabled)
        {
            return;
        }
        _lastSeen = null;
        _timer.Start();
        _logger.LogDebug("ClipboardWatcher started (poll interval {Interval}ms)", PollInterval.TotalMilliseconds);
    }

    public void Stop()
    {
        if (!_timer.IsEnabled)
        {
            return;
        }
        _timer.Stop();
        _lastSeen = null;
        _logger.LogDebug("ClipboardWatcher stopped");
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_isReadingClipboard)
        {
            return;
        }

        var clipboard = GetClipboard();
        if (clipboard is null)
        {
            return;
        }

        _isReadingClipboard = true;
        try
        {
            var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            if (text is null)
            {
                return;
            }
            if (string.Equals(text, _lastSeen, StringComparison.Ordinal))
            {
                return;
            }
            _lastSeen = text;
            ClipboardChanged?.Invoke(this, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard poll failed");
        }
        finally
        {
            _isReadingClipboard = false;
        }
    }

    /// <summary>
    /// Records that <paramref name="text"/> was just written to the clipboard
    /// by the application itself, so the next poll does not re-emit it as a
    /// change. The ViewModel calls this after writing the auto-answer.
    /// </summary>
    public void NotifyApplicationWrote(string text) => _lastSeen = text;

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }
        if (desktop.MainWindow is not { } window)
        {
            return null;
        }
        return TopLevel.GetTopLevel(window)?.Clipboard;
    }
}
