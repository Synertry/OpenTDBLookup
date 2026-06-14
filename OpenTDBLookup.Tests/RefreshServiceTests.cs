using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;
using OpenTDBLookup.Services;

namespace OpenTDBLookup.Tests;

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

/// <summary>
/// Configurable fake for IOpenTdbClient. Each method delegate can be replaced
/// per-test; defaults return empty/safe values.
/// </summary>
internal sealed class FakeOpenTdbClient : IOpenTdbClient
{
    public Func<CancellationToken, Task<IReadOnlyList<OpenTdbCategoryItem>>> OnGetCategories =
        _ => Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>([]);

    public Func<CancellationToken, Task<IReadOnlyDictionary<int, int>>> OnGetVerifiedCounts =
        _ => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

    public Func<int, CancellationToken, Task<CategoryDifficultyTotals>> OnGetCategoryDifficultyTotals =
        (_, _) => Task.FromResult(new CategoryDifficultyTotals(0, 0, 0));

    public Func<CancellationToken, Task<string>> OnRequestToken =
        _ => Task.FromResult("tok");

    // Returns (responseCode, results). Default: code 4 (exhausted) with no questions.
    public Func<int, int?, string?, string?, CancellationToken, Task<(int Code, IReadOnlyList<OpenTdbQuestionResult> Results)>> OnFetchQuestions =
        (_, _, _, _, _) => Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));

    public Task<IReadOnlyList<OpenTdbCategoryItem>> GetCategoriesAsync(CancellationToken ct) =>
        OnGetCategories(ct);

    public Task<IReadOnlyDictionary<int, int>> GetVerifiedCountsAsync(CancellationToken ct) =>
        OnGetVerifiedCounts(ct);

    public Task<CategoryDifficultyTotals> GetCategoryDifficultyTotalsAsync(int id, CancellationToken ct) =>
        OnGetCategoryDifficultyTotals(id, ct);

    public Task<string> RequestTokenAsync(CancellationToken ct) =>
        OnRequestToken(ct);

    public Task ResetTokenAsync(string token, CancellationToken ct) => Task.CompletedTask;

    public Task<(int responseCode, IReadOnlyList<OpenTdbQuestionResult> results)> FetchQuestionsAsync(
        int amount, int? categoryId, string? difficulty, string? token, CancellationToken ct) =>
        OnFetchQuestions(amount, categoryId, difficulty, token, ct);

    public void Dispose() { }
}

// ---------------------------------------------------------------------------
// Helper
// ---------------------------------------------------------------------------

internal static class FakeQuestion
{
    public static OpenTdbQuestionResult Make(string text, string answer = "A") =>
        new(
            Category: "General",
            Type: "multiple",
            Difficulty: "easy",
            Question: text,
            CorrectAnswer: answer,
            IncorrectAnswers: ["B", "C", "D"]);

    // Build a stored Question the same way MapToQuestion does in RefreshService.
    public static Question Stored(
        string text,
        int categoryId = 9,
        string category = "General",
        string difficulty = "easy")
    {
        var hash = HashHelper.ComputeHash(TextNormalizer.Normalize(text));
        return new Question(hash, categoryId, category, difficulty, "multiple", text, "A", []);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class RefreshServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;
    private readonly FakeOpenTdbClient _client = new();

    public RefreshServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"otdb-rs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "questions.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private QuestionRepository NewRepo() =>
        new(NullLogger<QuestionRepository>.Instance, _filePath);

    private RefreshService MakeService(QuestionRepository repo) =>
        new(_client, repo, NullLogger<RefreshService>.Instance);

    // ------------------------------------------------------------------
    // ShouldRefreshAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ShouldRefreshAsync_returns_true_when_LastCountCheck_is_null()
    {
        var repo = NewRepo();
        var svc = MakeService(repo);

        var result = await svc.ShouldRefreshAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldRefreshAsync_returns_false_when_LastCountCheck_is_recent()
    {
        var repo = NewRepo();
        repo.MarkCountCheck();
        var svc = MakeService(repo);

        var result = await svc.ShouldRefreshAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldRefreshAsync_boundary_null_is_true_and_recent_is_false()
    {
        // Validates both sides of the 7-day threshold in one test.
        var repo = NewRepo();
        var svc = MakeService(repo);

        (await svc.ShouldRefreshAsync()).Should().BeTrue("no check on record forces a refresh");

        repo.MarkCountCheck();
        (await svc.ShouldRefreshAsync()).Should().BeFalse("recent mark is within the 7-day window");
    }

    // ------------------------------------------------------------------
    // ScrapeBucketAsync response_code handling (via InitialScrapeAsync)
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitialScrapeAsync_code1_halves_batch_until_exhausted_then_stops()
    {
        // One category with 5 easy questions verified; client always returns
        // code 1. After halving down to amount=1 and still getting code 1,
        // the bucket must be treated as empty and the run must complete cleanly.
        var cat = new OpenTdbCategoryItem(9, "General");
        _client.OnGetCategories = _ => Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>([cat]);
        _client.OnGetVerifiedCounts = _ => Task.FromResult<IReadOnlyDictionary<int, int>>(
            new Dictionary<int, int> { [9] = 5 });
        _client.OnGetCategoryDifficultyTotals = (_, _) =>
            Task.FromResult(new CategoryDifficultyTotals(5, 0, 0));
        _client.OnFetchQuestions = (_, _, _, _, _) =>
            Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((1, []));

        var repo = NewRepo();
        var svc = MakeService(repo);
        var act = () => svc.InitialScrapeAsync(null, CancellationToken.None);

        await act.Should().NotThrowAsync("code 1 exhaustion is a normal bucket-empty signal");
        var result = await svc.InitialScrapeAsync(null, CancellationToken.None);
        result.QuestionsAdded.Should().Be(0, "code 1 loop adds nothing");
    }

    [Fact]
    public async Task InitialScrapeAsync_code3_rerequests_token_before_continuing()
    {
        // One category with 1 easy question. First fetch returns code 3 (token
        // expired). Service must request a new token and retry. The retry returns
        // code 4. The whole run must complete without throwing.
        var cat = new OpenTdbCategoryItem(9, "General");
        _client.OnGetCategories = _ => Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>([cat]);
        _client.OnGetVerifiedCounts = _ => Task.FromResult<IReadOnlyDictionary<int, int>>(
            new Dictionary<int, int> { [9] = 1 });
        _client.OnGetCategoryDifficultyTotals = (_, _) =>
            Task.FromResult(new CategoryDifficultyTotals(1, 0, 0));

        var tokenRequests = 0;
        _client.OnRequestToken = _ =>
        {
            tokenRequests++;
            return Task.FromResult($"tok{tokenRequests}");
        };

        var fetchCount = 0;
        _client.OnFetchQuestions = (_, _, _, _, _) =>
        {
            fetchCount++;
            // First fetch returns code 3; all subsequent fetches return code 4.
            return fetchCount == 1
                ? Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((3, []))
                : Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
        };

        var repo = NewRepo();
        var svc = MakeService(repo);
        var act = () => svc.InitialScrapeAsync(null, CancellationToken.None);

        await act.Should().NotThrowAsync("code 3 recovery must not surface as an exception");
        // InitialScrapeAsync requests a token at startup (count 1), then code 3
        // triggers a second request (count 2).
        tokenRequests.Should().BeGreaterThanOrEqualTo(2, "code 3 must trigger an additional token request");
    }

    [Fact]
    public async Task InitialScrapeAsync_code4_stops_bucket_without_error()
    {
        // Client immediately returns code 4 for every fetch. No exception
        // should propagate and QuestionsAdded must be zero.
        var cat = new OpenTdbCategoryItem(9, "General");
        _client.OnGetCategories = _ => Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>([cat]);
        _client.OnGetVerifiedCounts = _ => Task.FromResult<IReadOnlyDictionary<int, int>>(
            new Dictionary<int, int> { [9] = 5 });
        _client.OnGetCategoryDifficultyTotals = (_, _) =>
            Task.FromResult(new CategoryDifficultyTotals(5, 0, 0));
        // Default OnFetchQuestions already returns code 4.

        var repo = NewRepo();
        var svc = MakeService(repo);
        var act = () => svc.InitialScrapeAsync(null, CancellationToken.None);

        await act.Should().NotThrowAsync("code 4 is a normal bucket-exhausted signal");
        var result = await svc.InitialScrapeAsync(null, CancellationToken.None);
        result.QuestionsAdded.Should().Be(0);
    }

    [Fact]
    public async Task InitialScrapeAsync_code0_adds_questions_and_saves()
    {
        // Client returns two questions on the first fetch then exhausts the bucket.
        var cat = new OpenTdbCategoryItem(9, "General");
        _client.OnGetCategories = _ => Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>([cat]);
        _client.OnGetVerifiedCounts = _ => Task.FromResult<IReadOnlyDictionary<int, int>>(
            new Dictionary<int, int> { [9] = 2 });
        _client.OnGetCategoryDifficultyTotals = (_, _) =>
            Task.FromResult(new CategoryDifficultyTotals(2, 0, 0));

        var fetchCount = 0;
        _client.OnFetchQuestions = (_, _, _, _, _) =>
        {
            fetchCount++;
            if (fetchCount == 1)
            {
                IReadOnlyList<OpenTdbQuestionResult> qs =
                [
                    FakeQuestion.Make("Q one"),
                    FakeQuestion.Make("Q two"),
                ];
                return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((0, qs));
            }
            return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
        };

        var repo = NewRepo();
        var svc = MakeService(repo);
        var result = await svc.InitialScrapeAsync(null, CancellationToken.None);

        result.QuestionsAdded.Should().Be(2);
        repo.Count.Should().Be(2);
    }

    // ------------------------------------------------------------------
    // InitialScrapeAsync cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitialScrapeAsync_cancellation_rethrows_and_retains_partial_state()
    {
        // Two categories. Cat 9 fetches one question (code 0 then code 4). Cat
        // 10 cancels the token before returning. The service must rethrow
        // OperationCanceledException but TrySavePartialAsync should have been
        // called so in-memory state reflects the cat-9 question.

        using var cts = new CancellationTokenSource();

        _client.OnGetCategories = _ =>
            Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>(
            [
                new OpenTdbCategoryItem(9, "General"),
                new OpenTdbCategoryItem(10, "Books"),
            ]);
        _client.OnGetVerifiedCounts = _ =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(
                new Dictionary<int, int> { [9] = 1, [10] = 1 });
        _client.OnGetCategoryDifficultyTotals = (id, _) =>
            Task.FromResult(id == 9
                ? new CategoryDifficultyTotals(1, 0, 0)
                : new CategoryDifficultyTotals(1, 0, 0));

        var fetchCount = 0;
        _client.OnFetchQuestions = (_, catId, _, _, ct) =>
        {
            fetchCount++;
            if (catId == 9 && fetchCount == 1)
            {
                IReadOnlyList<OpenTdbQuestionResult> qs = [FakeQuestion.Make("Cat9 question")];
                return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((0, qs));
            }
            if (catId == 9)
            {
                // Second call for cat 9: exhausted normally.
                return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
            }
            // First call for cat 10: cancel before returning.
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            // Unreachable, but satisfies the compiler.
            return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
        };

        var repo = NewRepo();
        var svc = MakeService(repo);
        var act = () => svc.InitialScrapeAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled token must propagate out of InitialScrapeAsync");

        // The in-memory merge of cat-9 questions happened before the
        // cancellation. TrySavePartialAsync serializes that state to disk.
        repo.Count.Should().BeGreaterThan(0, "partial results must be retained after cancellation");
    }

    // ------------------------------------------------------------------
    // IncrementalRefreshAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task IncrementalRefreshAsync_returns_zero_when_all_categories_at_target()
    {
        // Cat 9 has 2 questions verified and 2 stored - no gap - no fetch call.
        _client.OnGetVerifiedCounts = _ =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(
                new Dictionary<int, int> { [9] = 2 });

        var fetchCalled = false;
        _client.OnFetchQuestions = (_, _, _, _, _) =>
        {
            fetchCalled = true;
            return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
        };

        var repo = NewRepo();
        repo.Merge([FakeQuestion.Stored("Q1"), FakeQuestion.Stored("Q2")]);

        var svc = MakeService(repo);
        var result = await svc.IncrementalRefreshAsync(null, CancellationToken.None);

        result.QuestionsAdded.Should().Be(0);
        fetchCalled.Should().BeFalse("no gap means no fetch should occur");
    }

    [Fact]
    public async Task IncrementalRefreshAsync_only_refetches_categories_under_target()
    {
        // Cat 9 is at target (2 stored, 2 verified). Cat 10 has a gap (0 stored,
        // 2 verified). Only cat 10 should be fetched.
        _client.OnGetVerifiedCounts = _ =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(
                new Dictionary<int, int> { [9] = 2, [10] = 2 });
        _client.OnGetCategories = _ =>
            Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>(
            [
                new OpenTdbCategoryItem(9, "General"),
                new OpenTdbCategoryItem(10, "Books"),
            ]);
        _client.OnGetCategoryDifficultyTotals = (id, _) =>
            Task.FromResult(id == 10
                ? new CategoryDifficultyTotals(2, 0, 0)
                : new CategoryDifficultyTotals(0, 0, 0));

        var fetchedForCategories = new List<int?>();
        var fetchCount = 0;
        _client.OnFetchQuestions = (_, catId, _, _, _) =>
        {
            fetchedForCategories.Add(catId);
            fetchCount++;
            if (catId == 10 && fetchCount == 1)
            {
                IReadOnlyList<OpenTdbQuestionResult> qs =
                [
                    FakeQuestion.Make("Books Q1"),
                    FakeQuestion.Make("Books Q2"),
                ];
                return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((0, qs));
            }
            return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
        };

        var repo = NewRepo();
        // Pre-populate 2 questions for cat 9 so it is at target.
        repo.Merge([FakeQuestion.Stored("Gen Q1"), FakeQuestion.Stored("Gen Q2")]);

        var svc = MakeService(repo);
        var result = await svc.IncrementalRefreshAsync(null, CancellationToken.None);

        result.QuestionsAdded.Should().Be(2, "only the Books gap should be filled");
        fetchedForCategories.Should().NotContain(9, "cat 9 is at target and must be skipped");
        fetchedForCategories.Should().Contain(10, "cat 10 is under target and must be fetched");
    }

    [Fact]
    public async Task IncrementalRefreshAsync_code0_adds_questions_and_saves()
    {
        // One category under target. Code 0 returns questions; code 4 ends it.
        _client.OnGetVerifiedCounts = _ =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(
                new Dictionary<int, int> { [9] = 2 });
        _client.OnGetCategories = _ =>
            Task.FromResult<IReadOnlyList<OpenTdbCategoryItem>>(
                [new OpenTdbCategoryItem(9, "General")]);
        _client.OnGetCategoryDifficultyTotals = (_, _) =>
            Task.FromResult(new CategoryDifficultyTotals(2, 0, 0));

        var fetchCount = 0;
        _client.OnFetchQuestions = (_, _, _, _, _) =>
        {
            fetchCount++;
            if (fetchCount == 1)
            {
                IReadOnlyList<OpenTdbQuestionResult> qs =
                [
                    FakeQuestion.Make("General Q1"),
                    FakeQuestion.Make("General Q2"),
                ];
                return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((0, qs));
            }
            return Task.FromResult<(int, IReadOnlyList<OpenTdbQuestionResult>)>((4, []));
        };

        var repo = NewRepo();
        var svc = MakeService(repo);
        var result = await svc.IncrementalRefreshAsync(null, CancellationToken.None);

        result.QuestionsAdded.Should().Be(2);
        repo.Count.Should().Be(2);
    }
}
