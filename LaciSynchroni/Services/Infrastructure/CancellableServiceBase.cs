using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Services.Infrastructure;

/// <summary>
/// Base class for services that manage CancellationTokenSources.
/// Provides automatic tracking and disposal of managed CTS instances.
/// </summary>
public abstract class CancellableServiceBase : IDisposable
{
    private readonly List<CancellationTokenSource> _managedTokenSources = [];
    private readonly Lock _ctsLock = new();
    private bool _disposed;

    protected ILogger Logger { get; }

    protected CancellableServiceBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Creates a new CancellationTokenSource that will be automatically disposed.
    /// </summary>
    /// <returns>A managed CancellationTokenSource.</returns>
    protected CancellationTokenSource CreateManagedCts()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cts = new CancellationTokenSource();
        lock (_ctsLock)
        {
            _managedTokenSources.Add(cts);
        }
        return cts;
    }

    /// <summary>
    /// Creates a new CancellationTokenSource with a timeout that will be automatically disposed.
    /// </summary>
    /// <param name="timeout">The timeout after which the token will be cancelled.</param>
    /// <returns>A managed CancellationTokenSource.</returns>
    protected CancellationTokenSource CreateManagedCts(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cts = new CancellationTokenSource(timeout);
        lock (_ctsLock)
        {
            _managedTokenSources.Add(cts);
        }
        return cts;
    }

    /// <summary>
    /// Cancels and disposes an existing CTS, then creates a new one.
    /// Thread-safe replacement of a CancellationTokenSource.
    /// </summary>
    /// <param name="existing">Reference to the existing CTS (will be updated).</param>
    /// <returns>The new CancellationTokenSource.</returns>
    protected CancellationTokenSource RecreateCts(ref CancellationTokenSource? existing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_ctsLock)
        {
            if (existing != null)
            {
                try
                {
                    existing.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }

                try
                {
                    existing.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }

                _managedTokenSources.Remove(existing);
            }

            existing = new CancellationTokenSource();
            _managedTokenSources.Add(existing);
            return existing;
        }
    }

    /// <summary>
    /// Cancels a specific managed CTS without recreating it.
    /// </summary>
    /// <param name="cts">The CTS to cancel.</param>
    protected void CancelCts(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
    }

    /// <summary>
    /// Creates a linked CTS that cancels when either the managed CTS or the external token cancels.
    /// </summary>
    /// <param name="externalToken">The external cancellation token to link.</param>
    /// <returns>A linked CancellationTokenSource.</returns>
    protected CancellationTokenSource CreateLinkedCts(CancellationToken externalToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        lock (_ctsLock)
        {
            _managedTokenSources.Add(cts);
        }
        return cts;
    }

    /// <summary>
    /// Override this to perform additional cleanup.
    /// Called before CTS disposal.
    /// </summary>
    protected virtual void OnDisposing()
    {
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;

        if (disposing)
        {
            OnDisposing();

            lock (_ctsLock)
            {
                foreach (var cts in _managedTokenSources)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignore
                    }

                    try
                    {
                        cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignore
                    }
                }

                _managedTokenSources.Clear();
            }
        }
    }
}
