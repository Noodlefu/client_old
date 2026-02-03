using Dalamud.Utility;
using LaciSynchroni.Services.ServerConfiguration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LaciSynchroni.Services
{
    using PlayerNameHash = string;
    using ServerIndex = int;

    public class ConcurrentPairLockService(ServerConfigurationManager serverConfigurationManager, ILogger<ConcurrentPairLockService> logger)
    {
        private readonly ConcurrentDictionary<PlayerNameHash, LockData> _renderLocks = new(StringComparer.Ordinal);
        private readonly Lock _resourceLock = new();

        public int GetRenderLock(PlayerNameHash? playerNameHash, ServerIndex? serverIndex, string? characterName)
        {
            if (serverIndex is null || playerNameHash.IsNullOrWhitespace()) return -1;

            lock (_resourceLock)
            {
                var newLock = new LockData(characterName ?? "", playerNameHash, serverIndex.Value);
                // Check priority system
                var existingLock = _renderLocks.GetOrAdd(playerNameHash, newLock);
                if (existingLock.Index == newLock.Index)
                {
                    // No need to evaluate priorities -> server already has lock
                    return existingLock.Index;
                }

                var existingPriority = serverConfigurationManager.GetServerPriorityByIndex(existingLock.Index);
                var newPriority = serverConfigurationManager.GetServerPriorityByIndex(newLock.Index);

                if (newPriority > existingPriority)
                {
                    // The new server has higher priority, transfer the lock
                    logger.LogDebug(
                        "Transferring render lock for {CharacterName} ({PlayerHash}) from server {OldServer} (priority {OldPriority}) to server {NewServer} (priority {NewPriority})",
                        characterName,
                        playerNameHash,
                        serverConfigurationManager.GetServerNameByIndex(existingLock.Index),
                        existingPriority,
                        serverConfigurationManager.GetServerNameByIndex(newLock.Index),
                        newPriority);
                    _renderLocks[playerNameHash] = newLock;
                    return newLock.Index;
                }

                logger.LogDebug(
                    "Denying render lock for {CharacterName} ({PlayerHash}) to server {RequestingServer} (priority {RequestingPriority}): server {CurrentServer} (priority {CurrentPriority}) has priority",
                    characterName,
                    playerNameHash,
                    serverConfigurationManager.GetServerNameByIndex(newLock.Index),
                    newPriority,
                    serverConfigurationManager.GetServerNameByIndex(existingLock.Index),
                    existingPriority);

                return existingLock.Index;
            }
        }

        public bool HasRenderLock(PlayerNameHash? playerNameHash, ServerIndex serverIndex)
        {
            if (playerNameHash.IsNullOrWhitespace())
            {
                return true;
            }

            lock (_resourceLock)
            {
                var existingLock = _renderLocks.GetValueOrDefault(playerNameHash, null);
                if (existingLock == null)
                {
                    return true;
                }

                // If the lock is held by the same server, check is straightforward
                if (existingLock.Index == serverIndex)
                {
                    return true;
                }

                // If the lock is held by a different server, check priority
                var existingPriority = serverConfigurationManager.GetServerPriorityByIndex(existingLock.Index);
                var requestingPriority = serverConfigurationManager.GetServerPriorityByIndex(serverIndex);

                // Only has lock if requesting server has higher priority
                return requestingPriority > existingPriority;
            }
        }

        public bool ReleaseRenderLock(PlayerNameHash? playerNameHash, ServerIndex? serverIndex)
        {
            if (serverIndex is null || playerNameHash.IsNullOrWhitespace()) return false;

            lock (_resourceLock)
            {
                ServerIndex existingServerIndex = _renderLocks.GetValueOrDefault(playerNameHash)?.Index ?? -1;
                return (serverIndex == existingServerIndex) && _renderLocks.Remove(playerNameHash, out _);
            }
        }

        public ICollection<LockData> GetCurrentRenderLocks()
        {
            return _renderLocks.Values;
        }

        public record LockData(string CharName, PlayerNameHash PlayerHash, ServerIndex Index);
    }
}
