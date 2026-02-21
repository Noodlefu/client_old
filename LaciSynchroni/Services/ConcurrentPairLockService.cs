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

        /// <summary>
        /// Attempts to acquire or confirm ownership of the render lock for the given player.
        /// Returns a <see cref="CancellationToken"/> that:
        /// <list type="bullet">
        ///   <item>Is live when the lock is granted to this server.</item>
        ///   <item>Gets cancelled if a higher-priority server later takes the lock or the lock is released.</item>
        ///   <item>Is pre-cancelled when the lock is denied because a higher-priority server already holds it.</item>
        /// </list>
        /// </summary>
        public CancellationToken AcquireRenderLock(PlayerNameHash? playerNameHash, ServerIndex? serverIndex, string? characterName)
        {
            if (serverIndex is null || playerNameHash.IsNullOrWhitespace())
                return new CancellationToken(canceled: true);

            CancellationTokenSource? toCancel = null;
            CancellationToken result;

            lock (_resourceLock)
            {
                if (_renderLocks.TryGetValue(playerNameHash, out var existingLock))
                {
                    if (existingLock.Index == serverIndex.Value)
                    {
                        // Already the owner — return the existing live token.
                        return existingLock.Cts.Token;
                    }

                    var existingPriority = serverConfigurationManager.GetServerPriorityByIndex(existingLock.Index);
                    var newPriority = serverConfigurationManager.GetServerPriorityByIndex(serverIndex.Value);

                    if (newPriority > existingPriority)
                    {
                        logger.LogDebug(
                            "Transferring render lock for {CharacterName} ({PlayerHash}) from server {OldServer} (priority {OldPriority}) to server {NewServer} (priority {NewPriority})",
                            characterName, playerNameHash,
                            serverConfigurationManager.GetServerNameByIndex(existingLock.Index), existingPriority,
                            serverConfigurationManager.GetServerNameByIndex(serverIndex.Value), newPriority);

                        toCancel = existingLock.Cts;
                        var newCts = new CancellationTokenSource();
                        _renderLocks[playerNameHash] = new LockData(characterName ?? string.Empty, playerNameHash, serverIndex.Value, newCts);
                        result = newCts.Token;
                    }
                    else
                    {
                        logger.LogDebug(
                            "Denying render lock for {CharacterName} ({PlayerHash}) to server {RequestingServer} (priority {RequestingPriority}): server {CurrentServer} (priority {CurrentPriority}) has priority",
                            characterName, playerNameHash,
                            serverConfigurationManager.GetServerNameByIndex(serverIndex.Value), newPriority,
                            serverConfigurationManager.GetServerNameByIndex(existingLock.Index), existingPriority);

                        result = new CancellationToken(canceled: true);
                    }
                }
                else
                {
                    var newCts = new CancellationTokenSource();
                    _renderLocks[playerNameHash] = new LockData(characterName ?? string.Empty, playerNameHash, serverIndex.Value, newCts);
                    result = newCts.Token;
                }
            }

            // Cancel and dispose outside the lock to avoid potential deadlocks with registered callbacks.
            toCancel?.Cancel();
            toCancel?.Dispose();

            return result;
        }

        /// <summary>
        /// Returns the server index that currently holds the render lock for the given player,
        /// or -1 if no lock is held. This is a read-only check and does not modify lock state.
        /// </summary>
        public int GetCurrentLockHolder(PlayerNameHash? playerNameHash)
        {
            if (playerNameHash.IsNullOrWhitespace()) return -1;

            lock (_resourceLock)
            {
                return _renderLocks.TryGetValue(playerNameHash, out var existing) ? existing.Index : -1;
            }
        }

        /// <summary>
        /// Releases the render lock if the given server currently holds it.
        /// The lock's cancellation token is cancelled, propagating to any in-progress download or apply.
        /// </summary>
        public bool ReleaseRenderLock(PlayerNameHash? playerNameHash, ServerIndex? serverIndex)
        {
            if (serverIndex is null || playerNameHash.IsNullOrWhitespace()) return false;

            LockData? removed = null;

            lock (_resourceLock)
            {
                if (!_renderLocks.TryGetValue(playerNameHash, out var existingLock) || existingLock.Index != serverIndex.Value)
                    return false;

                _renderLocks.Remove(playerNameHash, out removed);
            }

            // Cancel and dispose outside the lock.
            removed?.Cts.Cancel();
            removed?.Cts.Dispose();

            return removed != null;
        }

        public ICollection<LockData> GetCurrentRenderLocks()
        {
            return _renderLocks.Values;
        }

        public record LockData(string CharName, PlayerNameHash PlayerHash, ServerIndex Index, CancellationTokenSource Cts);
    }
}
