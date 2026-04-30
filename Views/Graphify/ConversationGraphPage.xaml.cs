using VirtmaAi.ViewModels.Graphify;

namespace VirtmaAi.Views.Graphify;

public partial class ConversationGraphPage : ContentPage
{
    private readonly ConversationGraphViewModel _vm;
    private bool _webViewReady;
    private string? _pendingPayload;

    public ConversationGraphPage(ConversationGraphViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        _vm.GraphReady += OnGraphReady;
        _vm.GraphCleared += OnGraphCleared;
        GraphWebView.Navigated += OnWebViewNavigated;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success) return;
        _webViewReady = true;
        if (_pendingPayload is not null)
        {
            var payload = _pendingPayload;
            _pendingPayload = null;
            _ = SendGraphAsync(payload);
        }
    }

    private void OnGraphReady(object? sender, string payload)
    {
        if (!_webViewReady) { _pendingPayload = payload; return; }
        _ = SendGraphAsync(payload);
    }

    private void OnGraphCleared(object? sender, EventArgs e)
    {
        if (!_webViewReady) return;
        _ = GraphWebView.EvaluateJavaScriptAsync("window.clearGraph && window.clearGraph();");
    }

    private async Task SendGraphAsync(string payload)
    {
        // Pass the JSON payload as base64 so we don't have to chase escape-encoding bugs through
        // a JS string literal (any backslash, quote, or unicode escape was previously a landmine).
        // The HTML template decodes via atob → TextDecoder('utf-8') → JSON.parse, which is
        // bullet-proof regardless of message content.
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var b64 = Convert.ToBase64String(bytes);
        var script = $"window.loadGraphB64 && window.loadGraphB64('{b64}');";
        try { await GraphWebView.EvaluateJavaScriptAsync(script); }
        catch { /* swallow — race during page nav */ }
    }
}
