using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LaciSynchroni.Services.Infrastructure;

/// <summary>
/// Tracks background tasks to ensure they complete successfully and logs any failures.
/// Prevents fire-and-forget tasks from silently failing.
/// </summary>
public sealed class BackgroundTaskTracker : IDisposable
{
    private readonly ILogger<BackgroundTaskTracker> _logger;
    private readonly ConcurrentDictionary<int, TrackedTask> _activeTasks = new();
    private readonly CancellationTokenSource _disposalCts = new();
    private int _taskIdCounter;
    private bool _disposed;

    public BackgroundTaskTracker(ILogger<BackgroundTaskTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of currently active (incomplete) tasks.
    /// </summary>
    public int ActiveTaskCount => _activeTasks.Count(t => !t.Value.Task.IsCompleted);

    /// <summary>
    /// Tracks a task and logs any exceptions that occur.
    /// </summary>
    /// <param name="task">The task to track.</param>
    /// <param name="taskName">Optional name for the task (for logging).</param>
    /// <returns>The task ID for reference.</returns>
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

    /// <summary>
    /// Tracks a task created from an action, with automatic exception handling.
    /// </summary>
    /// <param name="action">The action to run.</param>
    /// <param name="taskName">Optional name for the task.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The task ID for reference.</returns>
    public int Run(Action action, string? taskName = null, CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token, cancellationToken);
        var task = Task.Run(action, linkedCts.Token);
        return Track(task, taskName);
    }

    /// <summary>
    /// Tracks an async task created from a func, with automatic exception handling.
    /// </summary>
    /// <param name="func">The async function to run.</param>
    /// <param name="taskName">Optional name for the task.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The task ID for reference.</returns>
    public int Run(Func<Task> func, string? taskName = null, CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposalCts.Token, cancellationToken);
        var task = Task.Run(func, linkedCts.Token);
        return Track(task, taskName);
    }

    /// <summary>
    /// Waits for all tracked tasks to complete.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all tasks completed within the timeout.</returns>
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

    /// <summary>
    /// Cancels all tracked tasks and waits for completion.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for tasks to complete after cancellation.</param>
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

        // Give tasks a brief moment to complete
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
