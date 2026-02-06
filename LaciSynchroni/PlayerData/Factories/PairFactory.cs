using LaciSynchroni.Common.Dto.User;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.PlayerData.Factories;

public class PairFactory(ILoggerFactory loggerFactory, PairHandlerFactory cachedPlayerFactory,
    SyncMediator syncMediator, ServerConfigurationManager serverConfigurationManager)
{
    private readonly PairHandlerFactory _cachedPlayerFactory = cachedPlayerFactory;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SyncMediator _syncMediator = syncMediator;
    private readonly ServerConfigurationManager _serverConfigurationManager = serverConfigurationManager;

    public Pair Create(UserFullPairDto userPairDto, int serverIndex)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), userPairDto, _cachedPlayerFactory, _syncMediator, _serverConfigurationManager, serverIndex);
    }

    public Pair Create(UserPairDto userPairDto, int serverIndex)
    {
        return new Pair(_loggerFactory.CreateLogger<Pair>(), new(userPairDto.User, userPairDto.IndividualPairStatus, [], userPairDto.OwnPermissions, userPairDto.OtherPermissions),
            _cachedPlayerFactory, _syncMediator, _serverConfigurationManager, serverIndex);
    }
}
