namespace OpenTDBLookup.Services;

/// <summary>
/// Surfaces a short-lived in-corner toast that appears regardless of whether
/// the main window is visible, minimised, or hidden in the tray. Distinct from
/// the in-window status bar so the user can be told what happened even when
/// they are focused on another application.
/// </summary>
public interface IToastService
{
    /// <summary>
    /// Show a toast with the given title and body. If a previous toast is
    /// still on screen its content is replaced and the auto-dismiss timer
    /// restarts.
    /// </summary>
    /// <param name="title">Bold first line, kept short.</param>
    /// <param name="body">Multi-line body. Embedded <c>\n</c> separates lines.</param>
    /// <param name="duration">How long the toast stays visible. Defaults to a
    /// few seconds when null.</param>
    void Show(string title, string body, TimeSpan? duration = null);
}
