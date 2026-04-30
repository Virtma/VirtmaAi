using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Graphify;

namespace VirtmaAi.ViewModels.Graphify;

public sealed partial class GraphifyViewModel : ViewModelBase
{
    private readonly IGraphifyRuntime _runtime;
    private readonly IGraphifyService _service;
    private readonly IDatabaseService _db;
    private readonly ILogger<GraphifyViewModel> _logger;

    public GraphifyViewModel(IGraphifyRuntime runtime, IGraphifyService service, IDatabaseService db, ILogger<GraphifyViewModel> logger)
    {
        _runtime = runtime;
        _service = service;
        _db = db;
        _logger = logger;
    }

    public ObservableCollection<GraphifyGraph> Graphs { get; } = new();
    public ObservableCollection<ConversationOption> Conversations { get; } = new();

    [ObservableProperty] private GraphifyGraph? _selected;
    [ObservableProperty] private ConversationOption? _selectedConversation;
    [ObservableProperty] private string? _projectDir;
    [ObservableProperty] private string? _reportMarkdown;
    [ObservableProperty] private string? _graphJson;
    [ObservableProperty] private string _status = "Not probed";
    [ObservableProperty] private bool _isDesktop;
    [ObservableProperty] private string? _progressStage;
    [ObservableProperty] private double? _progressPercent;

    partial void OnSelectedConversationChanged(ConversationOption? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(value.ProjectDir))
            ProjectDir = value.ProjectDir;
    }

    partial void OnSelectedChanged(GraphifyGraph? value)
    {
        _ = LoadSelectedFilesAsync();
    }

    private async Task LoadSelectedFilesAsync()
    {
        ReportMarkdown = null;
        GraphJson = null;
        if (Selected is null) return;
        try
        {
            ReportMarkdown = await _service.ReadReportAsync(Selected.Id);
            GraphJson = await _service.ReadGraphJsonAsync(Selected.Id);
        }
        catch (Exception ex) { _logger.LogError(ex, "load graph files"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsDesktop = _runtime.IsDesktop;
            if (!IsDesktop) { Status = "Graphify requires desktop — feature disabled on this platform."; return; }

            var probe = _runtime.Probe();
            Status = probe switch
            {
                { GraphifyInstalled: true } => $"Ready — {probe.PythonVersion ?? "python"} / {probe.GraphifyVersion ?? "graphify"}",
                { RuntimeReady: true } => "Portable Python is present but graphify isn't installed yet. Click Generate to install (~80MB, one-time).",
                _ => "Graphify needs a portable Python runtime that isn't bundled yet — see the Conversation Graph tab for an in-app graph view that works without Python."
            };

            var list = await _service.ListAsync();
            Graphs.Clear();
            foreach (var g in list) Graphs.Add(g);

            Conversations.Clear();
            if (_db.Current is not null)
            {
                await using var ctx = _db.CreateContext();
                var convs = await ctx.Conversations
                    .OrderByDescending(c => c.UpdatedAt)
                    .Select(c => new { c.Id, c.Title, c.ProjectDir })
                    .ToListAsync();
                foreach (var c in convs)
                    Conversations.Add(new ConversationOption(c.Id, c.Title, c.ProjectDir));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "graphify load"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectDir))
        {
            ErrorMessage = SelectedConversation is null
                ? "Pick a conversation that has a project directory, or enter one manually."
                : "Selected conversation has no project directory; enter one manually.";
            return;
        }
        try
        {
            IsBusy = true;
            var progress = new Progress<GraphifyInstallProgress>(p =>
            {
                ProgressStage = p.Stage;
                ProgressPercent = p.Percent;
            });
            var graph = await _service.GenerateAsync(ProjectDir, SelectedConversation?.Id, progress, CancellationToken.None);
            await LoadAsync();
            Selected = Graphs.FirstOrDefault(g => g.Id == graph.Id);
        }
        catch (Exception ex) { _logger.LogError(ex, "generate graph"); ErrorMessage = ex.Message; }
        finally { IsBusy = false; ProgressStage = null; ProgressPercent = null; }
    }

    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Selected is null) return;
        try { await _service.DeleteAsync(Selected.Id); Selected = null; await LoadAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "delete graph"); ErrorMessage = ex.Message; }
    }
}

public sealed record ConversationOption(Guid Id, string Title, string? ProjectDir)
{
    public string Display => string.IsNullOrWhiteSpace(ProjectDir) ? Title : $"{Title} — {ProjectDir}";
}
