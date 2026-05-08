using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace OpenTDBLookup.Helpers;

/// <summary>
/// Resolves Avalonia's <see cref="IClipboard"/> from the current desktop main
/// window. Centralized so both the watcher and the ViewModel can read/write
/// clipboard text without duplicating the lifetime walk.
/// </summary>
public static class ClipboardAccessor
{
    public static IClipboard? Current
    {
        get
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
}
