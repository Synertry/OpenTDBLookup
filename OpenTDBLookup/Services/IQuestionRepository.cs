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
    /// Number of OpenTDB-side within-bucket text duplicates observed at the
    /// last successful refresh. Used by the UI to display
    /// <c>stored / (target - knownDuplicates)</c> so the user does not see a
    /// permanent gap caused by OpenTDB listing the same question twice in
    /// one (cat, diff). Updated only on a refresh that completed without
    /// hitting the API ceiling.
    /// </summary>
    int KnownDuplicateCount { get; }

    /// <summary>
    /// Per-category-id count of questions actually stored in the cache. Computed
    /// from the in-memory dictionary on demand. Distinct from
    /// <see cref="CategoryVerifiedCounts"/> which mirrors what
    /// <c>api_count_global.php</c> reported during the last refresh - the
    /// "target" count, not what we successfully fetched and stored.
    /// </summary>
    IReadOnlyDictionary<int, int> GetCachedCountsByCategory();

    /// <summary>
    /// Per-(category, difficulty) count of questions actually stored. Lets
    /// the refresh service ask <c>api.php</c> for exactly the missing slice
    /// of each bucket instead of probing 50/25/12 to discover the size.
    /// </summary>
    IReadOnlyDictionary<(int CategoryId, string Difficulty), int> GetCachedCountsByCategoryDifficulty();

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

    /// <summary>
    /// Records the count of unfetchable-but-API-reported questions observed
    /// after a complete refresh. The refresh service computes
    /// <c>verifiedTotal - storedCount</c> and passes it here only when the
    /// run finished without hitting the call ceiling.
    /// </summary>
    void RecordKnownDuplicateCount(int count);

    /// <summary>Sets both <see cref="LastFullScrape"/> and <see cref="LastCountCheck"/> to now (UTC).</summary>
    void MarkFullScrape();

    /// <summary>Sets <see cref="LastCountCheck"/> to now (UTC).</summary>
    void MarkCountCheck();
}
