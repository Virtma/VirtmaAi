using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Serilog;
using SkiaSharp.Views.Maui.Controls.Hosting;
using VirtmaAi.Services.AI;
using VirtmaAi.Services.AI.Providers;
using VirtmaAi.Services.Capture;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Data.MySqlBootstrap;
using VirtmaAi.Services.Database;
using VirtmaAi.Services.Diagnostics;
using VirtmaAi.Services.ExternalApi;
using VirtmaAi.Services.FileSystem;
using VirtmaAi.Services.FirstRun;
using VirtmaAi.Services.Graphify;
using VirtmaAi.Services.Integrations;
using VirtmaAi.Services.Media;
using VirtmaAi.Services.Notifications;
using VirtmaAi.Services.Plugins;
using VirtmaAi.Services.Plugins.BuiltIn;
using VirtmaAi.Services.References;
using VirtmaAi.Services.Routines;
using VirtmaAi.Services.Settings;
using VirtmaAi.Services.Skills;
using VirtmaAi.Services.System;
using VirtmaAi.Services.Themes;
using VirtmaAi.Services.Training;
using VirtmaAi.ViewModels.Chat;
using VirtmaAi.ViewModels.Database;
using VirtmaAi.ViewModels.FirstRun;
using VirtmaAi.ViewModels.Graphify;
using VirtmaAi.ViewModels.Integrations;
using VirtmaAi.ViewModels.Models;
using VirtmaAi.ViewModels.Plugins;
using VirtmaAi.ViewModels.Preview;
using VirtmaAi.ViewModels.Routines;
using VirtmaAi.ViewModels.Settings;
using VirtmaAi.ViewModels.Skills;
using VirtmaAi.ViewModels.Training;
using VirtmaAi.Views.Chat;
using VirtmaAi.Views.Database;
using VirtmaAi.Views.FirstRun;
using VirtmaAi.Views.Graphify;
using VirtmaAi.Views.Integrations;
using VirtmaAi.Views.Models;
using VirtmaAi.Views.Plugins;
using VirtmaAi.Views.Routines;
using VirtmaAi.Views.Settings;
using VirtmaAi.Views.Skills;
using VirtmaAi.Views.Training;

namespace VirtmaAi;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: false)
            .UseSkiaSharp()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IFirstRunService, FirstRunService>();
        builder.Services.AddSingleton<IMySqlLifecycleService, MySqlLifecycleService>();
        builder.Services.AddSingleton<SqliteProvisioner>();
        builder.Services.AddSingleton<MySqlProvisioner>();
        builder.Services.AddSingleton<IDatabaseService, DatabaseService>();

        builder.Services.AddTransient<FirstRunViewModel>();
        builder.Services.AddTransient<FirstRunPage>();

        builder.Services.AddSingleton<ISandboxedFileSystem, SandboxedFileSystem>();
        builder.Services.AddSingleton<IGitDiffService, GitDiffService>();
        builder.Services.AddSingleton<IFolderPickerService, FolderPickerService>();
        builder.Services.AddSingleton<IFilePickerService, FilePickerService>();
        builder.Services.AddSingleton<IToastService, ToastService>();

        var errorLog = new ErrorLogService();
        builder.Services.AddSingleton<IErrorLogService>(errorLog);

        builder.Services.AddSingleton<IAiProvider, OllamaProvider>();
        builder.Services.AddSingleton<IAiProvider, AnthropicProvider>();
        builder.Services.AddSingleton<IAiProvider, OpenAiProvider>();
#if WINDOWS
        // Direct GGUF inference — no Ollama required. Only available on Windows desktop.
        // Guard: LLamaSharp.Backend.Cpu requires AVX2. Without it the native DLL can crash
        // the process with 0xC0000005 (access violation) on first use. Silently skip
        // registration on CPUs that lack AVX2 rather than risking a hard crash.
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported)
            builder.Services.AddSingleton<IAiProvider, VirtmaAi.Services.AI.Providers.LlamaSharpProvider>();
        else
            Log.Warning("CPU does not support AVX2 — LLamaSharp (local GGUF) provider disabled");
#endif
        builder.Services.AddSingleton<IProviderRouter, ProviderRouter>();

        builder.Services.AddSingleton<ChatViewModel>();
        builder.Services.AddTransient<ChatPage>();

        builder.Services.AddSingleton<IAiRulesService, AiRulesService>();
        builder.Services.AddTransient<AiRulesViewModel>();
        builder.Services.AddTransient<AiRulesPage>();

        builder.Services.AddSingleton<IOllamaRegistryClient, OllamaRegistryClient>();
        builder.Services.AddSingleton<ILocalServiceProber, LocalServiceProber>();
        builder.Services.AddSingleton<IModelDownloadService, ModelDownloadService>();
        builder.Services.AddSingleton<IHardwareProbe, HardwareProbe>();
        builder.Services.AddSingleton<INetworkInterfaceService, NetworkInterfaceService>();
        builder.Services.AddSingleton<IResourceSampler, ResourceSampler>();

        builder.Services.AddTransient<ModelLibraryViewModel>();
        builder.Services.AddTransient<ApiKeysViewModel>();
        builder.Services.AddTransient<NetworkViewModel>();
        builder.Services.AddTransient<ResourceMonitorViewModel>();
        builder.Services.AddTransient<LogsViewModel>();
        builder.Services.AddTransient<ModelLibraryPage>();
        builder.Services.AddTransient<ApiKeysPage>();
        builder.Services.AddTransient<NetworkPage>();
        builder.Services.AddTransient<ResourceMonitorPage>();
        builder.Services.AddTransient<LogsPage>();

        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddTransient<ThemesViewModel>();
        builder.Services.AddTransient<ThemesPage>();

        builder.Services.AddSingleton<ISkillRegistry, SkillRegistry>();
        builder.Services.AddSingleton<ISkillMatcher, SkillMatcher>();
        builder.Services.AddTransient<SkillsViewModel>();
        builder.Services.AddTransient<SkillsListPage>();

        builder.Services.AddSingleton<ILiveNotetaker, LiveNotetaker>();

        builder.Services.AddSingleton<IBuiltInPlugin, DesktopCommanderPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, ProjectFilesPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, AppSelfModifyPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, MediaPlayerPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, HttpCallerPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, DesktopControlPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, LiveNotetakerPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, AutomatedWebPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, ScriptRunnerPlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, AudioTranscribePlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, VideoAnalyzePlugin>();
        builder.Services.AddSingleton<IBuiltInPlugin, WebSearchPlugin>();
        builder.Services.AddSingleton<IPluginHost, PluginHost>();
        builder.Services.AddTransient<PluginsViewModel>();
        builder.Services.AddTransient<PluginsListPage>();

        builder.Services.AddSingleton<IPreviewDispatcher, PreviewDispatcher>();
        builder.Services.AddSingleton<PreviewViewModel>();

        builder.Services.AddSingleton<IGraphifyRuntime, GraphifyRuntime>();
        builder.Services.AddSingleton<IGraphifyService, GraphifyService>();
        builder.Services.AddTransient<GraphifyViewModel>();
        builder.Services.AddTransient<GraphViewerPage>();
        builder.Services.AddTransient<ConversationGraphViewModel>();
        builder.Services.AddTransient<ConversationGraphPage>();

        builder.Services.AddSingleton<IRoutineScheduler, RoutineScheduler>();
        builder.Services.AddTransient<RoutinesViewModel>();
        builder.Services.AddTransient<RoutinesPage>();

        builder.Services.AddSingleton<IDbManager, DbManager>();
        builder.Services.AddTransient<DbManagerViewModel>();
        builder.Services.AddTransient<DbManagerPage>();

        builder.Services.AddSingleton<IIntegrationService, IntegrationService>();
        builder.Services.AddSingleton<IOAuthFlow, OAuthFlow>();
        builder.Services.AddTransient<IntegrationsViewModel>();
        builder.Services.AddTransient<IntegrationsPage>();

        builder.Services.AddSingleton<IReferenceService, ReferenceService>();
        builder.Services.AddTransient<ReferencesViewModel>();
        builder.Services.AddTransient<ReferencesPage>();

        builder.Services.AddSingleton<IExternalApiKeyService, ExternalApiKeyService>();
        builder.Services.AddSingleton<IExternalApiHost, ExternalApiHost>();
        builder.Services.AddTransient<ExternalApiKeysViewModel>();
        builder.Services.AddTransient<ExternalApiKeysPage>();

        builder.Services.AddSingleton<IInlineMediaDetector, InlineMediaDetector>();

        builder.Services.AddSingleton<ICrashReporter, CrashReporter>();

        builder.Services.AddSingleton<ITrainingService, TrainingService>();
        builder.Services.AddTransient<ModelCreatorViewModel>();
        builder.Services.AddTransient<ModelCreatorPage>();

        builder.Services.AddHttpClient();

        ConfigureLogging(builder, errorLog);

        return builder.Build();
    }

    private static void ConfigureLogging(MauiAppBuilder builder, IErrorLogService errorLog)
    {
        var logDir = Path.Combine(FileSystem.AppDataDirectory, "logs");
        Directory.CreateDirectory(logDir);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "virtmaai-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.Sink(new ErrorLogSink(errorLog))
            .CreateLogger();

        Log.Logger = logger;

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);
#if DEBUG
        builder.Logging.AddDebug();
#endif
    }
}
