using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenTDBLookup.Services;

/// <summary>
/// Per-tick payload sent from <see cref="IRefreshService"/> to a progress
/// reporter. <see cref="PercentComplete"/> is on a 0-100 scale.
/// </summary>
public sealed record ScrapeProgress(
    string CurrentCategory,
    string CurrentDifficulty,
    int ApiCallsMade,
    int ApiCallsCeiling,
    int QuestionsAdded,
    double PercentComplete);

/// <summary>
/// Summary returned at the end of a refresh run. <see cref="HitCeiling"/> is
/// true if the run aborted because <c>MaxApiCallsPerSession</c> was reached.
/// </summary>
public sealed record RefreshResult(
    int QuestionsAdded,
    int ApiCalls,
    bool HitCeiling,
    TimeSpan Duration);

/// <summary>
/// Orchestrates the OpenTDB client and the question repository to keep the
/// local cache up to date.
/// </summary>
public interface IRefreshService
{
    /// <summary>Full sweep across every (category, difficulty) bucket.</summary>
    Task<RefreshResult> InitialScrapeAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Compares verified counts to the stored counts and only re-scrapes the
    /// categories that grew.
    /// </summary>
    Task<RefreshResult> IncrementalRefreshAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken);

    /// <summary>True when the cache has never been refreshed or the last check is older than 7 days.</summary>
    Task<bool> ShouldRefreshAsync();
}
