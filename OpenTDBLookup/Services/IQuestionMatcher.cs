using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Maps free-text user input to a cached <see cref="Question"/> via exact
/// hash lookup first, then a longest-match substring fallback.
/// </summary>
public interface IQuestionMatcher
{
    /// <summary>
    /// Returns the best matching <see cref="Question"/> for
    /// <paramref name="input"/>, or <c>null</c> if no match was found.
    /// </summary>
    Question? Match(string? input);

    /// <summary>
    /// Exposes the normalized form of the most recent input so the UI can
    /// show what was actually searched on a no-match.
    /// </summary>
    string? NormalizeForPreview(string? input);
}
