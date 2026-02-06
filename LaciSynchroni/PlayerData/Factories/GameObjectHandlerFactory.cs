using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.PlayerData.Factories;

public class GameObjectHandlerFactory(ILoggerFactory loggerFactory, PerformanceCollectorService performanceCollectorService, SyncMediator syncMediator,
    DalamudUtilService dalamudUtilService)
{
    private readonly DalamudUtilService _dalamudUtilService = dalamudUtilService;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SyncMediator _syncMediator = syncMediator;
    private readonly PerformanceCollectorService _performanceCollectorService = performanceCollectorService;

    public async Task<GameObjectHandler> Create(ObjectKind objectKind, Func<nint> getAddressFunc, bool isWatched = false)
    {
        return await _dalamudUtilService.RunOnFrameworkThread(() => new GameObjectHandler(_loggerFactory.CreateLogger<GameObjectHandler>(),
            _performanceCollectorService, _syncMediator, _dalamudUtilService, objectKind, getAddressFunc, isWatched)).ConfigureAwait(false);
    }
}
