using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTDBLookup.Models;
using OpenTDBLookup.Services;
using OpenTDBLookup.ViewModels;

namespace OpenTDBLookup.Tests;

// ---------------------------------------------------------------------------
// Fakes
// ---------------------------------------------------------------------------

internal sealed class StubRepository : IQuestionRepository
{
    private int _count;
    private DateTimeOffset? _lastCountCheck;

    public StubRepository(int count = 0, DateTimeOffset? lastCountCheck = null)
    {
        _count = count;
        _lastCountCheck = lastCountCheck;
    }

    public IReadOnlyDictionary<string, Question> ByHash { get; } = new Dictionary<string, Question>();
    public IReadOnlyList<Question> All { get; } = [];
    public int Count => _count;
    public DateTimeOffset? LastFullScrape => null;
    public DateTimeOffset? LastCountCheck => _lastCountCheck;
    public IReadOnlyDictionary<int, int> CategoryVerifiedCounts { get; set; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<string, string> NormalizedQuestionByHash { get; } = new Dictionary<string, string>();
    public int KnownDuplicateCount { get; set; }
    public IReadOnlyDictionary<int, int> GetCachedCountsByCategory() => new Dictionary<int, int>();
    public IReadOnlyDictionary<(int CategoryId, string Difficulty), int> GetCachedCountsByCategoryDifficulty() =>
        new Dictionary<(int, string), int>();
    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    public int Merge(IEnumerable<Question> incoming) => 0;
    public void UpdateCategoryCounts(IReadOnlyDictionary<int, int> verifiedCounts) { }
    public void RecordKnownDuplicateCount(int count) { _count = count; }
    public void MarkFullScrape() { }
    public void MarkCountCheck() { }
}

internal sealed class StubMatcher : IQuestionMatcher
{
    public Question? NextMatch { get; set; }

    public Question? Match(string? input) => NextMatch;
    public string? NormalizeForPreview(string? input) => input;
}

internal sealed class StubRefresh : IRefreshService
{
    public bool ShouldRefreshResult { get; set; }
    public RefreshResult RefreshResult { get; set; } = new(0, 0, false, TimeSpan.Zero);

    public Task<bool> ShouldRefreshAsync() => Task.FromResult(ShouldRefreshResult);

    public Task<RefreshResult> InitialScrapeAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(RefreshResult);

    public Task<RefreshResult> IncrementalRefreshAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(RefreshResult);
}

internal sealed class StubClipboardWatcher : IClipboardWatcher
{
    public bool IsRunning { get; private set; }
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }

    public event EventHandler<string>? ClipboardChanged;

    public void Start() { IsRunning = true; StartCallCount++; }
    public void Stop() { IsRunning = false; StopCallCount++; }
    public void NotifyApplicationWrote(string text) { }

    // Test helper: simulate a clipboard change.
    public void RaiseClipboardChanged(string text) =>
        ClipboardChanged?.Invoke(this, text);
}

internal sealed class StubToast : IToastService
{
    public void Show(string title, string body, TimeSpan? duration = null) { }
}

internal sealed class StubSettings : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

/// <summary>
/// Pure-logic tests for <see cref="MainWindowViewModel"/> that do not require
/// a live Avalonia dispatcher. Methods that post work to
/// <c>Dispatcher.UIThread</c> (ScheduleSearch, RefreshAsync,
/// OnClipboardChanged, RunInitialScrapeAsync, RunIncrementalRefreshInBackground)
/// cannot be exercised here without Avalonia.Headless, which is not in the
/// project's test dependencies. Those paths are documented below as skipped.
/// </summary>
public sealed class MainWindowViewModelTests
{
    private static MainWindowViewModel MakeVm(
        StubRepository? repo = null,
        StubMatcher? matcher = null,
        StubRefresh? refresh = null,
        StubClipboardWatcher? watcher = null,
        StubSettings? settings = null)
    {
        return new MainWindowViewModel(
            repo ?? new StubRepository(),
            matcher ?? new StubMatcher(),
            refresh ?? new StubRefresh(),
            watcher ?? new StubClipboardWatcher(),
            new StubToast(),
            settings ?? new StubSettings(),
            NullLogger<MainWindowViewModel>.Instance);
    }

    // ------------------------------------------------------------------
    // Constructor
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_sets_AppVersion_to_non_empty_string()
    {
        var vm = MakeVm();

        vm.AppVersion.Should().NotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------
    // ClearSearch
    // ------------------------------------------------------------------

    [Fact]
    public void ClearSearch_resets_all_search_state()
    {
        var vm = MakeVm();
        // Directly set backing properties that ClearSearch is expected to reset.
        vm.PastedText = "some question";
        vm.CurrentMatch = null; // cannot set a real Question without hashing; null is the reset target anyway
        vm.StatusMessage = "some status";
        vm.NormalizedSearchPreview = "normalized";
        vm.ShowNoMatch = true;

        vm.ClearSearchCommand.Execute(null);

        vm.PastedText.Should().BeEmpty();
        vm.CurrentMatch.Should().BeNull();
        vm.StatusMessage.Should().BeNull();
        vm.NormalizedSearchPreview.Should().BeEmpty();
        vm.ShowNoMatch.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // UpdateRepoStats (exercised via InitializeAsync)
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_sets_RequiresInitialScrape_when_repository_is_empty()
    {
        var repo = new StubRepository(count: 0);
        var vm = MakeVm(repo: repo);

        await vm.InitializeAsync(CancellationToken.None);

        vm.RequiresInitialScrape.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_does_not_set_RequiresInitialScrape_when_repository_has_questions()
    {
        var repo = new StubRepository(count: 5);
        var refresh = new StubRefresh { ShouldRefreshResult = false };
        var vm = MakeVm(repo: repo, refresh: refresh);

        await vm.InitializeAsync(CancellationToken.None);

        vm.RequiresInitialScrape.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_TotalQuestions_reflects_repository_count()
    {
        var repo = new StubRepository(count: 42);
        var refresh = new StubRefresh { ShouldRefreshResult = false };
        var vm = MakeVm(repo: repo, refresh: refresh);

        await vm.InitializeAsync(CancellationToken.None);

        vm.TotalQuestions.Should().Be(42);
    }

    [Fact]
    public async Task InitializeAsync_TotalVerified_subtracts_known_duplicates()
    {
        // 10 verified across categories, 2 known duplicates, 8 stored.
        // TotalVerified = max(8, 10 - 2) = max(8, 8) = 8.
        var repo = new StubRepository(count: 8)
        {
            CategoryVerifiedCounts = new Dictionary<int, int> { [9] = 10 },
            KnownDuplicateCount = 2,
        };
        var refresh = new StubRefresh { ShouldRefreshResult = false };
        var vm = MakeVm(repo: repo, refresh: refresh);

        await vm.InitializeAsync(CancellationToken.None);

        vm.TotalVerified.Should().Be(8);
    }

    [Fact]
    public async Task InitializeAsync_TotalVerified_is_at_least_TotalQuestions()
    {
        // 5 stored but only 3 verified (e.g. questions added before API count
        // was updated). TotalVerified must never fall below the actual count.
        var repo = new StubRepository(count: 5)
        {
            CategoryVerifiedCounts = new Dictionary<int, int> { [9] = 3 },
            KnownDuplicateCount = 0,
        };
        var refresh = new StubRefresh { ShouldRefreshResult = false };
        var vm = MakeVm(repo: repo, refresh: refresh);

        await vm.InitializeAsync(CancellationToken.None);

        vm.TotalVerified.Should().BeGreaterThanOrEqualTo(vm.TotalQuestions);
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_stops_clipboard_watcher()
    {
        var watcher = new StubClipboardWatcher();
        var vm = MakeVm(watcher: watcher);

        vm.Dispose();

        watcher.StopCallCount.Should().BeGreaterThan(0, "Dispose must stop the clipboard watcher");
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var watcher = new StubClipboardWatcher();
        var vm = MakeVm(watcher: watcher);

        vm.Dispose();
        var act = () => vm.Dispose();

        act.Should().NotThrow("second Dispose must be a no-op");
    }

    // ------------------------------------------------------------------
    // Clipboard watcher toggle
    // ------------------------------------------------------------------

    [Fact]
    public void Enabling_clipboard_watch_starts_the_watcher()
    {
        var watcher = new StubClipboardWatcher();
        var vm = MakeVm(watcher: watcher);

        vm.IsClipboardWatchEnabled = true;

        watcher.IsRunning.Should().BeTrue();
        vm.StatusMessage.Should().Be("Clipboard watcher on");
    }

    [Fact]
    public void Disabling_clipboard_watch_stops_the_watcher()
    {
        var watcher = new StubClipboardWatcher();
        var vm = MakeVm(watcher: watcher);
        vm.IsClipboardWatchEnabled = true;

        vm.IsClipboardWatchEnabled = false;

        watcher.IsRunning.Should().BeFalse();
        vm.StatusMessage.Should().Be("Clipboard watcher off");
    }

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------

    [Fact]
    public void Toggling_IsToastNotificationsEnabled_persists_settings()
    {
        var settings = new StubSettings();
        var vm = MakeVm(settings: settings);

        vm.IsToastNotificationsEnabled = true;

        // SaveAsync is fire-and-forget; give it a tick to complete.
        Thread.Sleep(50);
        settings.Current.ToastNotificationsEnabled.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Skipped paths (Avalonia.Headless required)
    // ------------------------------------------------------------------
    // The following test cases require Avalonia.Headless (not in deps) because
    // they exercise code paths that post to Dispatcher.UIThread:
    //
    // - ScheduleSearch: Task.Run + Dispatcher.UIThread.InvokeAsync
    // - RefreshAsync: await _refresh.IncrementalRefreshAsync + Progress<T> callback
    // - RunInitialScrapeAsync: await _refresh.InitialScrapeAsync
    // - RunIncrementalRefreshInBackgroundAsync: async void, uses Progress<T>
    // - OnClipboardChanged: async void, Dispatcher.UIThread.InvokeAsync
    // - CopyAnswerToClipboardAsync: ClipboardAccessor.Current (null outside Avalonia)
    //
    // Adding Avalonia.Headless as a test dep would allow these to be covered
    // without mocking the dispatcher. Until then the RefreshService integration
    // tests (RefreshServiceTests.cs) cover the fetch/save logic end-to-end.
}
