using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using OpenTDBLookup.ViewModels;

namespace OpenTDBLookup.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        PropertyChanged += OnWindowPropertyChanged;
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) { return; }
        if (DataContext is not MainWindowViewModel vm || !vm.IsTrayEnabled) { return; }
        if (e.NewValue is WindowState.Minimized)
        {
            Hide();
        }
    }

    private void OnCopyAnswerButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.CopyAnswerToClipboardCommand.CanExecute(null))
        {
            vm.CopyAnswerToClipboardCommand.Execute(null);
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Avalonia 12 raises Opened on the UI thread; the VM does the heavy
        // lifting via async/await, returning to UI thread for Dispatched calls.
        if (DataContext is not MainWindowViewModel vm) { return; }

        InputBox.Focus();
        await vm.InitializeAsync(CancellationToken.None);

        if (vm.RequiresInitialScrape)
        {
            await ShowInitialScrapeDialogAsync(vm);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Tray "minimize to tray" toggle is handled here: when enabled, hide
        // instead of close. App quit is reachable via the tray menu.
        if (DataContext is not MainWindowViewModel vm) { return; }
        if (!vm.IsTrayEnabled) { return; }
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+L focus is awkward to express as a Command target, so handled
        // inline. The other shortcuts live in <Window.KeyBindings>.
        if (e.Key == Key.L && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control)
        {
            InputBox.Focus();
            InputBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Drives the modal initial-scrape dialog. The dialog and the scrape
    /// task race via <c>Task.WhenAny</c>: whichever finishes first
    /// triggers cleanup of the other. Tracking dialog dismissal through its
    /// own <c>Closed</c> event (rather than relying on <c>ShowAsync</c>'s
    /// task-completion semantics) avoids a Hide()/animation race we hit with
    /// FluentAvalonia 3.0.0-preview2.
    /// </summary>
    private async Task ShowInitialScrapeDialogAsync(MainWindowViewModel vm)
    {
        var scrapeVm = new ScrapeProgressViewModel { ApiCallsCeiling = 250, CanCancel = true };
        using var cts = new CancellationTokenSource();
        scrapeVm.CancelRequested = cts.Cancel;

        var content = new ScrapeProgressDialog { DataContext = scrapeVm };
        var dialog = new FAContentDialog
        {
            Title = "Building local question cache",
            Content = content,
            CloseButtonText = "Cancel in background",
            DefaultButton = FAContentDialogButton.Close,
        };
        dialog.CloseButtonClick += (_, _) => cts.Cancel();

        var dialogClosed = new TaskCompletionSource();
        dialog.Closed += (_, _) => dialogClosed.TrySetResult();

        Task<OpenTDBLookup.Services.RefreshResult>? scrapeTask = null;
        try
        {
            scrapeTask = vm.RunInitialScrapeAsync(scrapeVm, cts.Token);
            // ShowAsync returns a Task<FAContentDialogResult> that completes
            // when the dialog hides. We do NOT await it here because the
            // 3.0.0-preview2 path can leave that task uncompleted in some
            // edge cases - the Closed event TCS is the source of truth.
            _ = dialog.ShowAsync(this);

            await Task.WhenAny(scrapeTask, dialogClosed.Task);

            if (scrapeTask.IsCompleted && !dialogClosed.Task.IsCompleted)
            {
                // Scrape finished first (success or failure). Dismiss the dialog.
                dialog.Hide(FAContentDialogResult.None);
                await dialogClosed.Task;
            }
            else
            {
                // User dismissed the dialog. Cancel the scrape and let it unwind.
                cts.Cancel();
            }
        }
        catch (InvalidOperationException ex)
        {
            vm.StatusMessage = $"Scrape dialog failed to open ({ex.Message}); progress runs inline";
            cts.Cancel();
        }

        // Make sure the scrape task observes its exception and finalizes
        // VM state - regardless of whether dialog or scrape "won" the race.
        if (scrapeTask is not null)
        {
            try { await scrapeTask; }
            catch (OperationCanceledException) { /* expected on user cancel */ }
            catch (Exception ex)
            {
                vm.StatusMessage = $"Initial scrape failed: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Public entry-point used by the tray menu to surface the window after
    /// it has been hidden by the close-to-tray behavior.
    /// </summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
