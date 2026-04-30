namespace VirtmaAi.Services.Training;

public interface ITrainingService
{
    IReadOnlyList<TrainingArchitecture> Architectures { get; }
    Task<TrainedModelInfo> TrainAsync(TrainingJobRequest request, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default);
    Task<string> ExportOnnxAsync(Guid jobId, string targetPath, CancellationToken ct = default);
    Task<string> RegisterWithOllamaAsync(Guid jobId, string modelName, CancellationToken ct = default);
}

public enum TrainingArchitecture
{
    TextClassifierSdca = 0,
    TextClassifierLbfgsLogistic = 1,
    RegressionFastTree = 2
}

public sealed record TrainingJobRequest(
    string Name,
    TrainingArchitecture Architecture,
    string CsvPath,
    string LabelColumn,
    string FeatureColumn,
    bool HasHeader,
    double TestSplit,
    int MaxIterations);

public sealed record TrainedModelInfo(
    Guid Id,
    string Name,
    string ArtifactPath,
    double? Accuracy,
    double? Loss,
    DateTime CreatedAt);

public sealed record TrainingProgress(string Stage, double? Percent, string? Message);
