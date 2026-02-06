using LaciSynchroni.Common.Dto.Group;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.UI;
using LaciSynchroni.UI.Components.Popup;
using LaciSynchroni.WebAPI;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Services;

public class UiFactory(ILoggerFactory loggerFactory, SyncMediator syncMediator, ApiController apiController,
    UiSharedService uiSharedService, PairManager pairManager, ServerConfigurationManager serverConfigManager,
    ProfileManager profileManager, PerformanceCollectorService performanceCollectorService)
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SyncMediator _syncMediator = syncMediator;
    private readonly ApiController _apiController = apiController;
    private readonly UiSharedService _uiSharedService = uiSharedService;
    private readonly PairManager _pairManager = pairManager;
    private readonly ServerConfigurationManager _serverConfigManager = serverConfigManager;
    private readonly ProfileManager _profileManager = profileManager;
    private readonly PerformanceCollectorService _performanceCollectorService = performanceCollectorService;

    public SyncshellAdminUI CreateSyncshellAdminUi(GroupFullInfoDto dto, int serverIndex)
    {
        return new SyncshellAdminUI(_loggerFactory.CreateLogger<SyncshellAdminUI>(), _syncMediator,
            _apiController, _uiSharedService, _pairManager, dto, _performanceCollectorService, serverIndex);
    }

    public StandaloneProfileUi CreateStandaloneProfileUi(Pair pair)
    {
        return new StandaloneProfileUi(_loggerFactory.CreateLogger<StandaloneProfileUi>(), _syncMediator,
            _uiSharedService, _serverConfigManager, _profileManager, _pairManager, pair, _performanceCollectorService);
    }

    public PermissionWindowUI CreatePermissionPopupUi(Pair pair)
    {
        return new PermissionWindowUI(_loggerFactory.CreateLogger<PermissionWindowUI>(), pair,
            _syncMediator, _uiSharedService, _apiController, _performanceCollectorService);
    }
}
