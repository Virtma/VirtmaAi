using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Training;

namespace VirtmaAi.ViewModels.Training;

public sealed partial class ModelCreatorViewModel : ViewModelBase
{
    private readonly ITrainingService _training;
    private readonly ILogger<ModelCreatorViewModel> _logger;
    private CancellationTokenSource? _cts;

    public ModelCreatorViewModel(ITrainingService training, ILogger<ModelCreatorViewModel> logger)
    {
        _training = training;
        _logger = logger;
        foreach (var a in _training.Architectures) Architectures.Add(a);
        Architecture = TrainingArchitecture.TextClassifierSdca;
    }

    public ObservableCollection<TrainingArchitecture> Architectures { get; } = new();
    public ObservableCollection<TrainedModelInfo> Completed { get; } = new();

    [ObservableProperty] private string _name = "my-model";
    [ObservableProperty] private TrainingArchitecture _architecture;
    [ObservableProperty] private string _csvPath = string.Empty;
    [ObservableProperty] private bool _hasHeader = true;
    [ObservableProperty] private double _testSplit = 0.2;
    [ObservableProperty] private int _maxIterations = 100;

    [ObservableProperty] private string _stage = "idle";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isTraining;

    [ObservableProperty] private TrainedModelInfo? _selectedJob;
    [ObservableProperty] private string _ollamaName = string.Empty;

    [RelayCommand]
    public async Task PickCsvAsync()
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Select training CSV"
            });
            if (result is not null) CsvPath = result.FullPath;
        }
        catch (Exception ex) { _logger.LogError(ex, "Pick CSV"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task TrainAsync()
    {
        if (IsTraining) return;
        if (string.IsNullOrWhiteSpace(CsvPath) || !File.Exists(CsvPath))
        {
            ErrorMessage = "Select a valid CSV file first.";
            return;
        }
        ErrorMessage = null;
        IsTraining = true;
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<TrainingProgress>(p =>
            {
                Stage = p.Stage;
                if (p.Percent.HasValue) Percent = p.Percent.Value;
                if (p.Message is not null) StatusMessage = p.Message;
            });
            var req = new TrainingJobRequest(
                Name: string.IsNullOrWhiteSpace(Name) ? "model-" + DateTime.UtcNow.ToString("HHmmss") : Name,
                Architecture: Architecture,
                CsvPath: CsvPath,
                LabelColumn: "Label",
                FeatureColumn: Architecture == TrainingArchitecture.RegressionFastTree ? "Feature" : "Text",
                HasHeader: HasHeader,
                TestSplit: TestSplit,
                MaxIterations: MaxIterations);
            var info = await _training.TrainAsync(req, progress, _cts.Token).ConfigureAwait(true);
            Completed.Insert(0, info);
            SelectedJob = info;
            OllamaName = info.Name;
        }
        catch (OperationCanceledException) { StatusMessage = "Cancelled"; }
        catch (Exception ex) { _logger.LogError(ex, "Train"); ErrorMessage = ex.Message; }
        finally { IsTraining = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand]
    public void CancelTraining() => _cts?.Cancel();

    [RelayCommand]
    public async Task ExportOnnxAsync()
    {
        if (SelectedJob is null) { ErrorMessage = "Select a completed job."; return; }
        try
        {
            var dir = Path.GetDirectoryName(SelectedJob.ArtifactPath) ?? FileSystem.AppDataDirectory;
            var target = Path.Combine(dir, SelectedJob.Name + ".onnx");
            var path = await _training.ExportOnnxAsync(SelectedJob.Id, target).ConfigureAwait(true);
            StatusMessage = $"Exported ONNX to {path}";
        }
        catch (Exception ex) { _logger.LogError(ex, "Export ONNX"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task RegisterOllamaAsync()
    {
        if (SelectedJob is null) { ErrorMessage = "Select a completed job."; return; }
        if (string.IsNullOrWhiteSpace(OllamaName)) { ErrorMessage = "Enter an Ollama model name."; return; }
        try
        {
            var name = await _training.RegisterWithOllamaAsync(SelectedJob.Id, OllamaName).ConfigureAwait(true);
            StatusMessage = $"Registered with Ollama as '{name}'";
        }
        catch (Exception ex) { _logger.LogError(ex, "Register Ollama"); ErrorMessage = ex.Message; }
    }
}
