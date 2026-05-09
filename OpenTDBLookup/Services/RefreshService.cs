using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Default refresh orchestrator. Honors a per-session API call ceiling so a
/// single run cannot accidentally hammer OpenTDB.
/// </summary>
public sealed class RefreshService : IRefreshService
{
    private const int MaxApiCallsPerSession = 250;
    private const int RequestBatchSize = 50;
    private static readonly string[] Difficulties = ["easy", "medium", "hard"];
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    private readonly IOpenTdbClient _client;
    private readonly IQuestionRepository _repository;
    private readonly ILogger<RefreshService> _logger;

    public RefreshService(IOpenTdbClient client, IQuestionRepository repository, ILogger<RefreshService> logger)
    {
        _client = client;
        _repository = repository;
        _logger = logger;
    }

    public Task<bool> ShouldRefreshAsync()
    {
        var lastCheck = _repository.LastCountCheck;
        if (lastCheck is null)
        {
            return Task.FromResult(true);
        }
        return Task.FromResult(DateTimeOffset.UtcNow - lastCheck.Value > StaleAfter);
    }

    public async Task<RefreshResult> InitialScrapeAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var apiCalls = 0;
        var added = 0;
        var hitCeiling = false;
        var perBucketFetched = new Dictionary<(int CategoryId, string Difficulty), int>();
        IReadOnlyList<OpenTdbCategoryItem>? categoriesScraped = null;
        IReadOnlyDictionary<int, int>? verifiedCountsForSummary = null;

        try
        {
            var token = await _client.RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;
            var categories = await _client.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;
            var verifiedCounts = await _client.GetVerifiedCountsAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;
            categoriesScraped = categories;
            verifiedCountsForSummary = verifiedCounts;

            // Pre-fetch per-(category, difficulty) verified counts so each
            // api.php request can be sized to the bucket. Without this the
            // first call for a small bucket (e.g. cat 13 easy with 10 verified
            // questions) returns response_code 1 with empty results when we
            // ask for amount=50 - OpenTDB refuses partial fulfilment when
            // amount > available_unseen.
            var perDiffTargets = await PrefetchPerDifficultyTargetsAsync(
                categories, verifiedCounts, cancellationToken).ConfigureAwait(false);
            apiCalls += perDiffTargets.PrefetchCalls;

            var perDiffStored = _repository.GetCachedCountsByCategoryDifficulty();

            (added, apiCalls, hitCeiling, token) = await ScrapeAllBucketsAsync(
                categories,
                perDiffTargets.Targets,
                perDiffStored,
                token,
                apiCalls,
                added,
                progress,
                perBucketFetched,
                cancellationToken).ConfigureAwait(false);

            if (hitCeiling)
            {
                _logger.LogWarning("API call ceiling reached after {Calls} calls; saving partial scrape", apiCalls);
            }

            _repository.UpdateCategoryCounts(verifiedCounts);
            if (!hitCeiling)
            {
                _repository.MarkFullScrape();
                RecordDuplicateCountIfStable(verifiedCounts);
            }
            else
            {
                _repository.MarkCountCheck();
            }
            await _repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Initial scrape cancelled after {Calls} calls and {Added} new questions; saving partial state", apiCalls, added);
            if (categoriesScraped is not null && verifiedCountsForSummary is not null)
            {
                LogScrapeSummary("initial scrape (cancelled)", categoriesScraped, verifiedCountsForSummary, perBucketFetched, added);
            }
            await TrySavePartialAsync().ConfigureAwait(false);
            throw;
        }

        if (categoriesScraped is not null && verifiedCountsForSummary is not null)
        {
            LogScrapeSummary("initial scrape", categoriesScraped, verifiedCountsForSummary, perBucketFetched, added);
        }

        sw.Stop();
        return new RefreshResult(added, apiCalls, hitCeiling, sw.Elapsed);
    }

    public async Task<RefreshResult> IncrementalRefreshAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var apiCalls = 0;
        var added = 0;
        var hitCeiling = false;
        var perBucketFetched = new Dictionary<(int CategoryId, string Difficulty), int>();
        List<OpenTdbCategoryItem>? changedCategoriesForSummary = null;
        IReadOnlyDictionary<int, int>? verifiedCountsForSummary = null;

        try
        {
            var verifiedCounts = await _client.GetVerifiedCountsAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;

            // Compare API-reported verified counts against what we *actually*
            // have stored, not against the snapshot of API counts we recorded
            // last refresh. The old comparison ("api count > stored snapshot")
            // missed buckets where we never managed to fetch the questions in
            // the first place - the snapshot equalled the API count even when
            // our cache was empty for that category.
            var cachedCounts = _repository.GetCachedCountsByCategory();
            var changed = new List<int>();
            var totalGap = 0;
            foreach (var (categoryId, target) in verifiedCounts)
            {
                var actual = cachedCounts.GetValueOrDefault(categoryId, 0);
                if (target > actual)
                {
                    changed.Add(categoryId);
                    totalGap += target - actual;
                }
            }

            if (changed.Count == 0)
            {
                _logger.LogInformation(
                    "Incremental refresh: every category at or above its verified target; updating last-check timestamp only");
                _repository.UpdateCategoryCounts(verifiedCounts);
                _repository.MarkCountCheck();
                await _repository.SaveAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return new RefreshResult(0, apiCalls, false, sw.Elapsed);
            }

            _logger.LogInformation(
                "Incremental refresh: {ChangedCount} categories under target ({TotalGap} questions short) - refreshing",
                changed.Count, totalGap);

            var categoriesById = new Dictionary<int, OpenTdbCategoryItem>();
            foreach (var c in await _client.GetCategoriesAsync(cancellationToken).ConfigureAwait(false))
            {
                categoriesById[c.Id] = c;
            }
            apiCalls++;

            var token = await _client.RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;

            var changedCategories = new List<OpenTdbCategoryItem>(changed.Count);
            foreach (var categoryId in changed)
            {
                if (!categoriesById.TryGetValue(categoryId, out var category))
                {
                    _logger.LogWarning("Verified-count map referenced unknown category {CategoryId}; skipping", categoryId);
                    continue;
                }
                var actualForCat = cachedCounts.GetValueOrDefault(categoryId, 0);
                var targetForCat = verifiedCounts.GetValueOrDefault(categoryId, 0);
                _logger.LogInformation(
                    "Refreshing cat {CategoryId} ({CategoryName}): actual={Actual} target={Target} gap={Gap}",
                    category.Id, category.Name, actualForCat, targetForCat, targetForCat - actualForCat);
                changedCategories.Add(category);
            }
            changedCategoriesForSummary = changedCategories;
            verifiedCountsForSummary = verifiedCounts;

            // Pre-fetch per-(cat, diff) for changed categories only - on a
            // recovery refresh of all 24 cats this still adds ~24 calls but
            // typically it's far fewer (only categories where stored < target).
            var perDiffTargets = await PrefetchPerDifficultyTargetsAsync(
                changedCategories, verifiedCounts, cancellationToken).ConfigureAwait(false);
            apiCalls += perDiffTargets.PrefetchCalls;

            var perDiffStored = _repository.GetCachedCountsByCategoryDifficulty();

            (added, apiCalls, hitCeiling, token) = await ScrapeAllBucketsAsync(
                changedCategories,
                perDiffTargets.Targets,
                perDiffStored,
                token,
                apiCalls,
                added,
                progress,
                perBucketFetched,
                cancellationToken).ConfigureAwait(false);

            if (hitCeiling)
            {
                _logger.LogWarning("Incremental refresh hit API ceiling after {Calls} calls", apiCalls);
            }

            _repository.UpdateCategoryCounts(verifiedCounts);
            _repository.MarkCountCheck();
            if (!hitCeiling)
            {
                RecordDuplicateCountIfStable(verifiedCounts);
            }
            await _repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Incremental refresh cancelled after {Calls} calls and {Added} new questions; saving partial state", apiCalls, added);
            if (changedCategoriesForSummary is not null && verifiedCountsForSummary is not null)
            {
                LogScrapeSummary("incremental refresh (cancelled)", changedCategoriesForSummary, verifiedCountsForSummary, perBucketFetched, added);
            }
            await TrySavePartialAsync().ConfigureAwait(false);
            throw;
        }

        if (changedCategoriesForSummary is not null && verifiedCountsForSummary is not null)
        {
            LogScrapeSummary("incremental refresh", changedCategoriesForSummary, verifiedCountsForSummary, perBucketFetched, added);
        }

        sw.Stop();
        return new RefreshResult(added, apiCalls, hitCeiling, sw.Elapsed);
    }

    /// <summary>
    /// Calls <c>api_count.php</c> for every category with a non-zero verified
    /// count and returns the per-difficulty target plus the number of calls
    /// actually consumed (so the caller can charge them against the session
    /// ceiling).
    /// </summary>
    private async Task<(Dictionary<(int CategoryId, string Difficulty), int> Targets, int PrefetchCalls)>
        PrefetchPerDifficultyTargetsAsync(
            IReadOnlyList<OpenTdbCategoryItem> categories,
            IReadOnlyDictionary<int, int> verifiedCounts,
            CancellationToken cancellationToken)
    {
        var targets = new Dictionary<(int, string), int>(categories.Count * Difficulties.Length);
        var calls = 0;

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Skip cats that have zero verified - api_count.php would return
            // 0/0/0 and we'd burn a 5-second slot for no information.
            if (verifiedCounts.GetValueOrDefault(category.Id, 0) <= 0)
            {
                continue;
            }
            var totals = await _client.GetCategoryDifficultyTotalsAsync(
                category.Id, cancellationToken).ConfigureAwait(false);
            calls++;
            targets[(category.Id, "easy")] = totals.Easy;
            targets[(category.Id, "medium")] = totals.Medium;
            targets[(category.Id, "hard")] = totals.Hard;
        }
        return (targets, calls);
    }

    /// <summary>
    /// Iterates every (category, difficulty) bucket in <paramref name="categories"/>,
    /// calling <see cref="ScrapeBucketAsync"/> with the size remaining between
    /// the per-bucket target and what is already cached. Buckets where stored
    /// already meets target are skipped without spending an API call.
    /// </summary>
    private async Task<(int Added, int ApiCalls, bool HitCeiling, string Token)> ScrapeAllBucketsAsync(
        IReadOnlyList<OpenTdbCategoryItem> categories,
        IReadOnlyDictionary<(int CategoryId, string Difficulty), int> perDiffTargets,
        IReadOnlyDictionary<(int CategoryId, string Difficulty), int> perDiffStored,
        string token,
        int apiCallsSoFar,
        int addedSoFar,
        IProgress<ScrapeProgress>? progress,
        Dictionary<(int CategoryId, string Difficulty), int> perBucketFetched,
        CancellationToken cancellationToken)
    {
        var apiCalls = apiCallsSoFar;
        var added = addedSoFar;
        var hitCeiling = false;
        var totalBuckets = categories.Count * Difficulties.Length;
        var bucketIdx = 0;

        foreach (var category in categories)
        {
            foreach (var difficulty in Difficulties)
            {
                bucketIdx++;
                cancellationToken.ThrowIfCancellationRequested();

                if (apiCalls >= MaxApiCallsPerSession)
                {
                    hitCeiling = true;
                    break;
                }

                var target = perDiffTargets.GetValueOrDefault((category.Id, difficulty), 0);
                var stored = perDiffStored.GetValueOrDefault((category.Id, difficulty), 0);

                if (target <= 0)
                {
                    // Nothing to fetch for this bucket per OpenTDB's own books.
                    perBucketFetched[(category.Id, difficulty)] = 0;
                    continue;
                }
                if (stored >= target)
                {
                    _logger.LogDebug(
                        "Skipping bucket cat={CategoryId} ({CategoryName}) {Difficulty}: stored={Stored} >= target={Target}",
                        category.Id, category.Name, difficulty, stored, target);
                    perBucketFetched[(category.Id, difficulty)] = 0;
                    continue;
                }

                // Always fetch the full bucket when there is any gap. The
                // alternative ("ask only for the missing N") fails because
                // OpenTDB hands back N random unseen questions, and with a
                // mostly-cached bucket the random subset overlaps almost
                // entirely with what we already have - dedup nets ~zero new.
                // Pulling the whole bucket and letting Merge dedup catches
                // the long tail of missing questions in 1-2 retries.
                progress?.Report(new ScrapeProgress(
                    category.Name,
                    difficulty,
                    apiCalls,
                    MaxApiCallsPerSession,
                    added,
                    Percent(bucketIdx, totalBuckets)));

                var (bucketFetched, bucketAdded, callsConsumed, refreshedToken) = await ScrapeBucketAsync(
                    category, difficulty, target, token, apiCalls, cancellationToken).ConfigureAwait(false);
                perBucketFetched[(category.Id, difficulty)] = bucketFetched;
                added += bucketAdded;
                apiCalls += callsConsumed;
                token = refreshedToken;

                if (apiCalls >= MaxApiCallsPerSession)
                {
                    hitCeiling = true;
                    break;
                }
            }
            if (hitCeiling)
            {
                break;
            }
        }

        return (added, apiCalls, hitCeiling, token);
    }

    private async Task<(int Fetched, int Added, int CallsConsumed, string Token)> ScrapeBucketAsync(
        OpenTdbCategoryItem category,
        string difficulty,
        int initialTarget,
        string token,
        int apiCallsSoFar,
        CancellationToken cancellationToken)
    {
        var bucketResults = new List<OpenTdbQuestionResult>();
        var callsConsumed = 0;
        var bucketBusy = true;
        var remaining = initialTarget;
        // First request is sized off the known per-difficulty target so a
        // bucket of e.g. 10 verified questions is asked for amount=10 directly
        // instead of the previous amount=50 -> code 1 -> halve probe path.
        // Halving stays as a fallback inside the switch in case api_count.php
        // and api.php disagree (a question being added or removed between the
        // two calls is the realistic case).
        var batchSize = Math.Min(RequestBatchSize, Math.Max(1, remaining));

        while (bucketBusy && (apiCallsSoFar + callsConsumed) < MaxApiCallsPerSession)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (code, results) = await _client.FetchQuestionsAsync(
                batchSize, category.Id, difficulty, token, cancellationToken).ConfigureAwait(false);
            callsConsumed++;

            switch (code)
            {
                case 0:
                    bucketResults.AddRange(results);
                    remaining = Math.Max(0, remaining - results.Count);
                    if (remaining == 0 || results.Count == 0)
                    {
                        // We've collected the whole target, or OpenTDB returned
                        // an empty page. Either way, stop. (Empty-on-code-0
                        // shouldn't happen per docs but the guard prevents a
                        // hot loop if the API ever does it.)
                        bucketBusy = false;
                    }
                    else if (results.Count < batchSize)
                    {
                        // Short page mid-bucket: most likely the tail (next
                        // call will be code 4) but it can also be a transient
                        // shrink. Keep asking with the smaller batch so we
                        // don't drop questions to a one-shot quirk; an actual
                        // exhausted bucket exits cleanly on the next code 4.
                        batchSize = Math.Max(1, Math.Min(results.Count, remaining));
                    }
                    else
                    {
                        batchSize = Math.Min(RequestBatchSize, remaining);
                    }
                    break;
                case 1:
                    if (batchSize > 1)
                    {
                        var prev = batchSize;
                        // Snap to min(half, remaining) so a small remaining
                        // doesn't burn extra halving steps (50 -> 25 -> 12 ->
                        // ... when only 5 are actually wanted).
                        batchSize = Math.Max(1, Math.Min(batchSize / 2, Math.Max(1, remaining)));
                        _logger.LogDebug(
                            "Code 1 fallback for cat {CategoryId} ({CategoryName}) {Difficulty} amount={Prev}; halving to amount={New}",
                            category.Id, category.Name, difficulty, prev, batchSize);
                    }
                    else
                    {
                        // amount=1 also returned code 1: the bucket is genuinely
                        // empty/exhausted (or the entire pool is already seen by
                        // this token, but that case returns code 4).
                        bucketBusy = false;
                    }
                    break;
                case 3:
                    // Token not found (e.g. expired after 6h idle). Request
                    // a fresh one and retry the bucket from where we left off.
                    if ((apiCallsSoFar + callsConsumed) >= MaxApiCallsPerSession)
                    {
                        bucketBusy = false;
                        break;
                    }
                    token = await _client.RequestTokenAsync(cancellationToken).ConfigureAwait(false);
                    callsConsumed++;
                    break;
                case 4:
                    // Bucket exhausted - the token has returned every question
                    // matching this (category, difficulty) query. Move on to
                    // the next bucket. Resetting or replacing the token here
                    // would wipe the cross-bucket "seen" list and force us to
                    // re-fetch the same questions on the very next request.
                    bucketBusy = false;
                    break;
                default:
                    _logger.LogWarning("Unexpected response_code {Code} for category {Category} {Difficulty}; skipping bucket", code, category.Name, difficulty);
                    bucketBusy = false;
                    break;
            }
        }

        var mapped = new List<Question>(bucketResults.Count);
        foreach (var raw in bucketResults)
        {
            mapped.Add(MapToQuestion(raw, category.Id, category.Name, difficulty));
        }
        var fetched = bucketResults.Count;
        var added = _repository.Merge(mapped);
        // Per-bucket detail goes to Debug so the daily log can be inspected
        // post-hoc. The dropped count (fetched - added) reveals hash collisions
        // where the same question text was already in the cache under another
        // category. The target/fetched ratio reveals OpenTDB-side gaps.
        _logger.LogDebug(
            "Bucket cat={CategoryId} ({CategoryName}) {Difficulty}: target={Target} fetched={Fetched} added={Added} dropped={Dropped} calls={Calls}",
            category.Id, category.Name, difficulty, initialTarget, fetched, added, fetched - added, callsConsumed);
        return (fetched, added, callsConsumed, token);
    }

    private static Question MapToQuestion(OpenTdbQuestionResult raw, int categoryId, string categoryName, string difficulty)
    {
        var hash = HashHelper.ComputeHash(TextNormalizer.Normalize(raw.Question));
        return new Question(
            hash,
            categoryId,
            categoryName,
            difficulty,
            raw.Type,
            raw.Question,
            raw.CorrectAnswer,
            [.. raw.IncorrectAnswers]);
    }

    private async Task TrySavePartialAsync()
    {
        try
        {
            await _repository.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // Narrow catch: only swallow expected I/O / serialization failures.
            // Anything else (e.g. a bug in our own state) should bubble out
            // rather than disappear during cleanup.
            _logger.LogError(ex, "Failed to save partial cache during cancellation cleanup");
        }
    }

    private static double Percent(int idx, int total) =>
        total <= 0 ? 0d : Math.Clamp((double)idx / total * 100d, 0d, 100d);

    /// <summary>
    /// Computes how many questions OpenTDB reports as verified that we could
    /// not fetch (within-bucket text duplicates - same normalized question
    /// text listed twice in the same (cat, diff) on OpenTDB's side). The UI
    /// uses this to subtract those phantom-missing entries from the displayed
    /// verified target so the count reads X / X instead of X / X+dups.
    /// </summary>
    private void RecordDuplicateCountIfStable(IReadOnlyDictionary<int, int> verifiedCounts)
    {
        var totalVerified = 0;
        foreach (var v in verifiedCounts.Values) { totalVerified += v; }
        var dupCount = Math.Max(0, totalVerified - _repository.Count);
        _repository.RecordKnownDuplicateCount(dupCount);
        if (dupCount > 0)
        {
            _logger.LogInformation(
                "Refresh complete: {DupCount} OpenTDB-side within-bucket text duplicates detected (target {Target}, stored {Stored})",
                dupCount, totalVerified, _repository.Count);
        }
    }

    /// <summary>
    /// Per-category and overall fetched-vs-verified breakdown. Useful for
    /// diagnosing the gap between OpenTDB's reported verified count
    /// (<c>api_count_global.php</c>) and the question pool actually returned
    /// by <c>api.php</c> under a session token. Logged at Information so it
    /// shows up in default deployments without a Debug-level switch.
    /// </summary>
    private void LogScrapeSummary(
        string label,
        IReadOnlyList<OpenTdbCategoryItem> categories,
        IReadOnlyDictionary<int, int> verifiedCounts,
        IReadOnlyDictionary<(int CategoryId, string Difficulty), int> perBucketFetched,
        int distinctAdded)
    {
        var totalFetched = 0;
        var totalVerified = 0;

        foreach (var category in categories)
        {
            var fetched = 0;
            foreach (var diff in Difficulties)
            {
                if (perBucketFetched.TryGetValue((category.Id, diff), out var count))
                {
                    fetched += count;
                }
            }
            var verified = verifiedCounts.GetValueOrDefault(category.Id, 0);
            totalFetched += fetched;
            totalVerified += verified;
            _logger.LogInformation(
                "Scrape detail cat={CategoryId} ({CategoryName}): fetched={Fetched} verified={Verified} gap={Gap}",
                category.Id, category.Name, fetched, verified, verified - fetched);
        }

        var recall = totalVerified > 0 ? (double)totalFetched / totalVerified : 0d;
        _logger.LogInformation(
            "Scrape summary [{Label}]: fetched={Fetched} distinct={Distinct} verified={Verified} gap={Gap} recall={Recall:P1}",
            label, totalFetched, distinctAdded, totalVerified, totalVerified - totalFetched, recall);
    }
}
