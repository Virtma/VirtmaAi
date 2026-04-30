using VirtmaAi.ViewModels.Graphify;

namespace VirtmaAi.Views.Graphify;

public partial class GraphViewerPage : ContentPage
{
    private readonly GraphifyViewModel _vm;
    private bool _webViewReady;
    private string? _pendingGraphJson;

    public GraphViewerPage(GraphifyViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        GraphWebView.Navigated += OnWebViewNavigated;
        _vm.PropertyChanged  += OnVmPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }

    // ─── WebView ready ────────────────────────────────────────────────────────

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success) return;
        _webViewReady = true;
        if (_pendingGraphJson is not null)
        {
            var payload = _pendingGraphJson;
            _pendingGraphJson = null;
            _ = SendGraphAsync(payload);
        }
    }

    // ─── Graph JSON changed ───────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GraphifyViewModel.GraphJson)) return;
        var json = _vm.GraphJson;

        if (string.IsNullOrWhiteSpace(json))
        {
            if (_webViewReady)
                _ = GraphWebView.EvaluateJavaScriptAsync("network && network.destroy(); document.getElementById('hud') && (document.getElementById('hud').textContent = 'No graph selected.');");
            return;
        }

        if (!_webViewReady) { _pendingGraphJson = json; return; }
        _ = SendGraphAsync(json);
    }

    // ─── Pass payload to vis.js via base64 ───────────────────────────────────

    private async Task SendGraphAsync(string jsonPayload)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(jsonPayload);
        var b64   = Convert.ToBase64String(bytes);
        var script = $$"""
            (function(){
              try {
                var bytes = Uint8Array.from(atob('{{b64}}'), function(c){ return c.charCodeAt(0); });
                var json  = new TextDecoder('utf-8').decode(bytes);
                window.loadGraph && window.loadGraph(JSON.parse(json));
              } catch(e) {
                var h = document.getElementById('hud');
                if (h) h.textContent = 'Load error: ' + e.message;
              }
            })();
            """;
        try { await GraphWebView.EvaluateJavaScriptAsync(script); }
        catch { /* race during page nav */ }
    }
}
