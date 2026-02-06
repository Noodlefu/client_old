using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.WebAPI.SignalR;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LaciSynchroni.WebAPI
{
    using ServerIndex = int;

    public class MultiConnectTokenService(
        HttpClient httpClient,
        SyncMediator syncMediator,
        DalamudUtilService dalamudUtilService,
        ILoggerFactory loggerFactory,
        ServerConfigurationManager serverConfigurationManager)
    {
        private readonly ConcurrentDictionary<ServerIndex, ServerHubTokenProvider> _tokenProviders = new();

        public Task<string?> GetCachedToken(ServerIndex serverIndex)
        {
            return GetTokenProvider(serverIndex).GetToken();
        }

        public Task<string?> GetOrUpdateToken(ServerIndex serverIndex, CancellationToken ct)
        {
            return GetTokenProvider(serverIndex).GetOrUpdateToken(ct);
        }

        public Task<bool> TryUpdateOAuth2LoginTokenAsync(ServerIndex serverIndex, ServerStorage currentServer, bool forced = false)
        {
            return GetTokenProvider(serverIndex).TryUpdateOAuth2LoginTokenAsync(currentServer, forced);
        }

        private ServerHubTokenProvider GetTokenProvider(ServerIndex serverIndex)
        {
            return _tokenProviders.GetOrAdd(serverIndex, BuildNewTokenProvider);
        }

        private ServerHubTokenProvider BuildNewTokenProvider(ServerIndex serverIndex)
        {
            return new ServerHubTokenProvider(
                loggerFactory.CreateLogger<ServerHubTokenProvider>(),
                serverIndex,
                serverConfigurationManager,
                dalamudUtilService,
                syncMediator,
                httpClient
            );
        }
    }
}
