using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Training;

public sealed class TrainingService : ITrainingService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<TrainingService> _logger;
    private readonly ConcurrentDictionary<Guid, TrainedArtifacts> _artifacts = new();

    public TrainingService(ISettingsService settings, ILogger<TrainingService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<TrainingArchitecture> Architectures => Enum.GetValues<TrainingArchitecture>();

    public Task<TrainedModelInfo> TrainAsync(TrainingJobRequest request, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
    {
        return Task.Run(() => TrainCore(request, progress, ct), ct);
    }

    private TrainedModelInfo TrainCore(TrainingJobRequest request, IProgress<TrainingProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new TrainingProgress("loading", 0.05, "Loading data"));

        if (!File.Exists(request.CsvPath)) throw new FileNotFoundException("CSV not found", request.CsvPath);

        var ml = new MLContext(seed: 0);
        IDataView? data;
        DataViewSchema schema;
        string labelColumn;
        string featureColumn;

        if (request.Architecture == TrainingArchitecture.RegressionFastTree)
        {
            data = ml.Data.LoadFromTextFile<RegressionRow>(request.CsvPath, separatorChar: ',', hasHeader: request.HasHeader);
            labelColumn = nameof(RegressionRow.Label);
            featureColumn = nameof(RegressionRow.Feature);
        }
        else
        {
            data = ml.Data.LoadFromTextFile<ClassificationRow>(request.CsvPath, separatorChar: ',', hasHeader: request.HasHeader);
            labelColumn = nameof(ClassificationRow.Label);
            featureColumn = nameof(ClassificationRow.Text);
        }
        schema = data.Schema;

        ct.ThrowIfCancellationRequested();
        progress?.Report(new TrainingProgress("splitting", 0.1, $"Split {1 - request.TestSplit:P0} / {request.TestSplit:P0}"));
        var split = ml.Data.TrainTestSplit(data, request.TestSplit <= 0 ? 0.2 : Math.Min(0.5, request.TestSplit));

        ITransformer trained;
        double? accuracy = null;
        double? loss = null;

        switch (request.Architecture)
        {
            case TrainingArchitecture.TextClassifierSdca:
            {
                var pipeline = ml.Transforms.Text.FeaturizeText("Features", featureColumn)
                    .Append(ml.Transforms.Conversion.MapValueToKey("LabelKey", labelColumn))
                    .Append(ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(labelColumnName: "LabelKey", featureColumnName: "Features", maximumNumberOfIterations: request.MaxIterations <= 0 ? 100 : request.MaxIterations))
                    .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
                progress?.Report(new TrainingProgress("fitting", 0.3, "Training multiclass SDCA"));
                trained = pipeline.Fit(split.TrainSet);
                progress?.Report(new TrainingProgress("evaluating", 0.85, "Evaluating"));
                var metrics = ml.MulticlassClassification.Evaluate(trained.Transform(split.TestSet), "LabelKey");
                accuracy = metrics.MicroAccuracy;
                loss = metrics.LogLoss;
                break;
            }
            case TrainingArchitecture.TextClassifierLbfgsLogistic:
            {
                var lbfgsOpts = new LbfgsMaximumEntropyMulticlassTrainer.Options
                {
                    LabelColumnName = "LabelKey",
                    FeatureColumnName = "Features",
                    MaximumNumberOfIterations = request.MaxIterations <= 0 ? 100 : request.MaxIterations
                };
                var pipeline = ml.Transforms.Text.FeaturizeText("Features", featureColumn)
                    .Append(ml.Transforms.Conversion.MapValueToKey("LabelKey", labelColumn))
                    .Append(ml.MulticlassClassification.Trainers.LbfgsMaximumEntropy(lbfgsOpts))
                    .Append(ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
                progress?.Report(new TrainingProgress("fitting", 0.3, "Training L-BFGS"));
                trained = pipeline.Fit(split.TrainSet);
                progress?.Report(new TrainingProgress("evaluating", 0.85, "Evaluating"));
                var metrics = ml.MulticlassClassification.Evaluate(trained.Transform(split.TestSet), "LabelKey");
                accuracy = metrics.MicroAccuracy;
                loss = metrics.LogLoss;
                break;
            }
            case TrainingArchitecture.RegressionFastTree:
            {
                var pipeline = ml.Transforms.Concatenate("Features", featureColumn)
                    .Append(ml.Regression.Trainers.FastTree(labelColumnName: labelColumn, featureColumnName: "Features"));
                progress?.Report(new TrainingProgress("fitting", 0.3, "Training FastTree regression"));
                trained = pipeline.Fit(split.TrainSet);
                progress?.Report(new TrainingProgress("evaluating", 0.85, "Evaluating"));
                var metrics = ml.Regression.Evaluate(trained.Transform(split.TestSet), labelColumn);
                accuracy = metrics.RSquared;
                loss = metrics.MeanAbsoluteError;
                break;
            }
            default:
                throw new NotSupportedException($"Architecture {request.Architecture}");
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new TrainingProgress("saving", 0.95, "Writing artifact"));

        var id = Guid.NewGuid();
        var baseDir = string.IsNullOrWhiteSpace(_settings.DataDirectory) ? Microsoft.Maui.Storage.FileSystem.AppDataDirectory : _settings.DataDirectory;
        var root = Path.Combine(baseDir, "models", id.ToString("N"));
        Directory.CreateDirectory(root);
        var artifact = Path.Combine(root, "model.zip");
        ml.Model.Save(trained, schema, artifact);

        var info = new TrainedModelInfo(id, request.Name, artifact, accuracy, loss, DateTime.UtcNow);
        _artifacts[id] = new TrainedArtifacts(ml, trained, schema, info, request.Architecture);
        progress?.Report(new TrainingProgress("done", 1.0, $"Saved to {artifact}"));
        return info;
    }

    public Task<string> ExportOnnxAsync(Guid jobId, string targetPath, CancellationToken ct = default)
    {
        if (!_artifacts.TryGetValue(jobId, out var a))
            throw new InvalidOperationException("Job not found in current session — re-train before exporting.");
        return Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var fs = File.Create(targetPath);
            a.Ml.Model.ConvertToOnnx(a.Trained, LoadSampleDataView(a), fs);
            return targetPath;
        }, ct);
    }

    public async Task<string> RegisterWithOllamaAsync(Guid jobId, string modelName, CancellationToken ct = default)
    {
        if (!_artifacts.TryGetValue(jobId, out var a))
            throw new InvalidOperationException("Job not found in current session.");
        var root = Path.GetDirectoryName(a.Info.ArtifactPath)!;
        var modelfile = Path.Combine(root, "Modelfile");
        await File.WriteAllTextAsync(modelfile, $"FROM {Path.GetFileName(a.Info.ArtifactPath)}\n", ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo("ollama", $"create {modelName} -f \"{modelfile}\"")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ollama CLI not found on PATH");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException($"ollama create failed: {stderr}\n{stdout}");
        return modelName;
    }

    private static IDataView LoadSampleDataView(TrainedArtifacts a)
    {
        return a.Architecture switch
        {
            TrainingArchitecture.RegressionFastTree => a.Ml.Data.LoadFromEnumerable(new[] { new RegressionRow { Feature = 0f } }),
            _ => a.Ml.Data.LoadFromEnumerable(new[] { new ClassificationRow { Text = string.Empty, Label = string.Empty } })
        };
    }

    private sealed record TrainedArtifacts(MLContext Ml, ITransformer Trained, DataViewSchema Schema, TrainedModelInfo Info, TrainingArchitecture Architecture);

    private sealed class ClassificationRow
    {
        [LoadColumn(0)] public string Text { get; set; } = string.Empty;
        [LoadColumn(1)] public string Label { get; set; } = string.Empty;
    }

    private sealed class RegressionRow
    {
        [LoadColumn(0)] public float Feature { get; set; }
        [LoadColumn(1)] public float Label { get; set; }
    }
}
