using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace LaciSynchroni.WebAPI.SignalR.Utils;

public class ForeverRetryPolicy(SyncMediator mediator, int serverIndex, string serverName) : IRetryPolicy
{
    private static readonly Random _sharedRandom = new();
    private bool _sentDisconnected = false;

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // Use shared Random instance with jitter for better reconnection behavior
        int jitterMs;
        lock (_sharedRandom)
        {
            jitterMs = _sharedRandom.Next(0, 5000); // 0-5 seconds jitter
        }

        TimeSpan baseDelay;
        if (retryContext.PreviousRetryCount == 0)
        {
            _sentDisconnected = false;
            baseDelay = TimeSpan.FromSeconds(3);
        }
        else if (retryContext.PreviousRetryCount == 1)
        {
            baseDelay = TimeSpan.FromSeconds(5);
        }
        else if (retryContext.PreviousRetryCount == 2)
        {
            baseDelay = TimeSpan.FromSeconds(10);
        }
        else
        {
            if (!_sentDisconnected)
            {
                mediator.Publish(new NotificationMessage("Connection lost", $"Connection lost to service {serverName}", NotificationType.Warning, TimeSpan.FromSeconds(10)));
                mediator.Publish(new DisconnectedMessage(serverIndex));
            }
            _sentDisconnected = true;
            baseDelay = TimeSpan.FromSeconds(15); // Base delay for subsequent retries
        }

        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }
}
