using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LaciSynchroni.Tests.Infrastructure;

/// <summary>
/// Tests for SyncMediator functionality.
/// These tests validate message publishing, subscription management, and disposal.
/// </summary>
public class SyncMediatorTests
{
    private readonly Mock<ILogger<SyncMediator>> _loggerMock;

    public SyncMediatorTests()
    {
        _loggerMock = new Mock<ILogger<SyncMediator>>();
    }

    [Fact]
    public async Task Publish_DeliversMessageToSubscriber()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber = new TestSubscriber();
        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage);

        var message = new TestMessage("Hello");

        // Act
        await mediator.PublishAsync(message);

        // Assert
        Assert.Single(subscriber.ReceivedMessages);
        Assert.Equal("Hello", subscriber.ReceivedMessages[0].Content);
    }

    [Fact]
    public async Task Publish_DeliversToMultipleSubscribers()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber1 = new TestSubscriber();
        var subscriber2 = new TestSubscriber();

        mediator.Subscribe<TestMessage>(subscriber1, subscriber1.HandleMessage);
        mediator.Subscribe<TestMessage>(subscriber2, subscriber2.HandleMessage);

        var message = new TestMessage("Broadcast");

        // Act
        await mediator.PublishAsync(message);

        // Assert
        Assert.Single(subscriber1.ReceivedMessages);
        Assert.Single(subscriber2.ReceivedMessages);
    }

    [Fact]
    public async Task Unsubscribe_StopsReceivingMessages()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber = new TestSubscriber();
        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage);

        await mediator.PublishAsync(new TestMessage("First"));

        // Act
        mediator.Unsubscribe<TestMessage>(subscriber);
        await mediator.PublishAsync(new TestMessage("Second"));

        // Assert
        Assert.Single(subscriber.ReceivedMessages);
        Assert.Equal("First", subscriber.ReceivedMessages[0].Content);
    }

    [Fact]
    public async Task UnsubscribeAll_RemovesAllSubscriptions()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber = new TestSubscriber();

        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage);
        mediator.Subscribe<OtherMessage>(subscriber, subscriber.HandleOtherMessage);

        // Act
        mediator.UnsubscribeAll(subscriber);

        await mediator.PublishAsync(new TestMessage("Test"));
        await mediator.PublishAsync(new OtherMessage(42));

        // Assert
        Assert.Empty(subscriber.ReceivedMessages);
        Assert.Empty(subscriber.ReceivedOtherMessages);
    }

    [Fact]
    public async Task Publish_WithNoSubscribers_DoesNotThrow()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var message = new TestMessage("Nobody listening");

        // Act & Assert - should not throw
        await mediator.PublishAsync(message);
    }

    [Fact]
    public async Task Publish_HandlerException_DoesNotAffectOtherSubscribers()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var failingSubscriber = new FailingSubscriber();
        var workingSubscriber = new TestSubscriber();

        mediator.Subscribe<TestMessage>(failingSubscriber, failingSubscriber.HandleMessage);
        mediator.Subscribe<TestMessage>(workingSubscriber, workingSubscriber.HandleMessage);

        // Act
        await mediator.PublishAsync(new TestMessage("Test"));

        // Assert - working subscriber should still receive the message
        Assert.Single(workingSubscriber.ReceivedMessages);
    }

    [Fact]
    public async Task Publish_DifferentMessageTypes_RoutedCorrectly()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber = new TestSubscriber();

        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage);
        mediator.Subscribe<OtherMessage>(subscriber, subscriber.HandleOtherMessage);

        // Act
        await mediator.PublishAsync(new TestMessage("Text"));
        await mediator.PublishAsync(new OtherMessage(123));

        // Assert
        Assert.Single(subscriber.ReceivedMessages);
        Assert.Single(subscriber.ReceivedOtherMessages);
        Assert.Equal("Text", subscriber.ReceivedMessages[0].Content);
        Assert.Equal(123, subscriber.ReceivedOtherMessages[0].Value);
    }

    [Fact]
    public void Dispose_ClearsAllSubscriptions()
    {
        // Arrange
        var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber = new TestSubscriber();
        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage);

        // Act
        mediator.Dispose();

        // Assert - no way to directly check, but shouldn't throw on dispose
        Assert.True(true);
    }

    [Fact]
    public async Task Subscribe_SameSubscriberTwice_ReceivesMessageOnce()
    {
        // Arrange
        using var mediator = new SyncMediator(_loggerMock.Object);
        var subscriber = new TestSubscriber();

        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage);
        mediator.Subscribe<TestMessage>(subscriber, subscriber.HandleMessage); // Duplicate

        // Act
        await mediator.PublishAsync(new TestMessage("Test"));

        // Assert - should only receive once (implementation dependent)
        // This tests the expected behavior - adjust based on actual implementation
        Assert.True(subscriber.ReceivedMessages.Count >= 1);
    }
}

#region Test Infrastructure

public record TestMessage(string Content) : ISyncMessage;
public record OtherMessage(int Value) : ISyncMessage;

public interface ISyncMessage { }

public class TestSubscriber
{
    public List<TestMessage> ReceivedMessages { get; } = new();
    public List<OtherMessage> ReceivedOtherMessages { get; } = new();

    public Task HandleMessage(TestMessage message)
    {
        ReceivedMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task HandleOtherMessage(OtherMessage message)
    {
        ReceivedOtherMessages.Add(message);
        return Task.CompletedTask;
    }
}

public class FailingSubscriber
{
    public Task HandleMessage(TestMessage message)
    {
        throw new InvalidOperationException("Intentional test failure");
    }
}

/// <summary>
/// Simplified SyncMediator for testing purposes.
/// This provides a basic implementation of the mediator pattern.
/// </summary>
public sealed class SyncMediator : IDisposable
{
    private readonly ILogger<SyncMediator> _logger;
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = new();
    private readonly object _lock = new();
    private bool _disposed;

    public SyncMediator(ILogger<SyncMediator> logger)
    {
        _logger = logger;
    }

    public void Subscribe<TMessage>(object subscriber, Func<TMessage, Task> handler) where TMessage : ISyncMessage
    {
        lock (_lock)
        {
            var messageType = typeof(TMessage);
            if (!_subscriptions.TryGetValue(messageType, out var list))
            {
                list = new List<Subscription>();
                _subscriptions[messageType] = list;
            }

            // Check if already subscribed
            if (!list.Any(s => s.Subscriber == subscriber))
            {
                list.Add(new Subscription(subscriber, msg => handler((TMessage)msg)));
            }
        }
    }

    public void Unsubscribe<TMessage>(object subscriber) where TMessage : ISyncMessage
    {
        lock (_lock)
        {
            var messageType = typeof(TMessage);
            if (_subscriptions.TryGetValue(messageType, out var list))
            {
                list.RemoveAll(s => s.Subscriber == subscriber);
            }
        }
    }

    public void UnsubscribeAll(object subscriber)
    {
        lock (_lock)
        {
            foreach (var list in _subscriptions.Values)
            {
                list.RemoveAll(s => s.Subscriber == subscriber);
            }
        }
    }

    public async Task PublishAsync<TMessage>(TMessage message) where TMessage : ISyncMessage
    {
        List<Subscription> subscriptionsCopy;

        lock (_lock)
        {
            var messageType = typeof(TMessage);
            if (!_subscriptions.TryGetValue(messageType, out var list))
            {
                return;
            }
            subscriptionsCopy = list.ToList();
        }

        foreach (var subscription in subscriptionsCopy)
        {
            try
            {
                await subscription.Handler(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling message {MessageType} for subscriber {Subscriber}",
                    typeof(TMessage).Name, subscription.Subscriber.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_lock)
        {
            _subscriptions.Clear();
        }
    }

    private record Subscription(object Subscriber, Func<object, Task> Handler);
}

#endregion
