using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Thread-safe in-memory store of every cached question. Persists as
/// pretty-printed JSON next to the executable. All mutations are guarded by a
/// private lock; read-only properties expose snapshot views so callers can
/// iterate without holding the lock.
/// </summary>
public sealed class QuestionRepository : IQuestionRepository
{
    private const int CurrentSchemaVersion = 1;

    // Bounded so a corrupt or hostile questions.json cannot allocate gigabytes
    // before the parser gives up. The full OpenTDB corpus serializes to a few
    // megabytes; 200 MB is generous enough to never bite a real cache.
    private const long MaxStoreFileBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _lock = new();
    private readonly ILogger<QuestionRepository> _logger;
    private readonly string _filePath;
    private readonly string _tempPath;

    // Primary storage: every Question we've stored, including text-duplicates
    // that live in different (cat, diff) buckets so per-category counts can
    // match OpenTDB's reported totals exactly.
    private List<Question> _questions = [];
    // First-win text-hash lookup for the matcher fast path. With cross-bucket
    // duplicates the same text hash can appear under multiple categories;
    // whichever was stored first wins for lookup. The answer text is the
    // same for all of them so the user sees the right answer either way.
    private Dictionary<string, Question> _byHash = new(StringComparer.Ordinal);
    // Composite-key dedup. Same (text, cat, diff) tuple cannot be stored
    // twice; same text in a different (cat, diff) IS allowed because OpenTDB
    // lists those as distinct verified entries.
    private HashSet<(string Hash, int CategoryId, string Difficulty)> _seenComposite = [];
    private Dictionary<string, string> _normalizedByHash = new(StringComparer.Ordinal);
    private Dictionary<int, int> _categoryVerifiedCounts = [];
    private DateTimeOffset? _lastFullScrape;
    private DateTimeOffset? _lastCountCheck;
    private int _knownDuplicateCount;

    private IReadOnlyDictionary<string, Question> _byHashSnapshot = new Dictionary<string, Question>();
    private IReadOnlyList<Question> _allSnapshot = [];
    private IReadOnlyDictionary<string, string> _normalizedSnapshot = new Dictionary<string, string>();
    private IReadOnlyDictionary<int, int> _countsSnapshot = new Dictionary<int, int>();

    public QuestionRepository(ILogger<QuestionRepository> logger)
        : this(logger, Path.Combine(AppContext.BaseDirectory, "questions.json"))
    {
    }

    /// <summary>Test-friendly constructor allowing the JSON path to be redirected.</summary>
    public QuestionRepository(ILogger<QuestionRepository> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
        _tempPath = filePath + ".tmp";
    }

    public IReadOnlyDictionary<string, Question> ByHash => _byHashSnapshot;
    public IReadOnlyList<Question> All => _allSnapshot;
    public int Count => _allSnapshot.Count;
    public DateTimeOffset? LastFullScrape => _lastFullScrape;
    public DateTimeOffset? LastCountCheck => _lastCountCheck;
    public IReadOnlyDictionary<int, int> CategoryVerifiedCounts => _countsSnapshot;
    public int KnownDuplicateCount
    {
        get
        {
            lock (_lock) { return _knownDuplicateCount; }
        }
    }
    public IReadOnlyDictionary<string, string> NormalizedQuestionByHash => _normalizedSnapshot;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No questions.json at {Path}; starting with an empty cache", _filePath);
            ResetState();
            return;
        }

        try
        {
            var fileLength = new FileInfo(_filePath).Length;
            if (fileLength > MaxStoreFileBytes)
            {
                _logger.LogError("questions.json is {Bytes} bytes (limit {Limit}); refusing to load", fileLength, MaxStoreFileBytes);
                ResetState();
                return;
            }

            await using var stream = File.OpenRead(_filePath);
            var loaded = await JsonSerializer.DeserializeAsync<QuestionStoreFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                _logger.LogWarning("questions.json deserialized as null; starting with empty cache");
                ResetState();
                return;
            }

            ApplyLoadedState(loaded);
            _logger.LogInformation("Loaded {Count} cached questions from {Path}", _allSnapshot.Count, _filePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Failed to load questions.json; starting with empty cache");
            ResetState();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        QuestionStoreFile snapshot;
        lock (_lock)
        {
            snapshot = new QuestionStoreFile(
                CurrentSchemaVersion,
                _lastFullScrape,
                _lastCountCheck,
                new Dictionary<int, int>(_categoryVerifiedCounts),
                [.. _questions],
                _knownDuplicateCount);
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            await using (var stream = File.Create(_tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(_tempPath, _filePath, overwrite: true);
            _logger.LogDebug("Saved {Count} questions to {Path}", snapshot.Questions.Count, _filePath);
        }
        finally
        {
            // Best-effort cleanup: if File.Move raised (e.g. cross-volume move
            // on a non-default install) we still want the half-written .tmp
            // gone so a subsequent Save sees a clean slate.
            if (File.Exists(_tempPath))
            {
                try { File.Delete(_tempPath); }
                catch (IOException ioex) { _logger.LogWarning(ioex, "Failed to delete leftover temp file {Path}", _tempPath); }
            }
        }
    }

    public int Merge(IEnumerable<Question> incoming)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var q in incoming)
            {
                var key = (q.Hash, q.CategoryId, q.Difficulty);
                // _seenComposite.Add returns false when the key already exists
                // (same text in same (cat, diff) bucket - a true duplicate).
                // It returns true on first sight, including cases where the
                // text hash exists but under a different (cat, diff).
                if (!_seenComposite.Add(key))
                {
                    continue;
                }

                _questions.Add(q);
                _byHash.TryAdd(q.Hash, q);
                _normalizedByHash.TryAdd(q.Hash, TextNormalizer.Normalize(q.QuestionText));
                added++;
            }

            if (added > 0)
            {
                RebuildSnapshots();
            }
        }
        return added;
    }

    public IReadOnlyDictionary<int, int> GetCachedCountsByCategory()
    {
        // Snapshot the list under the lock then bucket outside it so this
        // scales linearly with the cache size without blocking writers.
        Question[] snapshot;
        lock (_lock)
        {
            snapshot = new Question[_questions.Count];
            _questions.CopyTo(snapshot, 0);
        }
        var counts = new Dictionary<int, int>(snapshot.Length);
        foreach (var q in snapshot)
        {
            counts[q.CategoryId] = counts.GetValueOrDefault(q.CategoryId, 0) + 1;
        }
        return counts;
    }

    public IReadOnlyDictionary<(int CategoryId, string Difficulty), int> GetCachedCountsByCategoryDifficulty()
    {
        Question[] snapshot;
        lock (_lock)
        {
            snapshot = new Question[_questions.Count];
            _questions.CopyTo(snapshot, 0);
        }
        var counts = new Dictionary<(int, string), int>(snapshot.Length);
        foreach (var q in snapshot)
        {
            var key = (q.CategoryId, q.Difficulty);
            counts[key] = counts.GetValueOrDefault(key, 0) + 1;
        }
        return counts;
    }

    public void UpdateCategoryCounts(IReadOnlyDictionary<int, int> verifiedCounts)
    {
        lock (_lock)
        {
            _categoryVerifiedCounts = new Dictionary<int, int>(verifiedCounts);
            _countsSnapshot = new Dictionary<int, int>(_categoryVerifiedCounts);
        }
    }

    public void RecordKnownDuplicateCount(int count)
    {
        lock (_lock) { _knownDuplicateCount = Math.Max(0, count); }
    }

    public void MarkFullScrape()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _lastFullScrape = now;
            _lastCountCheck = now;
        }
    }

    public void MarkCountCheck()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _lastCountCheck = now;
        }
    }

    private void ResetState()
    {
        lock (_lock)
        {
            _questions = [];
            _byHash = new Dictionary<string, Question>(StringComparer.Ordinal);
            _seenComposite = [];
            _normalizedByHash = new Dictionary<string, string>(StringComparer.Ordinal);
            _categoryVerifiedCounts = [];
            _lastFullScrape = null;
            _lastCountCheck = null;
            _knownDuplicateCount = 0;
            _countsSnapshot = new Dictionary<int, int>();
            RebuildSnapshots();
        }
    }

    private void ApplyLoadedState(QuestionStoreFile loaded)
    {
        lock (_lock)
        {
            _questions = new List<Question>(loaded.Questions.Count);
            _byHash = new Dictionary<string, Question>(loaded.Questions.Count, StringComparer.Ordinal);
            _seenComposite = new HashSet<(string, int, string)>(loaded.Questions.Count);
            _normalizedByHash = new Dictionary<string, string>(loaded.Questions.Count, StringComparer.Ordinal);
            foreach (var q in loaded.Questions)
            {
                var key = (q.Hash, q.CategoryId, q.Difficulty);
                if (!_seenComposite.Add(key)) { continue; }
                _questions.Add(q);
                _byHash.TryAdd(q.Hash, q);
                _normalizedByHash.TryAdd(q.Hash, TextNormalizer.Normalize(q.QuestionText));
            }
            _categoryVerifiedCounts = new Dictionary<int, int>(loaded.CategoryVerifiedCounts);
            _lastFullScrape = loaded.LastFullScrape;
            _lastCountCheck = loaded.LastCountCheck;
            _knownDuplicateCount = Math.Max(0, loaded.KnownDuplicateCount);

            // Lazy migration for caches written before KnownDuplicateCount existed.
            // If a refresh ran in the last 7 days and there is a residual gap
            // between target and stored, infer that gap as the OpenTDB-side
            // duplicate count. The 7-day window matches the auto-refresh
            // cadence: anything older will be re-checked by the next refresh
            // anyway, and we'd rather not silently mask a real gap on a stale
            // cache. The very next successful refresh recomputes and writes
            // the authoritative value.
            if (_knownDuplicateCount == 0
                && _lastCountCheck.HasValue
                && DateTimeOffset.UtcNow - _lastCountCheck.Value < TimeSpan.FromDays(7))
            {
                var rawTarget = 0;
                foreach (var v in _categoryVerifiedCounts.Values) { rawTarget += v; }
                var inferred = Math.Max(0, rawTarget - _questions.Count);
                if (inferred > 0)
                {
                    _knownDuplicateCount = inferred;
                    _logger.LogInformation(
                        "Inferred {Count} OpenTDB-side duplicates from existing cache (target={Target} stored={Stored}); next refresh will recompute",
                        inferred, rawTarget, _questions.Count);
                }
            }

            RebuildSnapshots();
            _countsSnapshot = new Dictionary<int, int>(_categoryVerifiedCounts);
        }
    }

    private void RebuildSnapshots()
    {
        _byHashSnapshot = new Dictionary<string, Question>(_byHash, StringComparer.Ordinal);
        _allSnapshot = [.. _questions];
        _normalizedSnapshot = new Dictionary<string, string>(_normalizedByHash, StringComparer.Ordinal);
    }
}
