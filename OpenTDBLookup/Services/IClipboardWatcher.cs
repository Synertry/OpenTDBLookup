namespace OpenTDBLookup.Services;

/// <summary>
/// Polls the system clipboard for changes and emits the latest text via
/// <see cref="ClipboardChanged"/>. Both <see cref="Start"/> and
/// <see cref="Stop"/> are idempotent.
/// </summary>
public interface IClipboardWatcher
{
    /// <summary>Raised when the clipboard text changes from the previously seen value.</summary>
    event EventHandler<string>? ClipboardChanged;

    bool IsRunning { get; }

    /// <summary>Starts polling. Subsequent calls are no-ops while running.</summary>
    void Start();

    /// <summary>Stops polling and clears the last-seen text.</summary>
    void Stop();

    /// <summary>
    /// Records that <paramref name="text"/> was just written to the clipboard
    /// by the application itself, so the next poll does not re-emit it as a
    /// spurious change.
    /// </summary>
    void NotifyApplicationWrote(string text);
}
