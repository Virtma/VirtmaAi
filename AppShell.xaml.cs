using VirtmaAi.Views.Notifications;

namespace VirtmaAi
{
    public partial class AppShell : Shell
    {
        // Track pages we've already enriched with a toast host so we don't double-attach.
        private static readonly HashSet<int> ToastHostedPages = new();

        public AppShell()
        {
            InitializeComponent();
            Navigated += OnShellNavigated;
        }

        private void OnShellNavigated(object? sender, ShellNavigatedEventArgs e)
        {
            if (CurrentPage is not ContentPage page) return;
            // Use the page's hashcode as identity; Shell reuses page instances.
            var key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(page);
            if (!ToastHostedPages.Add(key)) return;
            AttachToastHost(page);
        }

        private static void AttachToastHost(ContentPage page)
        {
            try
            {
                var host = new ToastHostView
                {
                    VerticalOptions = LayoutOptions.Start,
                    HorizontalOptions = LayoutOptions.End,
                };

                if (page.Content is Grid grid)
                {
                    // Layer on top of every existing column/row in the page's root grid.
                    if (grid.ColumnDefinitions.Count > 1)
                    {
                        Grid.SetColumn(host, 0);
                        Grid.SetColumnSpan(host, grid.ColumnDefinitions.Count);
                    }
                    if (grid.RowDefinitions.Count > 1)
                    {
                        Grid.SetRow(host, 0);
                        Grid.SetRowSpan(host, grid.RowDefinitions.Count);
                    }
                    grid.Children.Add(host);
                }
                else
                {
                    // Wrap the existing content in a Grid so we can layer the host on top.
                    var existing = page.Content;
                    page.Content = null;
                    var wrapper = new Grid();
                    if (existing is not null) wrapper.Children.Add(existing);
                    wrapper.Children.Add(host);
                    page.Content = wrapper;
                }
            }
            catch
            {
                // If attaching fails for any reason, the page is still usable; just no toasts here.
            }
        }
    }
}
