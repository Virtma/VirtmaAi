using Microsoft.Extensions.DependencyInjection;
using VirtmaAi.Services.AI;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Diagnostics;
using VirtmaAi.Services.ExternalApi;
using VirtmaAi.Services.FirstRun;
using VirtmaAi.Services.Routines;
using VirtmaAi.Services.Settings;
using VirtmaAi.Views.FirstRun;

namespace VirtmaAi;

public partial class App : Application
{
    private const string OnboardingShownKey = "virtmaai.onboarding.shown";

    private readonly IServiceProvider _services;
    private IRoutineScheduler? _scheduler;
    private IExternalApiHost? _apiHost;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _services.GetRequiredService<ICrashReporter>().Install();
        _services.GetRequiredService<IFirstRunService>().Completed += OnFirstRunCompleted;

        // Patch the schema on startup so columns added to entities after a user's first install
        // are picked up without requiring a fresh DB. Fire-and-forget — failures are logged but
        // don't block app boot.
        _ = TryEnsureSchemaAsync();
    }

    private async Task TryEnsureSchemaAsync()
    {
        try
        {
            var db = _services.GetRequiredService<IDatabaseService>();
            if (db.Current is not null)
                await db.EnsureSchemaUpToDateAsync().ConfigureAwait(false);
        }
        catch { /* non-fatal — failures already logged in DatabaseService */ }
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var firstRun = _services.GetRequiredService<IFirstRunService>();

        Page root = firstRun.IsFirstRun
            ? new NavigationPage(_services.GetRequiredService<FirstRunPage>())
            : new AppShell();

        var window = new Window(root) { Title = "VirtmaAi" };
        window.Activated += OnWindowActivated;
        window.Destroying += OnWindowDestroying;
        return window;
    }

    private void OnFirstRunCompleted(object? sender, EventArgs e)
    {
        Dispatcher.Dispatch(() =>
        {
            try
            {
                var window = Windows.FirstOrDefault();
                if (window is null) return;
                window.Page = new AppShell();
            }
            catch { }
        });
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_scheduler is null)
        {
            _scheduler = _services.GetRequiredService<IRoutineScheduler>();
            _ = _scheduler.StartAsync(CancellationToken.None);
        }
        _ = MaybeRunCliPromptAsync();

        if (sender is Window w && w.Page is { } page)
        {
            if (page.IsLoaded)
            {
                _ = MaybeShowOnboardingAsync();
            }
            else
            {
                page.Loaded -= OnRootPageLoaded;
                page.Loaded += OnRootPageLoaded;
            }
        }
    }

    private void OnRootPageLoaded(object? sender, EventArgs e)
    {
        if (sender is Page page) page.Loaded -= OnRootPageLoaded;
        _ = MaybeShowOnboardingAsync();
    }

    private async Task MaybeShowOnboardingAsync()
    {
        try
        {
            var firstRun = _services.GetRequiredService<IFirstRunService>();
            if (firstRun.IsFirstRun) return;
            var settings = _services.GetRequiredService<ISettingsService>();
            if (settings.Get<bool>(OnboardingShownKey)) return;
            var page = Current?.Windows.FirstOrDefault()?.Page;
            if (page is null) return;
            await page.DisplayAlertAsync(
                "Welcome to VirtmaAi",
                "Everything runs locally by default. Pull a model from the Models page, add API keys under API Keys, and open Help for a full feature tour.",
                "Got it");
            settings.Set(OnboardingShownKey, true);
        }
        catch { }
    }

    private async void OnWindowDestroying(object? sender, EventArgs e)
    {
        if (_scheduler is not null)
        {
            try { await _scheduler.StopAsync(); } catch { }
            _scheduler = null;
        }
        if (_apiHost is not null)
        {
            try { await _apiHost.StopAsync(); } catch { }
            _apiHost = null;
        }
    }

    private async Task MaybeRunCliPromptAsync()
    {
        var args = Environment.GetCommandLineArgs();
        string? prompt = null;
        string? model = null;
        string? provider = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--prompt" && i + 1 < args.Length) prompt = args[++i];
            else if (args[i] == "--model" && i + 1 < args.Length) model = args[++i];
            else if (args[i] == "--provider" && i + 1 < args.Length) provider = args[++i];
        }
        if (string.IsNullOrWhiteSpace(prompt)) return;

        try
        {
            var router = _services.GetRequiredService<IProviderRouter>();
            var p = router.Get(provider ?? "ollama");
            var request = new ChatRequest(model ?? string.Empty, new[] { new ChatMessage(ChatRole.User, prompt) });
            await foreach (var evt in p.StreamAsync(request))
            {
                if (evt is ContentChunk c) Console.Write(c.Text);
            }
            Console.WriteLine();
        }
        catch (Exception ex) { Console.Error.WriteLine("[--prompt] " + ex.Message); }
    }
}
