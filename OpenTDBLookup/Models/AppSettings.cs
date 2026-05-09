namespace OpenTDBLookup.Models;

/// <summary>
/// Persisted user preferences. Lives next to <c>questions.json</c> as
/// <c>settings.json</c> so a portable install carries its toggles with it.
/// </summary>
public sealed record AppSettings(
    bool ClipboardWatchEnabled = false,
    bool ToastNotificationsEnabled = false,
    bool TrayEnabled = false);
