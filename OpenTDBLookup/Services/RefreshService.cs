using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        try
        {
            var token = await _client.RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;
            var categories = await _client.GetCategoriesAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;
            var verifiedCounts = await _client.GetVerifiedCountsAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;

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

                    progress?.Report(new ScrapeProgress(
                        category.Name,
                        difficulty,
                        apiCalls,
                        MaxApiCallsPerSession,
                        added,
                        Percent(bucketIdx, totalBuckets)));

                    var (bucketAdded, callsConsumed, refreshedToken) = await ScrapeBucketAsync(
                        category, difficulty, token, apiCalls, cancellationToken).ConfigureAwait(false);
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

            if (hitCeiling)
            {
                _logger.LogWarning("API call ceiling reached after {Calls} calls; saving partial scrape", apiCalls);
            }

            _repository.UpdateCategoryCounts(verifiedCounts);
            if (!hitCeiling)
            {
                _repository.MarkFullScrape();
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
            await TrySavePartialAsync().ConfigureAwait(false);
            throw;
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

        try
        {
            var verifiedCounts = await _client.GetVerifiedCountsAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;

            var changed = new List<int>();
            foreach (var (categoryId, count) in verifiedCounts)
            {
                var stored = _repository.CategoryVerifiedCounts.GetValueOrDefault(categoryId, 0);
                if (count > stored)
                {
                    changed.Add(categoryId);
                }
            }

            if (changed.Count == 0)
            {
                _logger.LogInformation("Incremental refresh: no category counts changed; updating last-check timestamp only");
                _repository.UpdateCategoryCounts(verifiedCounts);
                _repository.MarkCountCheck();
                await _repository.SaveAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                return new RefreshResult(0, apiCalls, false, sw.Elapsed);
            }

            var categoriesById = new Dictionary<int, OpenTdbCategoryItem>();
            foreach (var c in await _client.GetCategoriesAsync(cancellationToken).ConfigureAwait(false))
            {
                categoriesById[c.Id] = c;
            }
            apiCalls++;

            var token = await _client.RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            apiCalls++;

            var totalBuckets = changed.Count * Difficulties.Length;
            var bucketIdx = 0;

            foreach (var categoryId in changed)
            {
                if (!categoriesById.TryGetValue(categoryId, out var category))
                {
                    _logger.LogWarning("Verified-count map referenced unknown category {CategoryId}; skipping", categoryId);
                    bucketIdx += Difficulties.Length;
                    continue;
                }
                foreach (var difficulty in Difficulties)
                {
                    bucketIdx++;
                    cancellationToken.ThrowIfCancellationRequested();

                    if (apiCalls >= MaxApiCallsPerSession)
                    {
                        hitCeiling = true;
                        break;
                    }

                    progress?.Report(new ScrapeProgress(
                        category.Name,
                        difficulty,
                        apiCalls,
                        MaxApiCallsPerSession,
                        added,
                        Percent(bucketIdx, totalBuckets)));

                    var (bucketAdded, callsConsumed, refreshedToken) = await ScrapeBucketAsync(
                        category, difficulty, token, apiCalls, cancellationToken).ConfigureAwait(false);
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

            if (hitCeiling)
            {
                _logger.LogWarning("Incremental refresh hit API ceiling after {Calls} calls", apiCalls);
            }

            _repository.UpdateCategoryCounts(verifiedCounts);
            _repository.MarkCountCheck();
            await _repository.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Incremental refresh cancelled after {Calls} calls and {Added} new questions; saving partial state", apiCalls, added);
            await TrySavePartialAsync().ConfigureAwait(false);
            throw;
        }

        sw.Stop();
        return new RefreshResult(added, apiCalls, hitCeiling, sw.Elapsed);
    }

    private async Task<(int Added, int CallsConsumed, string Token)> ScrapeBucketAsync(
        OpenTdbCategoryItem category,
        string difficulty,
        string token,
        int apiCallsSoFar,
        CancellationToken cancellationToken)
    {
        var bucketResults = new List<OpenTdbQuestionResult>();
        var callsConsumed = 0;
        var bucketBusy = true;

        while (bucketBusy && (apiCallsSoFar + callsConsumed) < MaxApiCallsPerSession)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (code, results) = await _client.FetchQuestionsAsync(
                RequestBatchSize, category.Id, difficulty, token, cancellationToken).ConfigureAwait(false);
            callsConsumed++;

            switch (code)
            {
                case 0:
                    bucketResults.AddRange(results);
                    if (results.Count < RequestBatchSize)
                    {
                        bucketBusy = false;
                    }
                    break;
                case 1:
                    bucketBusy = false;
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
        var added = _repository.Merge(mapped);
        return (added, callsConsumed, token);
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
}
