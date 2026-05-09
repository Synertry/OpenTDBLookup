using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;
using OpenTDBLookup.Services;

namespace OpenTDBLookup.Tests;

public sealed class QuestionRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public QuestionRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"otdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "questions.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private QuestionRepository NewRepo() =>
        new(NullLogger<QuestionRepository>.Instance, _filePath);

    private static Question MakeQuestion(string text, string answer = "x", int categoryId = 1, string difficulty = "easy")
    {
        var hash = HashHelper.ComputeHash(TextNormalizer.Normalize(text));
        return new Question(hash, categoryId, "General", difficulty, "multiple", text, answer, []);
    }

    [Fact]
    public async Task LoadAsync_initializes_empty_when_file_missing()
    {
        var repo = NewRepo();

        await repo.LoadAsync(CancellationToken.None);

        repo.Count.Should().Be(0);
        repo.LastFullScrape.Should().BeNull();
        repo.LastCountCheck.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_questions_and_metadata()
    {
        var repo = NewRepo();
        repo.Merge([MakeQuestion("Q1", "A1"), MakeQuestion("Q2", "A2")]);
        repo.UpdateCategoryCounts(new Dictionary<int, int> { [9] = 100, [10] = 50 });
        repo.MarkFullScrape();

        await repo.SaveAsync(CancellationToken.None);

        var fresh = NewRepo();
        await fresh.LoadAsync(CancellationToken.None);

        fresh.Count.Should().Be(2);
        fresh.LastFullScrape.Should().NotBeNull();
        fresh.LastCountCheck.Should().NotBeNull();
        fresh.CategoryVerifiedCounts.Should().BeEquivalentTo(new Dictionary<int, int> { [9] = 100, [10] = 50 });
        fresh.All.Should().Contain(q => q.QuestionText == "Q1" && q.CorrectAnswer == "A1");
        fresh.NormalizedQuestionByHash.Should().ContainKey(MakeQuestion("Q1").Hash);
    }

    [Fact]
    public void Merge_dedupes_by_hash_and_returns_count_added()
    {
        var repo = NewRepo();

        var firstAdded = repo.Merge([MakeQuestion("dup"), MakeQuestion("unique")]);
        firstAdded.Should().Be(2);

        var secondAdded = repo.Merge([MakeQuestion("dup"), MakeQuestion("brand new")]);
        secondAdded.Should().Be(1);

        repo.Count.Should().Be(3);
    }

    [Fact]
    public void Merge_keeps_same_text_under_different_category_or_difficulty()
    {
        var repo = NewRepo();

        // Same question text in three different (cat, diff) tuples - OpenTDB
        // lists each separately and the cache must keep all three so
        // per-category counts match the API's verified totals.
        var added = repo.Merge([
            MakeQuestion("shared", categoryId: 9, difficulty: "easy"),
            MakeQuestion("shared", categoryId: 9, difficulty: "medium"),
            MakeQuestion("shared", categoryId: 13, difficulty: "easy"),
        ]);
        added.Should().Be(3);
        repo.Count.Should().Be(3);

        // The fast-path text-hash lookup still resolves (whichever was stored
        // first wins) - the answer text is identical so the user gets the
        // right answer regardless of which copy is returned.
        var hash = HashHelper.ComputeHash(TextNormalizer.Normalize("shared"));
        repo.ByHash.Should().ContainKey(hash);
    }

    [Fact]
    public void Merge_skips_exact_same_text_in_same_category_and_difficulty()
    {
        var repo = NewRepo();

        var added = repo.Merge([
            MakeQuestion("twin", categoryId: 9, difficulty: "easy"),
            MakeQuestion("twin", categoryId: 9, difficulty: "easy"),
        ]);
        added.Should().Be(1);
        repo.Count.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_cleans_up_temp_file_on_success()
    {
        var repo = NewRepo();
        repo.Merge([MakeQuestion("hello", "world")]);

        await repo.SaveAsync(CancellationToken.None);

        File.Exists(_filePath).Should().BeTrue();
        File.Exists(_filePath + ".tmp").Should().BeFalse("the atomic write must rename .tmp over the target");
    }

    [Fact]
    public async Task LoadAsync_recovers_from_corrupt_json()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not valid json");
        var repo = NewRepo();

        await repo.LoadAsync(CancellationToken.None);

        repo.Count.Should().Be(0);
        repo.LastFullScrape.Should().BeNull();
    }

    [Fact]
    public void GetCachedCountsByCategoryDifficulty_groups_by_category_and_difficulty()
    {
        var repo = NewRepo();
        repo.Merge([
            MakeQuestion("a", categoryId: 9, difficulty: "easy"),
            MakeQuestion("b", categoryId: 9, difficulty: "easy"),
            MakeQuestion("c", categoryId: 9, difficulty: "medium"),
            MakeQuestion("d", categoryId: 13, difficulty: "easy"),
        ]);

        var counts = repo.GetCachedCountsByCategoryDifficulty();

        counts.Should().ContainKey((9, "easy")).WhoseValue.Should().Be(2);
        counts.Should().ContainKey((9, "medium")).WhoseValue.Should().Be(1);
        counts.Should().ContainKey((13, "easy")).WhoseValue.Should().Be(1);
        counts.Should().NotContainKey((13, "medium"));
    }
}
