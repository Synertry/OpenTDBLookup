using System;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenTDBLookup.Services;

namespace OpenTDBLookup.ViewModels;

/// <summary>
/// Backs the <c>ScrapeProgressDialog</c>. Implements <see cref="IProgress{T}"/>
/// so a refresh service can report progress directly into it.
/// </summary>
public partial class ScrapeProgressViewModel : ViewModelBase, IProgress<ScrapeProgress>
{
    // CommunityToolkit.Mvvm's [ObservableProperty] generator turns a private
    // backing field into a public property with INotifyPropertyChanged
    // notifications. The class must be `partial` for the generator to inject
    // the public surface alongside the field.
    [ObservableProperty]
    private string _currentCategory = string.Empty;

    [ObservableProperty]
    private string _currentDifficulty = string.Empty;

    [ObservableProperty]
    private int _apiCallsMade;

    [ObservableProperty]
    private int _apiCallsCeiling;

    [ObservableProperty]
    private int _questionsAdded;

    [ObservableProperty]
    private double _percentComplete;

    [ObservableProperty]
    private bool _canCancel = true;

    public void Report(ScrapeProgress value)
    {
        CurrentCategory = value.CurrentCategory;
        CurrentDifficulty = value.CurrentDifficulty;
        ApiCallsMade = value.ApiCallsMade;
        ApiCallsCeiling = value.ApiCallsCeiling;
        QuestionsAdded = value.QuestionsAdded;
        PercentComplete = value.PercentComplete;
    }
}
