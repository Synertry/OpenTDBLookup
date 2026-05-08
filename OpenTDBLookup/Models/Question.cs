using System.Collections.Generic;

namespace OpenTDBLookup.Models;

/// <summary>
/// A single trivia question loaded from the OpenTDB cache.
/// </summary>
/// <remarks>
/// <see cref="Hash"/> is the 16-char lowercase hex of XxHash3-64 over the
/// <c>TextNormalizer.Normalize</c>-output of <see cref="QuestionText"/> and
/// is used as the primary lookup key. Original casing of the question text and
/// answers is preserved for display.
/// </remarks>
public sealed record Question(
    string Hash,
    int CategoryId,
    string Category,
    string Difficulty,
    string Type,
    string QuestionText,
    string CorrectAnswer,
    IReadOnlyList<string> IncorrectAnswers);
