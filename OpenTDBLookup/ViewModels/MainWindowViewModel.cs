using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OpenTDBLookup.Helpers;
using OpenTDBLookup.Models;
using OpenTDBLookup.Services;

namespace OpenTDBLookup.ViewModels;

/// <summary>
/// Drives the main window: input box, search result, refresh status,
/// clipboard-watch toggle, and the optional initial-scrape dialog hand-off.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(100);

    private readonly IQuestionRepository _repository;
    private readonly IQuestionMatcher _matcher;
    private readonly IRefreshService _refresh;
    private readonly IClipboardWatcher _watcher;
    private readonly ILogger<MainWindowViewModel> _logger;

    private CancellationTokenSource? _searchCts;
    private string _lastClipboardWriteByApp = string.Empty;
    private bool _disposed;

    [ObservableProperty]
    private string _pastedText = string.Empty;

    [ObservableProperty]
    private Question? _currentMatch;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isClipboardWatchEnabled;

    [ObservableProperty]
    private bool _isTrayEnabled;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private int _totalQuestions;

    [ObservableProperty]
    private DateTimeOffset? _lastRefresh;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressLabel = string.Empty;

    [ObservableProperty]
    private string _normalizedSearchPreview = string.Empty;

    [ObservableProperty]
    private bool _showNoMatch;

    [ObservableProperty]
    private string _appVersion = "0.1.0";

    public MainWindowViewModel(
        IQuestionRepository repository,
        IQuestionMatcher matcher,
        IRefreshService refresh,
        IClipboardWatcher watcher,
        ILogger<MainWindowViewModel> logger)
    {
        _repository = repository;
        _matcher = matcher;
        _refresh = refresh;
        _watcher = watcher;
        _logger = logger;

        AppVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.1.0";
    }

    /// <summary>
    /// Design-time constructor for the XAML previewer. Resolves to a no-op
    /// VM with empty state so the previewer does not crash for lack of DI.
    /// </summary>
    public MainWindowViewModel()
        : this(
            new DesignTimeRepository(),
            new DesignTimeMatcher(),
            new DesignTimeRefresh(),
            new DesignTimeWatcher(),
            new DesignTimeLogger())
    {
    }

    /// <summary>True while the initial scrape is showing the modal dialog.</summary>
    public bool IsInitialScrapeRunning { get; private set; }

    /// <summary>True after <see cref="InitializeAsync"/> if the cache was empty and the View should host the initial-scrape dialog.</summary>
    public bool RequiresInitialScrape { get; private set; }

    /// <summary>
    /// Loads the cache and decides whether the View needs to host the initial
    /// scrape dialog. The View calls <see cref="RunInitialScrapeAsync"/>
    /// itself rather than the VM driving the dialog through events, so the
    /// dialog lifetime stays under one owner and dismissal is deterministic.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _repository.LoadAsync(cancellationToken).ConfigureAwait(true);
            UpdateRepoStats();

            if (_repository.Count == 0)
            {
                RequiresInitialScrape = true;
            }
            else if (await _refresh.ShouldRefreshAsync().ConfigureAwait(true))
            {
                _ = RunIncrementalRefreshInBackgroundAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup initialize failed");
            StatusMessage = $"Startup failed: {ex.Message}";
        }
    }

    partial void OnPastedTextChanged(string value) =>
        ScheduleSearch(value);

    partial void OnIsClipboardWatchEnabledChanged(bool value)
    {
        if (value)
        {
            _watcher.ClipboardChanged += OnClipboardChanged;
            _watcher.Start();
            StatusMessage = "Clipboard watcher on";
        }
        else
        {
            _watcher.ClipboardChanged -= OnClipboardChanged;
            _watcher.Stop();
            StatusMessage = "Clipboard watcher off";
        }
    }

    private void ScheduleSearch(string value)
    {
        var fresh = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCts, fresh);
        try { previous?.Cancel(); previous?.Dispose(); } catch (ObjectDisposedException) { }

        var token = fresh.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounce, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) { return; }

                var match = _matcher.Match(value);
                var preview = _matcher.NormalizeForPreview(value) ?? string.Empty;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) { return; }
                    CurrentMatch = match;
                    NormalizedSearchPreview = preview;
                    ShowNoMatch = match is null && !string.IsNullOrWhiteSpace(value);
                    StatusMessage = match is null
                        ? (string.IsNullOrWhiteSpace(value) ? null : "No match found")
                        : $"Matched in {match.Category} ({match.Difficulty})";
                });
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Search failed");
            }
        });
    }

    [RelayCommand]
    // [RelayCommand] generates a public RefreshCommand of type
    // IAsyncRelayCommand. XAML can bind directly to that generated property.
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsRefreshing) { return; }
        IsRefreshing = true;
        try
        {
            // The incremental refresh path also handles an empty cache
            // correctly: every category's stored count is 0, so every
            // category gets scraped. We use it for both "fill cache" and
            // "check for new questions" so the inline progress strip works
            // for either.
            StatusMessage = _repository.Count == 0 ? "Building cache..." : "Refreshing...";
            ProgressValue = 0;
            ProgressLabel = "Checking for new questions";
            var progress = new Progress<ScrapeProgress>(p =>
            {
                ProgressValue = p.PercentComplete;
                ProgressLabel = $"{p.CurrentCategory} ({p.CurrentDifficulty}) - {p.ApiCallsMade}/{p.ApiCallsCeiling} calls";
            });
            var result = await _refresh.IncrementalRefreshAsync(progress, cancellationToken).ConfigureAwait(true);
            StatusMessage = result.QuestionsAdded == 0
                ? "Up to date"
                : $"Added {result.QuestionsAdded} new question(s)";
            UpdateRepoStats();
            RequiresInitialScrape = _repository.Count == 0;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Refresh cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh failed");
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            ProgressValue = 0;
            ProgressLabel = string.Empty;
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task CopyAnswerToClipboardAsync()
    {
        if (CurrentMatch is null) { return; }
        var clipboard = ClipboardAccessor.Current;
        if (clipboard is null)
        {
            StatusMessage = "Clipboard unavailable";
            return;
        }
        await clipboard.SetTextAsync(CurrentMatch.CorrectAnswer).ConfigureAwait(true);
        _lastClipboardWriteByApp = CurrentMatch.CorrectAnswer;
        if (_watcher is ClipboardWatcher cw) { cw.NotifyApplicationWrote(CurrentMatch.CorrectAnswer); }
        StatusMessage = "Copied answer to clipboard";
    }

    [RelayCommand]
    private void ClearSearch()
    {
        PastedText = string.Empty;
        CurrentMatch = null;
        StatusMessage = null;
        NormalizedSearchPreview = string.Empty;
        ShowNoMatch = false;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open log folder");
            StatusMessage = $"Could not open log folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs the initial scrape and updates VM state around it. Caller owns
    /// the <see cref="CancellationToken"/> - the View typically holds the CTS
    /// and surfaces the same token to whatever Cancel mechanism it builds.
    /// Throws <see cref="OperationCanceledException"/> on user cancel; the
    /// View should swallow that.
    /// </summary>
    public async Task<RefreshResult> RunInitialScrapeAsync(ScrapeProgressViewModel progress, CancellationToken cancellationToken)
    {
        IsInitialScrapeRunning = true;
        IsRefreshing = true;
        try
        {
            var result = await _refresh.InitialScrapeAsync(progress, cancellationToken).ConfigureAwait(true);
            StatusMessage = result.HitCeiling
                ? $"Partial scrape: {result.QuestionsAdded} added, ceiling reached"
                : $"Loaded {result.QuestionsAdded} questions in {result.Duration.TotalSeconds:F1}s";
            RequiresInitialScrape = _repository.Count == 0;
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Initial scrape cancelled";
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial scrape failed");
            StatusMessage = $"Initial scrape failed: {ex.Message}";
            throw;
        }
        finally
        {
            IsRefreshing = false;
            IsInitialScrapeRunning = false;
            UpdateRepoStats();
        }
    }

    private async Task RunIncrementalRefreshInBackgroundAsync()
    {
        try
        {
            IsRefreshing = true;
            ProgressValue = 0;
            ProgressLabel = "Background refresh";
            var progress = new Progress<ScrapeProgress>(p =>
            {
                ProgressValue = p.PercentComplete;
                ProgressLabel = $"{p.CurrentCategory} ({p.CurrentDifficulty})";
            });
            var result = await _refresh.IncrementalRefreshAsync(progress, CancellationToken.None).ConfigureAwait(true);
            UpdateRepoStats();
            if (result.QuestionsAdded > 0)
            {
                StatusMessage = $"Background refresh added {result.QuestionsAdded} question(s)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background refresh failed");
        }
        finally
        {
            IsRefreshing = false;
            ProgressValue = 0;
            ProgressLabel = string.Empty;
        }
    }

    private async void OnClipboardChanged(object? sender, string text)
    {
        try
        {
            if (string.Equals(text, _lastClipboardWriteByApp, StringComparison.Ordinal)) { return; }
            var match = _matcher.Match(text);
            if (match is null) { return; }
            var clipboard = ClipboardAccessor.Current;
            if (clipboard is null) { return; }
            await clipboard.SetTextAsync(match.CorrectAnswer).ConfigureAwait(true);
            _lastClipboardWriteByApp = match.CorrectAnswer;
            if (_watcher is ClipboardWatcher cw) { cw.NotifyApplicationWrote(match.CorrectAnswer); }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentMatch = match;
                PastedText = text;
                StatusMessage = $"Auto-answered: {match.Category}";
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard auto-answer failed");
        }
    }

    private void UpdateRepoStats()
    {
        TotalQuestions = _repository.Count;
        LastRefresh = _repository.LastCountCheck;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _watcher.ClipboardChanged -= OnClipboardChanged;
        _watcher.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }

    // ---------------------------------------------------------------------
    // Design-time stubs - never used at runtime; only the parameterless
    // constructor for the XAML previewer reaches them.
    // ---------------------------------------------------------------------
    private sealed class DesignTimeRepository : IQuestionRepository
    {
        public System.Collections.Generic.IReadOnlyDictionary<string, Question> ByHash { get; } = new System.Collections.Generic.Dictionary<string, Question>();
        public System.Collections.Generic.IReadOnlyList<Question> All { get; } = [];
        public int Count => 0;
        public DateTimeOffset? LastFullScrape => null;
        public DateTimeOffset? LastCountCheck => null;
        public System.Collections.Generic.IReadOnlyDictionary<int, int> CategoryVerifiedCounts { get; } = new System.Collections.Generic.Dictionary<int, int>();
        public System.Collections.Generic.IReadOnlyDictionary<string, string> NormalizedQuestionByHash { get; } = new System.Collections.Generic.Dictionary<string, string>();
        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public int Merge(System.Collections.Generic.IEnumerable<Question> incoming) => 0;
        public void UpdateCategoryCounts(System.Collections.Generic.IReadOnlyDictionary<int, int> verifiedCounts) { }
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
    }

    private sealed class DesignTimeLogger : ILogger<MainWindowViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
