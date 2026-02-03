using LaciSynchroni.Services.Infrastructure;
using LaciSynchroni.SyncConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace LaciSynchroni.Services.Mediator;

public sealed class SyncMediator : IHostedService
{
    private readonly Lock _addRemoveLock = new();
    private readonly BackgroundTaskTracker _taskTracker;
    private readonly ConcurrentDictionary<object, DateTime> _lastErrorTime = [];
    private readonly ILogger<SyncMediator> _logger;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly Channel<MessageBase> _messageChannel = Channel.CreateUnbounded<MessageBase>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly SyncConfigService _syncConfigService;
    private readonly ConcurrentDictionary<Type, HashSet<SubscriberAction>> _subscriberDict = [];
    /// <summary>Cached immutable snapshots of subscribers per message type, invalidated on subscribe/unsubscribe.</summary>
    private readonly ConcurrentDictionary<Type, ImmutableList<SubscriberAction>> _subscriberCache = [];
    private bool _processQueue = false;
    private readonly ConcurrentDictionary<Type, MethodInfo?> _genericExecuteMethods = new();
    public SyncMediator(ILogger<SyncMediator> logger, PerformanceCollectorService performanceCollector, SyncConfigService syncConfigService,
        BackgroundTaskTracker taskTracker)
    {
        _logger = logger;
        _performanceCollector = performanceCollector;
        _syncConfigService = syncConfigService;
        _taskTracker = taskTracker;
    }

    public void PrintSubscriberInfo()
    {
        foreach (var subscriber in _subscriberDict.SelectMany(c => c.Value.Select(v => v.Subscriber))
            .DistinctBy(p => p).OrderBy(p => p.GetType().FullName, StringComparer.Ordinal).ToList())
        {
            _logger.LogInformation("Subscriber {type}: {sub}", subscriber.GetType().Name, subscriber.ToString());
            StringBuilder sb = new();
            sb.Append("=> ");
            foreach (var item in _subscriberDict.Where(item => item.Value.Any(v => v.Subscriber == subscriber)).ToList())
            {
                sb.Append(item.Key.Name).Append(", ");
            }

            if (!string.Equals(sb.ToString(), "=> ", StringComparison.Ordinal))
                _logger.LogInformation("{sb}", sb.ToString());
            _logger.LogInformation("---");
        }
    }

    public void Publish<T>(T message) where T : MessageBase
    {
        if (message.KeepThreadContext)
        {
            ExecuteMessage(message);
        }
        else
        {
            _messageChannel.Writer.TryWrite(message);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting {ClassName}", GetType().Name);

        _taskTracker.Run(async () =>
        {
            // Wait for queue processing to be enabled
            while (!_processQueue && !_loopCts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _loopCts.Token).ConfigureAwait(false);
            }

            // Process messages as they arrive using async enumeration (no polling!)
            await foreach (var message in _messageChannel.Reader.ReadAllAsync(_loopCts.Token).ConfigureAwait(false))
            {
                ExecuteMessage(message);
            }
        }, "SyncMediator.MessageLoop", _loopCts.Token);

        _logger.LogInformation("Started {ClassName}", GetType().Name);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _messageChannel.Writer.Complete();
        _loopCts.Cancel();
        _loopCts.Dispose();
        return Task.CompletedTask;
    }

    public void Subscribe<T>(IMediatorSubscriber subscriber, Action<T> action) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            _subscriberDict.TryAdd(typeof(T), []);

            if (!_subscriberDict[typeof(T)].Add(new(subscriber, action)))
            {
                throw new InvalidOperationException("Already subscribed");
            }

            // Invalidate cache for this message type
            _subscriberCache.TryRemove(typeof(T), out _);
        }
    }

    public void Unsubscribe<T>(IMediatorSubscriber subscriber) where T : MessageBase
    {
        lock (_addRemoveLock)
        {
            if (_subscriberDict.ContainsKey(typeof(T)))
            {
                _subscriberDict[typeof(T)].RemoveWhere(p => p.Subscriber == subscriber);

                // Invalidate cache for this message type
                _subscriberCache.TryRemove(typeof(T), out _);
            }
        }
    }

    internal void UnsubscribeAll(IMediatorSubscriber subscriber)
    {
        lock (_addRemoveLock)
        {
            foreach (Type kvp in _subscriberDict.Select(k => k.Key))
            {
                int unSubbed = _subscriberDict[kvp]?.RemoveWhere(p => p.Subscriber == subscriber) ?? 0;
                if (unSubbed > 0)
                {
                    _logger.LogDebug("{sub} unsubscribed from {msg}", subscriber.GetType().Name, kvp.Name);

                    // Invalidate cache for this message type
                    _subscriberCache.TryRemove(kvp, out _);
                }
            }
        }
    }

    private void ExecuteMessage(MessageBase message)
    {
        var msgType = message.GetType();

        // Try to get cached subscriber list, or build and cache it
        if (!_subscriberCache.TryGetValue(msgType, out var subscribersList))
        {
            if (!_subscriberDict.TryGetValue(msgType, out HashSet<SubscriberAction>? subscribers) || subscribers == null || subscribers.Count == 0)
                return;

            lock (_addRemoveLock)
            {
                // Double-check after acquiring lock
                if (!_subscriberCache.TryGetValue(msgType, out subscribersList))
                {
                    subscribersList = subscribers
                        .Where(s => s.Subscriber != null)
                        .OrderBy(k => k.Subscriber is IHighPriorityMediatorSubscriber ? 0 : 1)
                        .ToImmutableList();
                    _subscriberCache[msgType] = subscribersList;
                }
            }
        }

        if (subscribersList.Count == 0) return;

#pragma warning disable S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
        if (!_genericExecuteMethods.TryGetValue(msgType, out var methodInfo))
        {
            _genericExecuteMethods[msgType] = methodInfo = GetType()
                 .GetMethod(nameof(ExecuteReflected), BindingFlags.NonPublic | BindingFlags.Instance)?
                 .MakeGenericMethod(msgType);
        }

        methodInfo!.Invoke(this, [subscribersList, message]);
#pragma warning restore S3011 // Reflection should not be used to increase accessibility of classes, methods, or fields
    }

    private void ExecuteReflected<T>(ImmutableList<SubscriberAction> subscribers, T message) where T : MessageBase
    {
        foreach (SubscriberAction subscriber in subscribers)
        {
            try
            {
                if (_syncConfigService.Current.LogPerformance)
                {
                    var isSameThread = message.KeepThreadContext ? "$" : string.Empty;
                    _performanceCollector.LogPerformance(this, $"{isSameThread}Execute>{message.GetType().Name}+{subscriber.Subscriber.GetType().Name}>{subscriber.Subscriber}",
                        () => ((Action<T>)subscriber.Action).Invoke(message));
                }
                else
                {
                    ((Action<T>)subscriber.Action).Invoke(message);
                }
            }
            catch (Exception ex)
            {
                if (_lastErrorTime.TryGetValue(subscriber, out var lastErrorTime) && lastErrorTime.Add(TimeSpan.FromSeconds(10)) > DateTime.UtcNow)
                    continue;

                _logger.LogError(ex.InnerException ?? ex, "Error executing {type} for subscriber {subscriber}",
                    message.GetType().Name, subscriber.Subscriber.GetType().Name);
                _lastErrorTime[subscriber] = DateTime.UtcNow;
            }
        }
    }

    public void StartQueueProcessing()
    {
        _logger.LogInformation("Starting Message Queue Processing");
        _processQueue = true;
    }

    private sealed class SubscriberAction
    {
        public SubscriberAction(IMediatorSubscriber subscriber, object action)
        {
            Subscriber = subscriber;
            Action = action;
        }

        public object Action { get; }
        public IMediatorSubscriber Subscriber { get; }
    }
}
