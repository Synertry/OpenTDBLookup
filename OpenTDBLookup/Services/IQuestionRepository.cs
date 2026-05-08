using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// In-memory cache of every cached <see cref="Question"/>, plus the metadata
/// required to incrementally refresh from OpenTDB. Persisted to
/// <c>questions.json</c> next to the executable.
/// </summary>
public interface IQuestionRepository
{
    IReadOnlyDictionary<string, Question> ByHash { get; }
    IReadOnlyList<Question> All { get; }
    int Count { get; }
    DateTimeOffset? LastFullScrape { get; }
    DateTimeOffset? LastCountCheck { get; }
    IReadOnlyDictionary<int, int> CategoryVerifiedCounts { get; }

    /// <summary>
    /// Snapshot of normalized question text keyed by hash. Used by the matcher
    /// for the substring fallback so it does not have to re-normalize on every
    /// keystroke.
    /// </summary>
    IReadOnlyDictionary<string, string> NormalizedQuestionByHash { get; }

    /// <summary>Loads from <c>questions.json</c>; initializes empty on missing/corrupt file.</summary>
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically writes the current state to <c>questions.json</c>.</summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Adds questions whose hash is not yet present. Returns the number actually added.</summary>
    int Merge(IEnumerable<Question> incoming);

    void UpdateCategoryCounts(IReadOnlyDictionary<int, int> verifiedCounts);

    /// <summary>Sets both <see cref="LastFullScrape"/> and <see cref="LastCountCheck"/> to now (UTC).</summary>
    void MarkFullScrape();

    /// <summary>Sets <see cref="LastCountCheck"/> to now (UTC).</summary>
    void MarkCountCheck();
}
