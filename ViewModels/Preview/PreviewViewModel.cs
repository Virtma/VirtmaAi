using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Media;

namespace VirtmaAi.ViewModels.Preview;

public sealed partial class PreviewViewModel : ViewModelBase
{
    private readonly IPreviewDispatcher _dispatcher;
    private readonly ILogger<PreviewViewModel> _logger;

    public PreviewViewModel(IPreviewDispatcher dispatcher, ILogger<PreviewViewModel> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    [ObservableProperty] private string? _source;
    [ObservableProperty] private PreviewKind _kind = PreviewKind.Unknown;
    [ObservableProperty] private string? _textContent;
    [ObservableProperty] private bool _isPanelOpen;

    public bool IsImage => Kind == PreviewKind.Image;
    public bool IsVideo => Kind == PreviewKind.Video;
    public bool IsAudio => Kind == PreviewKind.Audio;
    public bool IsWeb => Kind == PreviewKind.WebUrl || Kind == PreviewKind.Html;
    public bool IsText => Kind is PreviewKind.Text or PreviewKind.Code or PreviewKind.Markdown
                                  or PreviewKind.Json or PreviewKind.Xml or PreviewKind.Yaml;
    public bool IsUnsupported => Kind == PreviewKind.Unknown || Kind == PreviewKind.Pdf || Kind == PreviewKind.Model3D;

    partial void OnKindChanged(PreviewKind value)
    {
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(IsAudio));
        OnPropertyChanged(nameof(IsWeb));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsUnsupported));
    }

    [RelayCommand]
    public async Task OpenAsync(string? pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl)) return;
        try
        {
            Source = pathOrUrl;
            Kind = _dispatcher.Classify(pathOrUrl);
            IsPanelOpen = true;
            TextContent = null;
            if (IsText && File.Exists(pathOrUrl))
            {
                TextContent = await File.ReadAllTextAsync(pathOrUrl);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Open preview"); ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public void Close()
    {
        IsPanelOpen = false;
        Source = null;
        Kind = PreviewKind.Unknown;
        TextContent = null;
    }
}
