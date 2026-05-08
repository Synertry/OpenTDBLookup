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

    private Dictionary<string, Question> _byHash = new(StringComparer.Ordinal);
    private Dictionary<string, string> _normalizedByHash = new(StringComparer.Ordinal);
    private Dictionary<int, int> _categoryVerifiedCounts = [];
    private DateTimeOffset? _lastFullScrape;
    private DateTimeOffset? _lastCountCheck;

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
                [.. _byHash.Values]);
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var stream = File.Create(_tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(_tempPath, _filePath, overwrite: true);
        _logger.LogDebug("Saved {Count} questions to {Path}", snapshot.Questions.Count, _filePath);
    }

    public int Merge(IEnumerable<Question> incoming)
    {
        var added = 0;
        lock (_lock)
        {
            foreach (var q in incoming)
            {
                if (_byHash.ContainsKey(q.Hash))
                {
                    continue;
                }

                _byHash[q.Hash] = q;
                _normalizedByHash[q.Hash] = TextNormalizer.Normalize(q.QuestionText);
                added++;
            }

            if (added > 0)
            {
                RebuildSnapshots();
            }
        }
        return added;
    }

    public void UpdateCategoryCounts(IReadOnlyDictionary<int, int> verifiedCounts)
    {
        lock (_lock)
        {
            _categoryVerifiedCounts = new Dictionary<int, int>(verifiedCounts);
            _countsSnapshot = new Dictionary<int, int>(_categoryVerifiedCounts);
        }
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
            _byHash = new Dictionary<string, Question>(StringComparer.Ordinal);
            _normalizedByHash = new Dictionary<string, string>(StringComparer.Ordinal);
            _categoryVerifiedCounts = [];
            _lastFullScrape = null;
            _lastCountCheck = null;
            RebuildSnapshots();
            _countsSnapshot = new Dictionary<int, int>();
        }
    }

    private void ApplyLoadedState(QuestionStoreFile loaded)
    {
        lock (_lock)
        {
            _byHash = new Dictionary<string, Question>(loaded.Questions.Count, StringComparer.Ordinal);
            _normalizedByHash = new Dictionary<string, string>(loaded.Questions.Count, StringComparer.Ordinal);
            foreach (var q in loaded.Questions)
            {
                _byHash[q.Hash] = q;
                _normalizedByHash[q.Hash] = TextNormalizer.Normalize(q.QuestionText);
            }
            _categoryVerifiedCounts = new Dictionary<int, int>(loaded.CategoryVerifiedCounts);
            _lastFullScrape = loaded.LastFullScrape;
            _lastCountCheck = loaded.LastCountCheck;
            RebuildSnapshots();
            _countsSnapshot = new Dictionary<int, int>(_categoryVerifiedCounts);
        }
    }

    private void RebuildSnapshots()
    {
        _byHashSnapshot = new Dictionary<string, Question>(_byHash, StringComparer.Ordinal);
        _allSnapshot = [.. _byHash.Values];
        _normalizedSnapshot = new Dictionary<string, string>(_normalizedByHash, StringComparer.Ordinal);
    }
}
