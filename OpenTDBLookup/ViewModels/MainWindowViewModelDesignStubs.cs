using Microsoft.Extensions.Logging;
using OpenTDBLookup.Models;
using OpenTDBLookup.Services;

namespace OpenTDBLookup.ViewModels;

// Design-time stubs used only by the parameterless constructor of
// MainWindowViewModel, which the XAML previewer calls. None of these run at
// runtime.
public sealed partial class MainWindowViewModel
{
    private sealed class DesignTimeRepository : IQuestionRepository
    {
        public System.Collections.Generic.IReadOnlyDictionary<string, Question> ByHash { get; } = new System.Collections.Generic.Dictionary<string, Question>();
        public System.Collections.Generic.IReadOnlyList<Question> All { get; } = [];
        public int Count => 0;
        public DateTimeOffset? LastFullScrape => null;
        public DateTimeOffset? LastCountCheck => null;
        public System.Collections.Generic.IReadOnlyDictionary<int, int> CategoryVerifiedCounts { get; } = new System.Collections.Generic.Dictionary<int, int>();
        public System.Collections.Generic.IReadOnlyDictionary<string, string> NormalizedQuestionByHash { get; } = new System.Collections.Generic.Dictionary<string, string>();
        public int KnownDuplicateCount => 0;
        public System.Collections.Generic.IReadOnlyDictionary<int, int> GetCachedCountsByCategory() => new System.Collections.Generic.Dictionary<int, int>();
        public System.Collections.Generic.IReadOnlyDictionary<(int CategoryId, string Difficulty), int> GetCachedCountsByCategoryDifficulty() => new System.Collections.Generic.Dictionary<(int, string), int>();
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public int Merge(System.Collections.Generic.IEnumerable<Question> incoming) => 0;
        public void UpdateCategoryCounts(System.Collections.Generic.IReadOnlyDictionary<int, int> verifiedCounts) { }
        public void RecordKnownDuplicateCount(int count) { }
        public void MarkFullScrape() { }
        public void MarkCountCheck() { }
    }

    private sealed class DesignTimeMatcher : IQuestionMatcher
    {
        public Question? Match(string? input) => null;
        public string? NormalizeForPreview(string? input) => input;
    }

    private sealed class DesignTimeRefresh : IRefreshService
    {
        public Task<RefreshResult> InitialScrapeAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(new RefreshResult(0, 0, false, TimeSpan.Zero));
        public Task<RefreshResult> IncrementalRefreshAsync(IProgress<ScrapeProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(new RefreshResult(0, 0, false, TimeSpan.Zero));
        public Task<bool> ShouldRefreshAsync() => Task.FromResult(false);
    }

    private sealed class DesignTimeWatcher : IClipboardWatcher
    {
        public event EventHandler<string>? ClipboardChanged
        {
            add { _ = value; }
            remove { _ = value; }
        }
        public bool IsRunning => false;
        public void Start() { }
        public void Stop() { }
        public void NotifyApplicationWrote(string text) { }
    }

    private sealed class DesignTimeToast : IToastService
    {
        public void Show(string title, string body, TimeSpan? duration = null) { }
    }

    private sealed class DesignTimeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DesignTimeLogger : ILogger<MainWindowViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
