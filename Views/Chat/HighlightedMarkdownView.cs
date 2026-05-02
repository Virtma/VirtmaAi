using Markdig;

namespace VirtmaAi.Views.Chat;

/// <summary>
/// A <see cref="ContentView"/> that renders Markdown with full per-language syntax
/// highlighting using a WebView + highlight.js (CDN). Intended for the "settled" state
/// of a chat message — i.e. after streaming has finished — so there is no conflict with
/// the live-update path used by <c>MarkdownView</c> during streaming.
///
/// Height is self-reported: after the HTML page loads, embedded JavaScript measures
/// <c>document.body.scrollHeight</c> and the view updates its own <c>HeightRequest</c>.
/// </summary>
public sealed class HighlightedMarkdownView : ContentView
{
    // ──────────────────────────── Bindable properties ────────────────────────────

    public static readonly BindableProperty MarkdownTextProperty =
        BindableProperty.Create(nameof(MarkdownText), typeof(string),
            typeof(HighlightedMarkdownView), default(string?),
            propertyChanged: (b, _, n) =>
            {
                var hmv = (HighlightedMarkdownView)b;
                // Only render when this view is the active renderer.  During streaming,
                // IsActive=false so we skip the WebView load entirely; the fast native
                // MarkdownView is doing that job.  When streaming ends, IsActive flips to
                // true and OnIsActiveChanged calls Refresh() itself.
                if (hmv.IsActive) hmv.Refresh((string?)n);
            });

    /// <summary>
    /// Controls whether the WebView renderer is the active content renderer.
    /// <para>
    /// <c>False</c> (default, during streaming): HeightRequest is collapsed to 0 so the
    /// control takes no space, but the WebView platform handler is kept alive so that the
    /// first navigation fires reliably when we flip to <c>True</c>.
    /// </para>
    /// <para>
    /// <c>True</c> (streaming ended): HeightRequest is reset to 1 px and <see cref="Refresh"/>
    /// is invoked immediately with the current <see cref="MarkdownText"/>.  The
    /// <see cref="OnNavigated"/> callback then measures the rendered height and expands the
    /// view to fit.
    /// </para>
    /// Bind to <c>UseWebView</c> on the item view-model so the switch happens exactly once,
    /// the moment the streaming flag clears.
    /// </summary>
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool),
            typeof(HighlightedMarkdownView), false,
            propertyChanged: (b, _, n) => ((HighlightedMarkdownView)b).OnIsActiveChanged((bool)n));

    public static readonly BindableProperty IsDarkModeProperty =
        BindableProperty.Create(nameof(IsDarkMode), typeof(bool),
            typeof(HighlightedMarkdownView), true,
            propertyChanged: (b, _, _) =>
            {
                var hmv = (HighlightedMarkdownView)b;
                if (hmv.IsActive) hmv.Refresh();
            });

    public static readonly BindableProperty OpenLinkCommandProperty =
        BindableProperty.Create(nameof(OpenLinkCommand), typeof(System.Windows.Input.ICommand),
            typeof(HighlightedMarkdownView), default(System.Windows.Input.ICommand?));

    // ──────────────────────────── Properties ─────────────────────────────────────

    public string? MarkdownText
    {
        get => (string?)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public bool IsDarkMode
    {
        get => (bool)GetValue(IsDarkModeProperty);
        set => SetValue(IsDarkModeProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public System.Windows.Input.ICommand? OpenLinkCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(OpenLinkCommandProperty);
        set => SetValue(OpenLinkCommandProperty, value);
    }

    // ──────────────────────────── Fields ─────────────────────────────────────────

    private readonly WebView _webView;
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

    // ── Windows: scroll forwarding via JS postMessage + WebMessageReceived ─────────
    // WebView2's Chromium renderer processes WM_MOUSEWHEEL at the Win32 HWND level,
    // BEFORE WinUI's UIElement event routing sees it.  AddHandler(handledEventsToo:true)
    // is therefore ineffective — the event never enters the WinUI tree at all.
    //
    // Fix: the page JS registers a 'wheel' listener that calls
    //   window.chrome.webview.postMessage({t:'vs', d:e.deltaY})
    // on every vertical tick.  CoreWebView2.WebMessageReceived delivers those payloads
    // on the UI thread; we decode them here and fire ScrollDeltaRequested so ChatPage
    // can forward the delta to its ScrollViewer.  Horizontal scroll (Shift+wheel /
    // trackpad two-finger horizontal) is passed through so <pre> blocks remain
    // independently scrollable.
#if WINDOWS
    private bool _scrollMsgHooked;
#endif

    /// <summary>
    /// Raised (on the UI thread) when the embedded WebView2 captures a vertical wheel
    /// event.  The argument is the JS <c>deltaY</c> value in pixels
    /// (<b>positive = scroll down</b>, negative = scroll up).
    /// <see cref="ChatPage"/> subscribes here and forwards the delta to the
    /// <c>MessagesList</c> <see cref="Microsoft.UI.Xaml.Controls.ScrollViewer"/>
    /// via <c>VerticalOffset + delta</c>.
    /// </summary>
    internal static event EventHandler<double>? ScrollDeltaRequested;

    // ──────────────────────────── Constructor ────────────────────────────────────

    public HighlightedMarkdownView()
    {
        _webView = new WebView
        {
            BackgroundColor = Colors.Transparent,
            HeightRequest = 0,
            HorizontalOptions = LayoutOptions.Fill,
        };
        _webView.Navigating += OnNavigating;
        _webView.Navigated  += OnNavigated;
        Content = _webView;

        // Start collapsed — takes no space until IsActive=true.
        HeightRequest = 0;

#if WINDOWS
        // Subscribe to HandlerChanged so we can hook CoreWebView2.WebMessageReceived as
        // soon as the Chromium engine initialises (which happens asynchronously after the
        // WinUI WebView2 control first renders).
        _webView.HandlerChanged += OnWebViewHandlerChanged;
#endif
    }

    // ──────────────────────────── Core logic ─────────────────────────────────────

    /// <summary>
    /// Called when <see cref="IsActive"/> changes.  Expanding (true → active) triggers
    /// <see cref="Refresh"/> immediately so the WebView gets its first navigation at the
    /// exact moment the DataContext signals that streaming has ended.  Collapsing resets
    /// heights to 0 so the inactive control takes no layout space.
    /// </summary>
    private void OnIsActiveChanged(bool active)
    {
        if (active)
        {
            // Allow layout space and trigger the initial WebView load.
            _webView.HeightRequest = 1;
            HeightRequest = 1;
            Refresh();
        }
        else
        {
            // Collapse without removing from the visual tree — WebView handler stays alive
            // so the next Refresh() fires reliably when IsActive flips back to true.
            _webView.HeightRequest = 0;
            HeightRequest = 0;
        }
    }

    private void Refresh(string? markdown = null)
    {
        markdown ??= MarkdownText;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            _webView.Source = null;
            HeightRequest = 1;
            return;
        }

        var htmlBody = Markdig.Markdown.ToHtml(markdown, Pipeline);
        var page     = BuildPage(htmlBody, IsDarkMode);
        _webView.Source = new HtmlWebViewSource { Html = page };
    }

    /// <summary>
    /// After the HTML page loads, measure its height via JavaScript and update
    /// <see cref="HeightRequest"/> so the CollectionView item sizes correctly.
    /// We measure the inner content-wrap div (not the document/viewport) so the
    /// height reflects actual rendered content, not the initial 1 px WebView clip.
    /// A short delay lets any CDN resources (highlight.js) finish applying styles
    /// before we snapshot the height.
    /// </summary>
    private async void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success) return;
        try
        {
            // Wait for highlight.js and font resources to finish rendering.
            await Task.Delay(180).ConfigureAwait(true);

            // Measure the content wrapper div rather than the document so we get
            // the real content height, not the WebView's current viewport height.
            var raw = await _webView.EvaluateJavaScriptAsync(
                "(function(){ var w = document.getElementById('content-wrap'); return w ? w.offsetHeight + '' : document.body.scrollHeight + ''; })()").ConfigureAwait(true);

            if (raw is not null && double.TryParse(raw, out var px) && px > 4)
            {
                // Guard: clamp against runaway values (pathological code blocks).
                px = Math.Min(px, 8000);
                _webView.HeightRequest = px + 4;
                HeightRequest          = px + 4;
                InvalidateMeasure();
            }

        }
        catch { /* layout race — ignore */ }
    }

#if WINDOWS
    private void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
        if (_webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 wv2) return;
        // CoreWebView2 initialises asynchronously after the WinUI control first renders.
        // If it's already ready (e.g. handler re-attaching after hot-reload), hook immediately;
        // otherwise register for the initialization-completed event.
        if (wv2.CoreWebView2 is not null)
            AttachScrollHook(wv2.CoreWebView2);
        else
            wv2.CoreWebView2Initialized += (s, _) =>
            {
                if (s.CoreWebView2 is not null)
                    AttachScrollHook(s.CoreWebView2);
            };
    }

    private void AttachScrollHook(Microsoft.Web.WebView2.Core.CoreWebView2 coreWv)
    {
        if (_scrollMsgHooked) return;
        _scrollMsgHooked = true;
        coreWv.WebMessageReceived += OnScrollWebMessage;
    }

    private void OnScrollWebMessage(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // JS sends: window.chrome.webview.postMessage({t:'vs', d:e.deltaY})
            // deltaY convention: positive = scroll down, negative = scroll up.
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var t) || t.GetString() != "vs") return;
            if (!root.TryGetProperty("d", out var d)) return;
            var delta = d.GetDouble();
            if (delta == 0) return;
            ScrollDeltaRequested?.Invoke(this, delta);
        }
        catch { /* ignore malformed messages */ }
    }
#endif

    /// <summary>
    /// Intercept WebView navigation so that user link clicks open in the system
    /// browser rather than navigating the embedded view away.
    /// </summary>
    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        // Always let the initial HTML content load through. Depending on the platform:
        //   Android  : about:blank  (loadData) or file://
        //   iOS/Mac  : about:blank  (WKWebView.LoadHTMLString)
        //   Windows  : data:text/html;base64,… (WebView2 converts HtmlWebViewSource to a data URI)
        //              or ms-local-stream: / ms-appx-web: on older WinUI builds
        // Cancelling any of these prevents the page from rendering entirely.
        if (e.Url.StartsWith("about:",            StringComparison.OrdinalIgnoreCase)) return;
        if (e.Url.StartsWith("blob:",             StringComparison.OrdinalIgnoreCase)) return;
        if (e.Url.StartsWith("data:",             StringComparison.OrdinalIgnoreCase)) return;
        if (e.Url.StartsWith("ms-local-stream:",  StringComparison.OrdinalIgnoreCase)) return;
        if (e.Url.StartsWith("ms-appx-web:",      StringComparison.OrdinalIgnoreCase)) return;
        if (e.Url.StartsWith("file:",             StringComparison.OrdinalIgnoreCase)) return;

        // Cancel the in-WebView navigation and open externally.
        e.Cancel = true;
        var url = e.Url;
        Dispatcher.Dispatch(() =>
        {
            if (OpenLinkCommand?.CanExecute(url) == true)
                OpenLinkCommand.Execute(url);
        });
    }

    // ──────────────────────────── HTML template ──────────────────────────────────

    private static string BuildPage(string htmlBody, bool dark)
    {
        // highlight.js (cdnjs) — served over HTTPS, consistent with the vis-network CDN
        // already used by the conversation-graph page. Uses the "atom-one-dark" theme for
        // dark mode and "atom-one-light" for light mode — both read well.
        const string hljsVersion  = "11.10.0";
        var themeFile = dark ? "atom-one-dark.min.css" : "atom-one-light.min.css";
        var hljsCss   = $"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/{hljsVersion}/styles/{themeFile}";
        var hljsJs    = $"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/{hljsVersion}/highlight.min.js";

        // Colours matched to App.xaml's dark theme tokens.
        var bg       = dark ? "#0a0a0a" : "#ffffff";
        var fg       = dark ? "#ffffff" : "#0a0a0a";
        var fgSec    = dark ? "#acacac" : "#555555";
        var linkClr  = dark ? "#FF3B30" : "#E10600";
        var codeBg   = dark ? "#1f1f1f" : "#f0f0f0";
        var codeFg   = dark ? "#FFE082" : "#b85c00";
        var codeBdr  = dark ? "#525252" : "#d0d0d0";
        var bqBdr    = "#E10600";
        var bqFg     = dark ? "#acacac" : "#333333";
        var tblHBg   = dark ? "#141414" : "#eaeaea";
        var tblBdr   = dark ? "#404040" : "#d0d0d0";
        var divClr   = dark ? "#404040" : "#d0d0d0";

        // $$"""...""" raw string: single { } are literal characters (CSS/JS braces),
        // {{expr}} is the C# interpolation delimiter — no escaping of CSS rules needed.
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <link rel="stylesheet" href="{{hljsCss}}">
              <style>
                *, *::before, *::after { box-sizing: border-box; }
                /* height:auto prevents the document from expanding to the WebView
                   viewport height before content is measured. */
                html { height: auto; overflow: hidden; }
                body {
                  margin: 0; padding: 0;
                  height: auto;
                  background: {{bg}};
                  color: {{fg}};
                  font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
                  font-size: 14px;
                  line-height: 1.65;
                  word-break: break-word;
                  overflow-x: hidden;
                }
                #content-wrap { padding: 2px 0; }
                h1,h2,h3,h4,h5,h6 { color:{{fg}}; margin:.8em 0 .35em; line-height:1.3; }
                h1 { font-size:1.5em; }
                h2 { font-size:1.3em; }
                h3 { font-size:1.15em; }
                h4,h5,h6 { font-size:1em; }
                p { margin:.4em 0; }
                p:first-child { margin-top:0; }
                p:last-child  { margin-bottom:0; }
                a { color:{{linkClr}}; text-decoration:none; }
                a:hover { text-decoration:underline; }
                :not(pre) > code {
                  background:{{codeBg}};
                  color:{{codeFg}};
                  font-family:'Consolas','Courier New',monospace;
                  font-size:.88em;
                  padding:1px 5px;
                  border-radius:3px;
                  border:1px solid {{codeBdr}};
                }
                pre {
                  background:{{codeBg}} !important;
                  border:1px solid {{codeBdr}};
                  border-radius:5px;
                  padding:12px 14px;
                  margin:8px 0;
                  overflow-x:auto;
                }
                pre code.hljs {
                  background:transparent !important;
                  padding:0;
                  font-family:'Consolas','Courier New',monospace;
                  font-size:13px;
                  line-height:1.55;
                }
                blockquote {
                  border-left:3px solid {{bqBdr}};
                  margin:8px 0;
                  padding:4px 12px;
                  color:{{bqFg}};
                }
                ul, ol { padding-left:1.6em; margin:.35em 0; }
                li { margin:.2em 0; }
                hr { border:none; border-top:1px solid {{divClr}}; margin:12px 0; }
                table { border-collapse:collapse; width:100%; margin:8px 0; }
                th { background:{{tblHBg}}; color:{{fg}}; padding:6px 10px; border:1px solid {{tblBdr}}; font-weight:600; text-align:left; }
                td { padding:6px 10px; border:1px solid {{tblBdr}}; color:{{fg}}; }
                tr:nth-child(even) td { background:rgba(255,255,255,.03); }
                img { max-width:100%; height:auto; border-radius:4px; }
                .secondary { color:{{fgSec}}; }
              </style>
            </head>
            <body>
              <div id="content-wrap">
              {{htmlBody}}
              </div>
              <script src="{{hljsJs}}"></script>
              <script>
                document.querySelectorAll('pre code').forEach(function(el) {
                  hljs.highlightElement(el);
                });

                // Forward vertical wheel events to the host ScrollViewer via postMessage.
                // window.chrome.webview is WebView2-only (Windows); the feature-detect guard
                // makes this a no-op on Android/iOS WKWebView without any errors.
                // Shift+wheel (or a purely horizontal delta) is passed through unchanged so
                // <pre> code blocks remain independently scrollable.
                window.addEventListener('wheel', function(e) {
                  if (e.deltaY !== 0 && !e.shiftKey) {
                    e.preventDefault();
                    if (window.chrome && window.chrome.webview) {
                      window.chrome.webview.postMessage({t:'vs', d:e.deltaY});
                    }
                  }
                }, { passive: false });
              </script>
            </body>
            </html>
            """;
    }
}
