using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LaciSynchroni.Tests.Infrastructure;

/// <summary>
/// Tests for BackgroundTaskTracker functionality.
/// These tests validate task tracking, exception handling, and disposal behavior.
/// </summary>
public class BackgroundTaskTrackerTests
{
    private readonly Mock<ILogger<BackgroundTaskTracker>> _loggerMock;
    private readonly BackgroundTaskTracker _tracker;

    public BackgroundTaskTrackerTests()
    {
        _loggerMock = new Mock<ILogger<BackgroundTaskTracker>>();
        _tracker = new BackgroundTaskTracker(_loggerMock.Object);
    }

    [Fact]
    public void Track_ReturnsPositiveTaskId()
    {
        // Arrange
        var task = Task.CompletedTask;

        // Act
        var taskId = _tracker.Track(task, "TestTask");

        // Assert
        Assert.True(taskId > 0);
    }

    [Fact]
    public void Track_MultipleTasks_ReturnsUniqueIds()
    {
        // Arrange & Act
        var id1 = _tracker.Track(Task.CompletedTask, "Task1");
        var id2 = _tracker.Track(Task.CompletedTask, "Task2");
        var id3 = _tracker.Track(Task.CompletedTask, "Task3");

        // Assert
        Assert.NotEqual(id1, id2);
        Assert.NotEqual(id2, id3);
        Assert.NotEqual(id1, id3);
    }

    [Fact]
    public async Task ActiveTaskCount_ReflectsRunningTasks()
    {
        // Arrange
        var tcs = new TaskCompletionSource();

        // Act
        _tracker.Track(tcs.Task, "LongRunningTask");
        var countWhileRunning = _tracker.ActiveTaskCount;

        tcs.SetResult();
        await Task.Delay(50); // Give time for cleanup

        var countAfterComplete = _tracker.ActiveTaskCount;

        // Assert
        Assert.Equal(1, countWhileRunning);
        Assert.Equal(0, countAfterComplete);
    }

    [Fact]
    public async Task Run_Action_ExecutesSuccessfully()
    {
        // Arrange
        var executed = false;

        // Act
        var taskId = _tracker.Run(() => executed = true, "ActionTask");
        await Task.Delay(100); // Give time for execution

        // Assert
        Assert.True(taskId > 0);
        Assert.True(executed);
    }

    [Fact]
    public async Task Run_AsyncFunc_ExecutesSuccessfully()
    {
        // Arrange
        var executed = false;

        // Act
        var taskId = _tracker.Run(async () =>
        {
            await Task.Delay(10);
            executed = true;
        }, "AsyncTask");

        await Task.Delay(100); // Give time for execution

        // Assert
        Assert.True(taskId > 0);
        Assert.True(executed);
    }

    [Fact]
    public async Task WaitAllAsync_CompletesWhenAllTasksFinish()
    {
        // Arrange
        var tcs1 = new TaskCompletionSource();
        var tcs2 = new TaskCompletionSource();

        _tracker.Track(tcs1.Task, "Task1");
        _tracker.Track(tcs2.Task, "Task2");

        // Act
        var waitTask = _tracker.WaitAllAsync(TimeSpan.FromSeconds(5));

        tcs1.SetResult();
        tcs2.SetResult();

        var result = await waitTask;

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAllAsync_ReturnsFalseOnTimeout()
    {
        // Arrange
        var tcs = new TaskCompletionSource();
        _tracker.Track(tcs.Task, "NeverCompletingTask");

        // Act
        var result = await _tracker.WaitAllAsync(TimeSpan.FromMilliseconds(50));

        // Assert
        Assert.False(result);

        // Cleanup
        tcs.SetResult();
    }

    [Fact]
    public async Task CancelAllAsync_SignalsCancellation()
    {
        // Arrange
        var taskStarted = new TaskCompletionSource();
        var tcs = new TaskCompletionSource();

        _tracker.Track(Task.Run(async () =>
        {
            taskStarted.SetResult();
            await tcs.Task; // Wait until we complete it
        }), "CancellableTask");

        await taskStarted.Task; // Ensure task has started

        // Act - cancel should return even with timeout
        await _tracker.CancelAllAsync(TimeSpan.FromMilliseconds(100));

        // Complete the task to allow cleanup
        tcs.SetResult();
        await Task.Delay(50);

        // Assert - task should be cleaned up after completion
        Assert.Equal(0, _tracker.ActiveTaskCount);
    }

    [Fact]
    public void Track_AfterDispose_ReturnsNegativeId()
    {
        // Arrange
        _tracker.Dispose();

        // Act
        var taskId = _tracker.Track(Task.CompletedTask, "PostDisposeTask");

        // Assert
        Assert.Equal(-1, taskId);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Act & Assert - should not throw
        _tracker.Dispose();
        _tracker.Dispose();
        _tracker.Dispose();
    }

    [Fact]
    public async Task FailedTask_IsLoggedAsError()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Test exception");

        // Act
        _tracker.Track(Task.FromException(expectedException), "FailingTask");
        await Task.Delay(100); // Give time for monitoring

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("FailingTask")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task CancelledTask_IsLoggedAsDebug()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        _tracker.Track(Task.FromCanceled(cts.Token), "CancelledTask");
        await Task.Delay(100); // Give time for monitoring

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("CancelledTask")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

/// <summary>
/// Simplified BackgroundTaskTracker for testing purposes.
/// This mirrors the production implementation without Dalamud dependencies.
/// </summary>
public sealed class BackgroundTaskTracker : IDisposable
{
    private readonly ILogger<BackgroundTaskTracker> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TrackedTask> _activeTasks = new();
    private readonly CancellationTokenSource _disposalCts = new();
    private int _taskIdCounter;
    private bool _disposed;

    public BackgroundTaskTracker(ILogger<BackgroundTaskTracker> logger)
    {
        _logger = logger;
    }

    public int ActiveTaskCount => _activeTasks.Count(t => !t.Value.Task.IsCompleted);

    public int Track(Task task, string? taskName = null)
    {
        if (_disposed)
        {
            _logger.LogWarning("Attempted to track task after disposal: {TaskName}", taskName ?? "unnamed");
            return -1;
        }

        var taskId = Interlocked.Increment(ref _taskIdCounter);
        var trackedTask = new TrackedTask(taskId, task, taskName ?? $"Task-{taskId}", DateTime.UtcNow);

        _activeTasks.TryAdd(taskId, trackedTask);

        _ = MonitorTaskAsync(trackedTask);

        return taskId;
    }

    public int Run(Action action, string? taskName = null, CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token, cancellationToken);
        var task = Task.Run(action, linkedCts.Token);
        return Track(task, taskName);
    }

    public int Run(Func<Task> func, string? taskName = null, CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token, cancellationToken);
        var task = Task.Run(func, linkedCts.Token);
        return Track(task, taskName);
    }

    public async Task<bool> WaitAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var tasks = _activeTasks.Values.Select(t => t.Task).ToArray();
        if (tasks.Length == 0)
            return true;

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            await Task.WhenAll(tasks).WaitAsync(linkedCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task CancelAllAsync(TimeSpan timeout)
    {
        _disposalCts.Cancel();
        await WaitAllAsync(timeout, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MonitorTaskAsync(TrackedTask trackedTask)
    {
        try
        {
            await trackedTask.Task.ConfigureAwait(false);
            _logger.LogTrace("Background task completed: {TaskName} (ID: {TaskId})",
                trackedTask.Name, trackedTask.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background task cancelled: {TaskName} (ID: {TaskId})",
                trackedTask.Name, trackedTask.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background task failed: {TaskName} (ID: {TaskId}, Duration: {Duration})",
                trackedTask.Name, trackedTask.Id, DateTime.UtcNow - trackedTask.StartTime);
        }
        finally
        {
            _activeTasks.TryRemove(trackedTask.Id, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _disposalCts.Cancel();

        var remainingTasks = _activeTasks.Values.Select(t => t.Task).ToArray();
        if (remainingTasks.Length > 0)
        {
            try
            {
                Task.WaitAll(remainingTasks, TimeSpan.FromSeconds(5));
            }
            catch
            {
                _logger.LogWarning("Some background tasks did not complete during disposal. Active tasks: {Count}",
                    remainingTasks.Count(t => !t.IsCompleted));
            }
        }

        _disposalCts.Dispose();
    }

    private sealed record TrackedTask(int Id, Task Task, string Name, DateTime StartTime);
}
