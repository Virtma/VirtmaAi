using VirtmaAi.ViewModels.Chat;

namespace VirtmaAi.Views.Chat;

public partial class ChatPage : ContentPage
{
    // ====================================================================================
    // Autoscroll (rewritten from scratch, 2026-04-28).
    //
    // Contract:
    //   1. The page only auto-scrolls when the user is "pinned to bottom" — i.e. they have
    //      scrolled all the way to the latest message.
    //   2. As long as they remain pinned, new content (streamed tokens, new messages) keeps
    //      the latest message in view.
    //   3. The instant the user scrolls upward to read history, autoscroll DISENGAGES. Their
    //      scroll position is NEVER moved by the system from that point on.
    //   4. If the user scrolls back to the bottom, autoscroll RE-ENGAGES.
    //   5. We never touch the scroll position outside of these rules — no "settle" passes,
    //      no cascading dispatches, no fighting between platform paths.
    //
    // Implementation: read scroll state directly from the WinUI ScrollViewer (Windows path) or
    // CollectionView events (other platforms). The pinned flag updates only on user scroll
    // events; our own ChangeView calls are flagged with `_isProgrammaticScroll` so the
    // resulting Scrolled event doesn't accidentally toggle the pin.
    // ====================================================================================

    private const double BottomEpsilonPx = 6.0;

    private readonly ChatViewModel _vm;
    private bool _pinnedToBottom = true;
    private bool _isProgrammaticScroll;

    public ChatPage(ChatViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        InputEditor.HandlerChanged += OnInputEditorHandlerChanged;
        MessagesList.HandlerChanged += OnMessagesListHandlerChanged;

        // Forward vertical wheel events that bubble up from HighlightedMarkdownView
        // WebViews (which would otherwise eat the scroll and prevent the CollectionView
        // from scrolling while the cursor is over a code block).
        HighlightedMarkdownView.ScrollDeltaRequested += OnWebViewScrollDelta;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
        // Re-pin on (re-)open and snap to bottom once layout is ready.
        _pinnedToBottom = true;
        ScrollToBottomIfPinned();
        // Re-subscribe in case OnDisappearing removed us (e.g. page nav).
        HighlightedMarkdownView.ScrollDeltaRequested -= OnWebViewScrollDelta;
        HighlightedMarkdownView.ScrollDeltaRequested += OnWebViewScrollDelta;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Unsubscribe the static event so we don't receive scrolls from other pages.
        HighlightedMarkdownView.ScrollDeltaRequested -= OnWebViewScrollDelta;
    }

    /// <summary>
    /// Receives vertical wheel deltas forwarded from <see cref="HighlightedMarkdownView"/>
    /// instances that would otherwise be silently consumed by WebView2.
    /// On Windows, forwards the delta directly to the CollectionView's ScrollViewer.
    /// On other platforms the event is never raised (WebView2 is Windows-specific).
    /// </summary>
    private void OnWebViewScrollDelta(object? sender, double deltaY)
    {
#if WINDOWS
        try
        {
            if (_scrollViewer is null &&
                MessagesList.Handler?.PlatformView is Microsoft.UI.Xaml.DependencyObject root)
                _scrollViewer = FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);

            if (_scrollViewer is null) return;

            // ChangeView with disableAnimation=true for instant, jank-free scrolling.
            var newOffset = _scrollViewer.VerticalOffset + deltaY;
            newOffset = Math.Max(0, Math.Min(newOffset, _scrollViewer.ScrollableHeight));
            _scrollViewer.ChangeView(null, newOffset, null, disableAnimation: true);
        }
        catch { /* scroll races are safe to ignore */ }
#endif
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // Conversation switch — re-pin so the freshly-loaded conversation snaps to its
                // latest message.
                _pinnedToBottom = true;
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (var obj in e.NewItems)
                {
                    if (obj is not ChatMessageItem item) continue;
                    if (item.IsStreaming)
                    {
                        // A new streaming assistant item means the user just sent a message.
                        // Re-pin unconditionally so they always see their response arrive,
                        // even if they were reading back through history.
                        _pinnedToBottom = true;
                        item.PropertyChanged += OnStreamingItemPropertyChanged;
                    }
                }
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var obj in e.OldItems)
                    if (obj is ChatMessageItem item)
                        item.PropertyChanged -= OnStreamingItemPropertyChanged;
                break;
        }

        // New messages only follow the user if they were already at the bottom.
        ScrollToBottomIfPinned();
    }

    private void OnStreamingItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ChatMessageItem item) return;
        if (e.PropertyName == nameof(ChatMessageItem.IsStreaming) && !item.IsStreaming)
        {
            item.PropertyChanged -= OnStreamingItemPropertyChanged;
            // No "settle" pass — if the user is pinned, the next layout change will scroll us;
            // if they aren't, we leave them alone.
            ScrollToBottomIfPinned();
            return;
        }
        if (e.PropertyName == nameof(ChatMessageItem.Content) || e.PropertyName == nameof(ChatMessageItem.Thinking))
        {
            ScrollToBottomIfPinned();
        }
    }

    private void ScrollToBottomIfPinned()
    {
        if (!_pinnedToBottom) return;
        if (_vm.Messages.Count == 0) return;
        Dispatcher.Dispatch(() =>
        {
            try
            {
                _isProgrammaticScroll = true;
#if WINDOWS
                if (!TryWindowsScrollToBottom())
#endif
                {
                    MessagesList.ScrollTo(_vm.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
                }
            }
            catch { /* scroll races are fine */ }
            finally
            {
                // Clear on the next dispatcher turn so the resulting Scrolled event (which fires
                // synchronously after ChangeView in many cases, but can be deferred) doesn't get
                // misread as a user scroll.
                Dispatcher.Dispatch(() => _isProgrammaticScroll = false);
            }
        });
    }

    // ===================== Platform-specific scroll detection =====================
    // Windows: hook the underlying ScrollViewer's ViewChanged event so we read the
    // authoritative VerticalOffset and ScrollableHeight. CollectionView.Scrolled on Windows
    // is unreliable for "is at bottom" because LastVisibleItemIndex isn't always the last
    // item even when scrolled fully down (depends on item virtualization).

    private void OnMessagesListHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        BindWindowsScrollViewer();
#endif
    }

#if WINDOWS
    private Microsoft.UI.Xaml.Controls.ScrollViewer? _scrollViewer;

    private void BindWindowsScrollViewer()
    {
        if (_scrollViewer is not null)
        {
            _scrollViewer.ViewChanged -= OnWindowsViewChanged;
            _scrollViewer = null;
        }
        if (MessagesList.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject root) return;
        // Schedule a microtask so the visual tree has time to materialize children.
        Dispatcher.Dispatch(() =>
        {
            var sv = FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
            if (sv is null) return;
            _scrollViewer = sv;
            sv.ViewChanged -= OnWindowsViewChanged;
            sv.ViewChanged += OnWindowsViewChanged;
        });
    }

    private void OnWindowsViewChanged(object? sender, Microsoft.UI.Xaml.Controls.ScrollViewerViewChangedEventArgs args)
    {
        if (_isProgrammaticScroll) return;
        if (sender is not Microsoft.UI.Xaml.Controls.ScrollViewer sv) return;
        // "At bottom" means the offset is within a hair of the maximum scrollable height. Tiny
        // sub-pixel differences (e.g. 0.4px) shouldn't disengage the pin.
        var atBottom = sv.VerticalOffset >= sv.ScrollableHeight - BottomEpsilonPx;
        _pinnedToBottom = atBottom;
    }

    private bool TryWindowsScrollToBottom()
    {
        try
        {
            if (_scrollViewer is null && MessagesList.Handler?.PlatformView is Microsoft.UI.Xaml.DependencyObject root)
                _scrollViewer = FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(root);
            if (_scrollViewer is null) return false;
            _scrollViewer.ChangeView(null, _scrollViewer.ScrollableHeight, null, disableAnimation: true);
            return true;
        }
        catch { return false; }
    }

    private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
    {
        if (root is T match) return match;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
#endif

    // ===================== Cross-platform fallback =====================
    // For non-Windows builds, hook CollectionView.Scrolled and approximate "at bottom" using
    // LastVisibleItemIndex.

#if !WINDOWS
    private void HookFallbackScroll()
    {
        MessagesList.Scrolled -= OnMessagesScrolledFallback;
        MessagesList.Scrolled += OnMessagesScrolledFallback;
    }

    private void OnMessagesScrolledFallback(object? sender, ItemsViewScrolledEventArgs e)
    {
        if (_isProgrammaticScroll) return;
        var count = _vm.Messages.Count;
        if (count == 0) { _pinnedToBottom = true; return; }
        _pinnedToBottom = e.LastVisibleItemIndex >= count - 1;
    }
#endif

    // ===================== Input editor (Windows: Enter to send) =====================

    private void OnInputEditorHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        if (InputEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            tb.AcceptsReturn = true;
            tb.PreviewKeyDown -= OnPlatformEditorPreviewKeyDown;
            tb.PreviewKeyDown += OnPlatformEditorPreviewKeyDown;
        }
#endif
    }

#if WINDOWS
    private void OnPlatformEditorPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter) return;

        var shiftState = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        var shiftDown = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (shiftDown) return;

        args.Handled = true;
        InputEditor.Unfocus();
        Dispatcher.Dispatch(() =>
        {
            if (_vm.SendCommand.CanExecute(null))
                _vm.SendCommand.Execute(null);
        });
    }
#endif
}
