using LaciSynchroni.FileCache;
using LaciSynchroni.Interop.Ipc;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.WebAPI.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.PlayerData.Factories;

public class PairHandlerFactory(ILoggerFactory loggerFactory, GameObjectHandlerFactory gameObjectHandlerFactory, IpcManager ipcManager,
    FileDownloadManagerFactory fileDownloadManagerFactory, DalamudUtilService dalamudUtilService,
    PluginWarningNotificationService pluginWarningNotificationManager, IHostApplicationLifetime hostApplicationLifetime,
    FileCacheManager fileCacheManager, SyncMediator syncMediator, PlayerPerformanceService playerPerformanceService,
    ServerConfigurationManager serverConfigManager, ConcurrentPairLockService concurrentPairLockService,
    FileTransferOrchestrator transferOrchestrator)
{
    private readonly DalamudUtilService _dalamudUtilService = dalamudUtilService;
    private readonly FileCacheManager _fileCacheManager = fileCacheManager;
    private readonly FileDownloadManagerFactory _fileDownloadManagerFactory = fileDownloadManagerFactory;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory = gameObjectHandlerFactory;
    private readonly IHostApplicationLifetime _hostApplicationLifetime = hostApplicationLifetime;
    private readonly IpcManager _ipcManager = ipcManager;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SyncMediator _syncMediator = syncMediator;
    private readonly PlayerPerformanceService _playerPerformanceService = playerPerformanceService;
    private readonly FileTransferOrchestrator _transferOrchestrator = transferOrchestrator;
    private readonly ServerConfigurationManager _serverConfigManager = serverConfigManager;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager = pluginWarningNotificationManager;
    private readonly ConcurrentPairLockService _concurrentPairLockService = concurrentPairLockService;

    public PairHandler Create(Pair pair)
    {
        return new PairHandler(_loggerFactory.CreateLogger<PairHandler>(), pair, _gameObjectHandlerFactory,
            _ipcManager, _fileDownloadManagerFactory.Create(), _pluginWarningNotificationManager, _dalamudUtilService, _hostApplicationLifetime,
            _fileCacheManager, _syncMediator, _playerPerformanceService, _serverConfigManager, _concurrentPairLockService, _transferOrchestrator);
    }
}
