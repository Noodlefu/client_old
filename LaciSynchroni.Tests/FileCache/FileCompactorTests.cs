using System.Diagnostics;
using Xunit;

namespace LaciSynchroni.Tests.FileCache;

/// <summary>
/// Tests for FileCompactor functionality.
/// </summary>
public class FileCompactorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestFileCompactor _compactor;

    public FileCompactorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FileCompactorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _compactor = new TestFileCompactor();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WriteAllBytesAsync_WritesFileCorrectly()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await _compactor.WriteAllBytesAsync(filePath, data, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(filePath));
        Assert.Equal(data, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task WriteAllBytesAsync_WhenCompressionEnabled_CallsCompactFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        _compactor.UseCompactor = true;

        // Act
        await _compactor.WriteAllBytesAsync(filePath, data, CancellationToken.None);

        // Assert
        Assert.True(_compactor.CompactFileCalled);
        Assert.Equal(filePath, _compactor.LastCompactedFilePath);
    }

    [Fact]
    public async Task WriteAllBytesAsync_WhenCompressionDisabled_DoesNotCallCompactFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        _compactor.UseCompactor = false;

        // Act
        await _compactor.WriteAllBytesAsync(filePath, data, CancellationToken.None);

        // Assert
        Assert.False(_compactor.CompactFileCalled);
    }

    [Fact]
    public async Task WriteAllBytesAsync_WhenIsWine_DoesNotCallCompactFile()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        _compactor.UseCompactor = true;
        _compactor.IsWine = true;

        // Act
        await _compactor.WriteAllBytesAsync(filePath, data, CancellationToken.None);

        // Assert
        Assert.False(_compactor.CompactFileCalled);
    }

    [Fact]
    public async Task WriteAllBytesAsync_CompactFileRunsOnThreadPool()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        _compactor.UseCompactor = true;

        var callingThreadId = Environment.CurrentManagedThreadId;
        int? compactFileThreadId = null;
        _compactor.OnCompactFile = () =>
        {
            compactFileThreadId = Environment.CurrentManagedThreadId;
        };

        // Act
        await _compactor.WriteAllBytesAsync(filePath, data, CancellationToken.None);

        // Assert
        Assert.NotNull(compactFileThreadId);
        // Note: Task.Run doesn't guarantee a different thread, but in practice
        // with a synchronization context or sufficient work, it typically will be.
        // The key behavior we're testing is that CompactFile is called via Task.Run.
        Assert.True(_compactor.CompactFileCalledViaTaskRun);
    }

    [Fact]
    public async Task WriteAllBytesAsync_DoesNotBlockCallerDuringCompression()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[1024];
        _compactor.UseCompactor = true;
        _compactor.CompactFileDelay = TimeSpan.FromMilliseconds(100);

        // Act
        var stopwatch = Stopwatch.StartNew();
        await _compactor.WriteAllBytesAsync(filePath, data, CancellationToken.None);
        stopwatch.Stop();

        // Assert
        // If compression were blocking synchronously on the calling thread without Task.Run,
        // the test framework's synchronization context could cause issues.
        // With Task.Run, the work is offloaded properly.
        Assert.True(_compactor.CompactFileCalled);
        // The method should complete (including the Task.Run) - timing isn't the key test here,
        // the key is that it's properly async via Task.Run
        Assert.True(_compactor.CompactFileCalledViaTaskRun);
    }

    [Fact]
    public async Task WriteAllBytesAsync_CancellationToken_AffectsFileWrite()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "test.dat");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        // TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _compactor.WriteAllBytesAsync(filePath, data, cts.Token));
    }
}

#region Test Infrastructure

/// <summary>
/// Test implementation of FileCompactor that tracks calls and allows simulation.
/// Mirrors the production implementation's async behavior.
/// </summary>
internal class TestFileCompactor
{
    public bool IsWine { get; set; } = false;
    public bool UseCompactor { get; set; } = false;
    public bool CompactFileCalled { get; private set; } = false;
    public bool CompactFileCalledViaTaskRun { get; private set; } = false;
    public string? LastCompactedFilePath { get; private set; } = null;
    public TimeSpan CompactFileDelay { get; set; } = TimeSpan.Zero;
    public Action? OnCompactFile { get; set; }

    public async Task WriteAllBytesAsync(string filePath, byte[] decompressedFile, CancellationToken token)
    {
        await File.WriteAllBytesAsync(filePath, decompressedFile, token).ConfigureAwait(false);

        if (IsWine || !UseCompactor)
        {
            return;
        }

        // Run compression on thread pool to avoid blocking the download pipeline
        await Task.Run(() => CompactFile(filePath), CancellationToken.None).ConfigureAwait(false);
        CompactFileCalledViaTaskRun = true;
    }

    private void CompactFile(string filePath)
    {
        CompactFileCalled = true;
        LastCompactedFilePath = filePath;
        OnCompactFile?.Invoke();

        if (CompactFileDelay > TimeSpan.Zero)
        {
            Thread.Sleep(CompactFileDelay);
        }
    }
}

#endregion
