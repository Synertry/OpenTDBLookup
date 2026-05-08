using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using OpenTDBLookup.ViewModels;

namespace OpenTDBLookup.Views;

public partial class MainWindow : Window
{
    private FAContentDialog? _activeScrapeDialog;
    private ScrapeProgressDialog? _activeScrapeContent;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
        PropertyChanged += OnWindowPropertyChanged;
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
    }

    private void OnWindowPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) { return; }
        if (DataContext is not MainWindowViewModel vm || !vm.IsTrayEnabled) { return; }
        if (e.NewValue is WindowState.Minimized)
        {
            Hide();
        }
    }

    private MainWindowViewModel? _subscribedVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is { } previous)
        {
            previous.InitialScrapeRequested -= OnInitialScrapeRequested;
            previous.InitialScrapeCompleted -= OnInitialScrapeCompleted;
            _subscribedVm = null;
        }
        if (DataContext is MainWindowViewModel vm)
        {
            vm.InitialScrapeRequested += OnInitialScrapeRequested;
            vm.InitialScrapeCompleted += OnInitialScrapeCompleted;
            _subscribedVm = vm;
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
        await vm.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
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

    private async void OnInitialScrapeRequested(object? sender, ScrapeProgressViewModel scrapeVm)
    {
        try
        {
            _activeScrapeContent = new ScrapeProgressDialog { DataContext = scrapeVm };
            _activeScrapeDialog = new FAContentDialog
            {
                Title = "Building local question cache",
                Content = _activeScrapeContent,
                CloseButtonText = "Cancel in background",
                DefaultButton = FAContentDialogButton.Close,
            };
            _activeScrapeDialog.CloseButtonClick += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm) { vm.CancelActiveScrape(); }
            };
            // Fire-and-forget; the VM raises InitialScrapeCompleted to dismiss.
            await _activeScrapeDialog.ShowAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            // The dialog throws if no top-level is associated yet; the VM
            // still drives the scrape, but we surface the failure to the
            // status line so the user knows progress is happening invisibly.
            if (DataContext is MainWindowViewModel vm)
            {
                vm.StatusMessage = $"Scrape dialog failed to open ({ex.Message}); progress will appear inline";
            }
        }
    }

    private void OnInitialScrapeCompleted(object? sender, EventArgs e)
    {
        if (_activeScrapeDialog is null) { return; }
        Dispatcher.UIThread.Post(() =>
        {
            _activeScrapeDialog.Hide(FAContentDialogResult.None);
            _activeScrapeDialog = null;
            _activeScrapeContent = null;
        });
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
