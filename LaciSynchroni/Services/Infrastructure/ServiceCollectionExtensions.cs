using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LaciSynchroni.FileCache;
using LaciSynchroni.Interop;
using LaciSynchroni.Interop.Ipc;
using LaciSynchroni.PlayerData.Factories;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.PlayerData.Services;
using LaciSynchroni.Services.CharaData;
using LaciSynchroni.Services.Events;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Configurations;
using LaciSynchroni.UI;
using LaciSynchroni.UI.Components;
using LaciSynchroni.UI.Components.Popup;
using LaciSynchroni.UI.Handlers;
using LaciSynchroni.WebAPI;
using LaciSynchroni.WebAPI.Files;
using LaciSynchroni.WebAPI.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;

namespace LaciSynchroni.Services.Infrastructure;

/// <summary>
/// Extension methods for organizing service registration in a modular way.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds core LaciSynchroni services (mediator, factories, managers).
    /// </summary>
    public static IServiceCollection AddLaciSynchroniCore(
        this IServiceCollection services,
        IDalamudPluginInterface pluginInterface,
        IPluginLog pluginLog)
    {
        // Core infrastructure
        services.AddSingleton(new WindowSystem(pluginInterface.InternalName));
        services.AddSingleton<FileDialogManager>();
        services.AddSingleton(new Dalamud.Localization(pluginInterface.InternalName + ".Localization.", "", useEmbedded: true));
        services.AddSingleton<BackgroundTaskTracker>();

        // Core services
        services.AddSingleton<SyncMediator>();
        services.AddSingleton<PerformanceCollectorService>();
        services.AddSingleton<LaciPlugin>();

        // Factories
        services.AddSingleton<GameObjectHandlerFactory>();
        services.AddSingleton<FileDownloadManagerFactory>();
        services.AddSingleton<PairHandlerFactory>();
        services.AddSingleton<PairFactory>();

        // HttpClient setup
        services.AddSingleton(s =>
        {
            var httpClient = new HttpClient();
            var ver = Assembly.GetExecutingAssembly().GetName().Version!;
            var versionString = string.Create(CultureInfo.InvariantCulture, $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}");
            var config = s.GetRequiredService<SyncConfigService>();
            if (config.Current.DebugExtendedUploadTimeout)
            {
                httpClient.Timeout = new TimeSpan(0, 10, 0);
                pluginLog.Warning("Extended upload timeout set!");
            }
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(pluginInterface.InternalName, versionString));
            return httpClient;
        });

        return services;
    }

    /// <summary>
    /// Adds file cache related services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniFileCache(this IServiceCollection services)
    {
        services.AddSingleton<FileCacheManager>();
        services.AddSingleton<FileCompactor>();
        services.AddSingleton<TransientResourceManager>();
        services.AddSingleton<FileUploadManager>();
        services.AddSingleton<FileTransferOrchestrator>();
        services.AddScoped<CacheMonitor>();

        return services;
    }

    /// <summary>
    /// Adds configuration services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniConfiguration(
        this IServiceCollection services,
        string configDirectory)
    {
        services.AddSingleton(s => new SyncConfigService(configDirectory));
        services.AddSingleton(s => new ServerConfigService(configDirectory));
        services.AddSingleton(s => new NotesConfigService(configDirectory));
        services.AddSingleton(s => new ServerTagConfigService(configDirectory));
        services.AddSingleton(s => new TransientConfigService(configDirectory));
        services.AddSingleton(s => new XivDataStorageService(configDirectory));
        services.AddSingleton(s => new PlayerPerformanceConfigService(configDirectory));
        services.AddSingleton(s => new CharaDataConfigService(configDirectory));

        // Register as IConfigService for iteration
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<SyncConfigService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<ServerConfigService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<NotesConfigService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<ServerTagConfigService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<TransientConfigService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<XivDataStorageService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<PlayerPerformanceConfigService>());
        services.AddSingleton<IConfigService<ISyncConfiguration>>(s => s.GetRequiredService<CharaDataConfigService>());

        services.AddSingleton<ConfigurationMigrator>();
        services.AddSingleton<ConfigurationSaveService>();
        services.AddSingleton<ServerConfigurationManager>();

        return services;
    }

    /// <summary>
    /// Adds IPC (inter-plugin communication) services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniIpc(
        this IServiceCollection services,
        IDalamudPluginInterface pluginInterface,
        IGameInteropProvider gameInteropProvider)
    {
        services.AddSingleton<RedrawManager>();

        services.AddSingleton(s => new IpcCallerPenumbra(
            s.GetRequiredService<ILogger<IpcCallerPenumbra>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<RedrawManager>()));

        services.AddSingleton(s => new IpcCallerGlamourer(
            s.GetRequiredService<ILogger<IpcCallerGlamourer>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<RedrawManager>()));

        services.AddSingleton(s => new IpcCallerCustomize(
            s.GetRequiredService<ILogger<IpcCallerCustomize>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new IpcCallerHeels(
            s.GetRequiredService<ILogger<IpcCallerHeels>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new IpcCallerHonorific(
            s.GetRequiredService<ILogger<IpcCallerHonorific>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new IpcCallerMoodles(
            s.GetRequiredService<ILogger<IpcCallerMoodles>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new IpcCallerPetNames(
            s.GetRequiredService<ILogger<IpcCallerPetNames>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new IpcCallerBrio(
            s.GetRequiredService<ILogger<IpcCallerBrio>>(),
            pluginInterface,
            s.GetRequiredService<DalamudUtilService>()));

        services.AddSingleton(s => new IpcManager(
            s.GetRequiredService<ILogger<IpcManager>>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<IpcCallerPenumbra>(),
            s.GetRequiredService<IpcCallerGlamourer>(),
            s.GetRequiredService<IpcCallerCustomize>(),
            s.GetRequiredService<IpcCallerHeels>(),
            s.GetRequiredService<IpcCallerHonorific>(),
            s.GetRequiredService<IpcCallerMoodles>(),
            s.GetRequiredService<IpcCallerPetNames>(),
            s.GetRequiredService<IpcCallerBrio>()));

        services.AddSingleton(s => new IpcProvider(
            s.GetRequiredService<ILogger<IpcProvider>>(),
            pluginInterface,
            s.GetRequiredService<CharaDataManager>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new VfxSpawnManager(
            s.GetRequiredService<ILogger<VfxSpawnManager>>(),
            gameInteropProvider,
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton(s => new BlockedCharacterHandler(
            s.GetRequiredService<ILogger<BlockedCharacterHandler>>(),
            gameInteropProvider));

        return services;
    }

    /// <summary>
    /// Adds character data management services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniCharaData(this IServiceCollection services)
    {
        services.AddSingleton<CharaDataManager>();
        services.AddSingleton<CharaDataFileHandler>();
        services.AddSingleton<CharaDataCharacterHandler>();
        services.AddSingleton<CharaDataNearbyManager>();
        services.AddSingleton<CharaDataGposeTogetherManager>();
        services.AddSingleton<XivDataAnalyzer>();
        services.AddSingleton<CharacterAnalyzer>();

        return services;
    }

    /// <summary>
    /// Adds player data and pair management services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniPlayerData(
        this IServiceCollection services,
        IContextMenu contextMenu)
    {
        services.AddSingleton<ConcurrentPairLockService>();
        services.AddSingleton(s => new PairManager(
            s.GetRequiredService<ILogger<PairManager>>(),
            s.GetRequiredService<PairFactory>(),
            s.GetRequiredService<SyncConfigService>(),
            s.GetRequiredService<SyncMediator>(),
            contextMenu,
            s.GetRequiredService<ServerConfigurationManager>(),
            s.GetRequiredService<ConcurrentPairLockService>()));

        services.AddSingleton<ProfileManager>();
        services.AddSingleton<MultiConnectTokenService>();
        services.AddSingleton<PlayerPerformanceService>();

        services.AddScoped<CacheCreationService>();
        services.AddScoped<PlayerDataFactory>();
        services.AddScoped<VisibleUserDataDistributor>();

        return services;
    }

    /// <summary>
    /// Adds UI services and windows.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniUI(
        this IServiceCollection services,
        IDalamudPluginInterface pluginInterface,
        ITextureProvider textureProvider)
    {
        // UI Helpers
        services.AddScoped<DrawEntityFactory>();
        services.AddScoped<UiFactory>();
        services.AddScoped<SelectTagForPairUi>();
        services.AddSingleton<SelectPairForTagUi>();
        services.AddSingleton<TagHandler>();
        services.AddSingleton<IdDisplayHandler>();

        // Windows
        services.AddScoped<WindowMediatorSubscriberBase, SettingsUi>();
        services.AddScoped<WindowMediatorSubscriberBase, LaciServerTesterUi>(s => new LaciServerTesterUi(
            s.GetRequiredService<DalamudUtilService>(),
            pluginInterface,
            s.GetRequiredService<ILogger<LaciServerTesterUi>>(),
            s.GetRequiredService<PerformanceCollectorService>(),
            s.GetRequiredService<UiSharedService>(),
            s.GetRequiredService<HttpClient>(),
            s.GetRequiredService<SyncMediator>()
        ));
        services.AddScoped<WindowMediatorSubscriberBase, CompactUi>();
        services.AddScoped<WindowMediatorSubscriberBase, IntroUi>();
        services.AddScoped<WindowMediatorSubscriberBase, DownloadUi>();
        services.AddScoped<WindowMediatorSubscriberBase, PopoutProfileUi>();
        services.AddScoped<WindowMediatorSubscriberBase, DataAnalysisUi>();
        services.AddScoped<WindowMediatorSubscriberBase, JoinSyncshellUI>();
        services.AddScoped<WindowMediatorSubscriberBase, CreateSyncshellUI>();
        services.AddScoped<WindowMediatorSubscriberBase, ServerJoinConfirmationUI>(s => new ServerJoinConfirmationUI(
            s.GetRequiredService<ILogger<ServerJoinConfirmationUI>>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<ServerConfigurationManager>(),
            s.GetRequiredService<UiSharedService>(),
            s.GetRequiredService<DalamudUtilService>(),
            s.GetRequiredService<PerformanceCollectorService>(),
            s.GetRequiredService<HttpClient>()));
        services.AddScoped<WindowMediatorSubscriberBase, RulesUI>(s => new RulesUI(
            s.GetRequiredService<ILogger<RulesUI>>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<UiSharedService>(),
            s.GetRequiredService<PerformanceCollectorService>()));
        services.AddScoped<WindowMediatorSubscriberBase, EventViewerUI>();
        services.AddScoped<WindowMediatorSubscriberBase, CharaDataHubUi>();
        services.AddScoped<WindowMediatorSubscriberBase, EditProfileUi>(s => new EditProfileUi(
            s.GetRequiredService<ILogger<EditProfileUi>>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<ApiController>(),
            s.GetRequiredService<UiSharedService>(),
            s.GetRequiredService<FileDialogManager>(),
            s.GetRequiredService<ProfileManager>(),
            s.GetRequiredService<PerformanceCollectorService>(),
            s.GetRequiredService<ServerConfigurationManager>()));
        services.AddScoped<WindowMediatorSubscriberBase, PopupHandler>();

        // Popup handlers
        services.AddScoped<IPopupHandler, BanUserPopupHandler>();
        services.AddScoped<IPopupHandler, CensusPopupHandler>();

        // UI Service
        services.AddScoped(s => new UiService(
            s.GetRequiredService<ILogger<UiService>>(),
            pluginInterface.UiBuilder,
            s.GetRequiredService<SyncConfigService>(),
            s.GetRequiredService<WindowSystem>(),
            s.GetServices<WindowMediatorSubscriberBase>(),
            s.GetRequiredService<UiFactory>(),
            s.GetRequiredService<FileDialogManager>(),
            s.GetRequiredService<SyncMediator>()));

        // UiSharedService
        services.AddScoped(s => new UiSharedService(
            s.GetRequiredService<ILogger<UiSharedService>>(),
            s.GetRequiredService<IpcManager>(),
            s.GetRequiredService<ApiController>(),
            s.GetRequiredService<CacheMonitor>(),
            s.GetRequiredService<FileDialogManager>(),
            s.GetRequiredService<SyncConfigService>(),
            s.GetRequiredService<DalamudUtilService>(),
            pluginInterface,
            textureProvider,
            s.GetRequiredService<Dalamud.Localization>(),
            s.GetRequiredService<ServerConfigurationManager>(),
            s.GetRequiredService<MultiConnectTokenService>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<LocalHttpServer>()));

        return services;
    }

    /// <summary>
    /// Adds Dalamud integration services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniDalamud(
        this IServiceCollection services,
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        ICondition condition,
        IDataManager gameData,
        ITargetManager targetManager,
        IGameConfig gameConfig,
        IDtrBar dtrBar,
        INotificationManager notificationManager,
        IChatGui chatGui,
        ICommandManager commandManager)
    {
        services.AddSingleton(s => new DalamudUtilService(
            s.GetRequiredService<ILogger<DalamudUtilService>>(),
            pluginInterface, clientState, objectTable, framework, gameGui, condition, gameData, targetManager, gameConfig,
            s.GetRequiredService<BlockedCharacterHandler>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<PerformanceCollectorService>(),
            s.GetRequiredService<SyncConfigService>()));

        services.AddSingleton(s => new DtrEntry(
            s.GetRequiredService<ILogger<DtrEntry>>(),
            dtrBar,
            s.GetRequiredService<SyncConfigService>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<PairManager>(),
            s.GetRequiredService<ApiController>(),
            s.GetRequiredService<ServerConfigurationManager>()));

        services.AddSingleton(s => new NotificationService(
            s.GetRequiredService<ILogger<NotificationService>>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<DalamudUtilService>(),
            notificationManager, chatGui,
            s.GetRequiredService<SyncConfigService>()));

        services.AddScoped(s => new CommandManagerService(
            commandManager,
            s.GetRequiredService<PerformanceCollectorService>(),
            s.GetRequiredService<ServerConfigurationManager>(),
            s.GetRequiredService<CacheMonitor>(),
            s.GetRequiredService<ApiController>(),
            s.GetRequiredService<SyncMediator>(),
            s.GetRequiredService<SyncConfigService>()));

        services.AddSingleton(s => new LocalHttpServer(
            s.GetRequiredService<ILogger<LocalHttpServer>>(),
            s.GetRequiredService<ServerConfigurationManager>(),
            s.GetRequiredService<SyncMediator>()));

        services.AddSingleton<PluginWarningNotificationService>();

        return services;
    }

    /// <summary>
    /// Adds networking and API services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniNetworking(this IServiceCollection services)
    {
        services.AddSingleton<ApiController>();

        return services;
    }

    /// <summary>
    /// Adds event aggregation services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniEvents(
        this IServiceCollection services,
        string configDirectory)
    {
        services.AddSingleton(s => new EventAggregator(
            configDirectory,
            s.GetRequiredService<ILogger<EventAggregator>>(),
            s.GetRequiredService<SyncMediator>()));

        return services;
    }

    /// <summary>
    /// Registers all hosted services.
    /// </summary>
    public static IServiceCollection AddLaciSynchroniHostedServices(this IServiceCollection services)
    {
        services.AddHostedService(p => p.GetRequiredService<ConfigurationSaveService>());
        services.AddHostedService(p => p.GetRequiredService<SyncMediator>());
        services.AddHostedService(p => p.GetRequiredService<NotificationService>());
        services.AddHostedService(p => p.GetRequiredService<FileCacheManager>());
        services.AddHostedService(p => p.GetRequiredService<ConfigurationMigrator>());
        services.AddHostedService(p => p.GetRequiredService<DalamudUtilService>());
        services.AddHostedService(p => p.GetRequiredService<PerformanceCollectorService>());
        services.AddHostedService(p => p.GetRequiredService<DtrEntry>());
        services.AddHostedService(p => p.GetRequiredService<EventAggregator>());
        services.AddHostedService(p => p.GetRequiredService<IpcProvider>());
        services.AddHostedService(p => p.GetRequiredService<LaciPlugin>());

        return services;
    }
}
