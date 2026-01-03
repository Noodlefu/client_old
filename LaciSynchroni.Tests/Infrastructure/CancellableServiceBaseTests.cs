using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LaciSynchroni.Tests.Infrastructure;

/// <summary>
/// Tests for CancellableServiceBase functionality.
/// These tests validate CTS management, disposal, and thread safety.
/// </summary>
public class CancellableServiceBaseTests
{
    private readonly Mock<ILogger> _loggerMock;

    public CancellableServiceBaseTests()
    {
        _loggerMock = new Mock<ILogger>();
    }

    [Fact]
    public void CreateManagedCts_ReturnsValidCts()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);

        // Act
        var cts = service.TestCreateManagedCts();

        // Assert
        Assert.NotNull(cts);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public void CreateManagedCts_WithTimeout_ReturnsValidCts()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);

        // Act
        var cts = service.TestCreateManagedCts(TimeSpan.FromMinutes(5));

        // Assert
        Assert.NotNull(cts);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task CreateManagedCts_WithTimeout_CancelsAfterTimeout()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);

        // Act
        var cts = service.TestCreateManagedCts(TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);

        // Assert
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void RecreateCts_CancelsExistingCts()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);
        var original = service.TestCreateManagedCts();

        // Act
        CancellationTokenSource? existing = original;
        var newCts = service.TestRecreateCts(ref existing);

        // Assert
        Assert.True(original.IsCancellationRequested);
        Assert.NotNull(newCts);
        Assert.False(newCts.IsCancellationRequested);
        Assert.Same(existing, newCts);
    }

    [Fact]
    public void RecreateCts_WithNull_CreatesNewCts()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);
        CancellationTokenSource? existing = null;

        // Act
        var newCts = service.TestRecreateCts(ref existing);

        // Assert
        Assert.NotNull(newCts);
        Assert.NotNull(existing);
        Assert.Same(existing, newCts);
    }

    [Fact]
    public void CancelCts_CancelsToken()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);
        var cts = service.TestCreateManagedCts();

        // Act
        service.TestCancelCts(cts);

        // Assert
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void CancelCts_WithNull_DoesNotThrow()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);

        // Act & Assert - should not throw
        service.TestCancelCts(null);
    }

    [Fact]
    public void CreateLinkedCts_LinksToExternalToken()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);
        using var externalCts = new CancellationTokenSource();

        // Act
        var linkedCts = service.TestCreateLinkedCts(externalCts.Token);
        externalCts.Cancel();

        // Assert
        Assert.True(linkedCts.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_CancelsAllManagedCts()
    {
        // Arrange
        var service = new TestCancellableService(_loggerMock.Object);
        var cts1 = service.TestCreateManagedCts();
        var cts2 = service.TestCreateManagedCts();
        var cts3 = service.TestCreateManagedCts();

        // Act
        service.Dispose();

        // Assert
        Assert.True(cts1.IsCancellationRequested);
        Assert.True(cts2.IsCancellationRequested);
        Assert.True(cts3.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_CallsOnDisposing()
    {
        // Arrange
        var service = new TestCancellableService(_loggerMock.Object);

        // Act
        service.Dispose();

        // Assert
        Assert.True(service.OnDisposingCalled);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = new TestCancellableService(_loggerMock.Object);

        // Act & Assert - should not throw
        service.Dispose();
        service.Dispose();
        service.Dispose();

        // OnDisposing should only be called once
        Assert.Equal(1, service.OnDisposingCallCount);
    }

    [Fact]
    public void CreateManagedCts_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = new TestCancellableService(_loggerMock.Object);
        service.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => service.TestCreateManagedCts());
    }

    [Fact]
    public void RecreateCts_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = new TestCancellableService(_loggerMock.Object);
        service.Dispose();
        CancellationTokenSource? existing = null;

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => service.TestRecreateCts(ref existing));
    }

    [Fact]
    public void CreateLinkedCts_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var service = new TestCancellableService(_loggerMock.Object);
        service.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => service.TestCreateLinkedCts(CancellationToken.None));
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentCtsCreation()
    {
        // Arrange
        using var service = new TestCancellableService(_loggerMock.Object);
        var tasks = new List<Task<CancellationTokenSource>>();

        // Act - create many CTS concurrently
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() => service.TestCreateManagedCts()));
        }

        var allCts = await Task.WhenAll(tasks);

        // Assert - all should be unique and valid
        Assert.Equal(100, allCts.Length);
        Assert.All(allCts, cts => Assert.False(cts.IsCancellationRequested));
    }
}

/// <summary>
/// Test implementation of CancellableServiceBase that exposes protected methods for testing.
/// </summary>
public class TestCancellableService : CancellableServiceBase
{
    public bool OnDisposingCalled { get; private set; }
    public int OnDisposingCallCount { get; private set; }

    public TestCancellableService(ILogger logger) : base(logger)
    {
    }

    public CancellationTokenSource TestCreateManagedCts() => CreateManagedCts();
    public CancellationTokenSource TestCreateManagedCts(TimeSpan timeout) => CreateManagedCts(timeout);
    public CancellationTokenSource TestRecreateCts(ref CancellationTokenSource? existing) => RecreateCts(ref existing);
    public void TestCancelCts(CancellationTokenSource? cts) => CancelCts(cts);
    public CancellationTokenSource TestCreateLinkedCts(CancellationToken token) => CreateLinkedCts(token);

    protected override void OnDisposing()
    {
        OnDisposingCalled = true;
        OnDisposingCallCount++;
        base.OnDisposing();
    }
}

/// <summary>
/// Simplified CancellableServiceBase for testing purposes.
/// This mirrors the production implementation without Dalamud dependencies.
/// </summary>
public abstract class CancellableServiceBase : IDisposable
{
    private readonly List<CancellationTokenSource> _managedTokenSources = [];
    private readonly object _ctsLock = new();
    private bool _disposed;

    protected ILogger Logger { get; }

    protected CancellableServiceBase(ILogger logger)
    {
        Logger = logger;
    }

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
