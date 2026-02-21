using K4os.Compression.LZ4.Legacy;
using LaciSynchroni.Interop.Ipc;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace LaciSynchroni.FileCache;

public sealed class FileCacheManager(ILogger<FileCacheManager> logger, IpcManager ipcManager, SyncConfigService configService, SyncMediator syncMediator) : IHostedService
{
    public const string CachePrefix = "{cache}";
    public const string CsvSplit = "|";
    public const string PenumbraPrefix = "{penumbra}";
    private readonly SyncConfigService _configService = configService;
    private readonly SyncMediator _syncMediator = syncMediator;
    private readonly string _csvPath = Path.Combine(configService.ConfigurationDirectory, "FileCache.csv");
    private readonly ConcurrentDictionary<string, List<FileCacheEntity>> _fileCaches = new(StringComparer.Ordinal);
    private readonly Lock _fileCachesLock = new();
    private readonly SemaphoreSlim _getCachesByPathsSemaphore = new(1, 1);
    private readonly SemaphoreSlim _decompressionSemaphore = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4), Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
    private readonly Lock _fileWriteLock = new();
    private readonly IpcManager _ipcManager = ipcManager;
    private readonly ILogger<FileCacheManager> _logger = logger;

    /// <summary>
    /// Cache for file validation results to avoid repeated FileInfo.Exists calls.
    /// Key: resolved file path, Value: (validation result, expiration time)
    /// </summary>
    private readonly ConcurrentDictionary<string, (bool Exists, long LastModifiedTicks, long ExpiresAt)> _validationCache = new(StringComparer.OrdinalIgnoreCase);
    private const int ValidationCacheTtlMs = 5000; // 5 second TTL

    /// <summary>
    /// Tracks pending file downloads by hash. When a download is in progress for a hash,
    /// other requesters will wait on the TaskCompletionSource instead of starting duplicate downloads.
    /// The value contains the TCS and a count of how many tasks are interested in this download.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingDownloadInfo> _pendingDownloads = new(StringComparer.Ordinal);

    /// <summary>
    /// Lock for coordinating pending download handoffs.
    /// </summary>
    private readonly Lock _pendingDownloadsLock = new();

    /// <summary>
    /// Queue of CSV entry strings pending a flush to disk.
    /// Entries are batched and written by a background timer to avoid per-entry I/O during scanning.
    /// </summary>
    private readonly ConcurrentQueue<string> _pendingCsvEntries = new();
    private Timer? _csvFlushTimer;

    /// <summary>
    /// Tracks information about a pending download, including waiter count for handoff support.
    /// </summary>
    private sealed class PendingDownloadInfo(TaskCompletionSource<string?> tcs)
    {
        public TaskCompletionSource<string?> Tcs { get; set; } = tcs;
        public int WaiterCount { get; set; } = 1; // The downloader counts as 1

    }

    public string CacheFolder => _configService.Current.CacheFolder;

    private string CsvBakPath => _csvPath + ".bak";

    public FileCacheEntity? CreateCacheEntry(string path)
    {
        FileInfo fi = new(path);
        if (!fi.Exists)
        {
            _logger.LogWarning("CreateCacheEntry: File does not exist: {Path}", path);
            return null;
        }
        _logger.LogTrace("Creating cache entry for {Path}", path);

        // Normalize paths to handle different formats (forward/back slashes, trailing slashes, case)
        var fullName = NormalizePath(fi.FullName);
        var cacheFolder = NormalizePath(_configService.Current.CacheFolder);

        if (!fullName.StartsWith(cacheFolder, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("CreateCacheEntry: Path {FullName} is not under cache folder {CacheFolder}", fullName, cacheFolder);
            return null;
        }

        // Build prefixed path by replacing the cache folder with the prefix
        var relativePath = fullName[cacheFolder.Length..].TrimStart('\\');
        string prefixedPath = CachePrefix + "\\" + relativePath;
        return CreateFileCacheEntity(fi, prefixedPath);
    }

    /// <summary>
    /// Normalizes a path for consistent comparison: converts to lowercase, uses backslashes, removes trailing slashes.
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        // Get the full path to resolve any relative components and normalize separators
        var normalized = Path.GetFullPath(path).ToLowerInvariant();
        // Ensure consistent trailing slash handling - remove trailing separator
        return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public FileCacheEntity? CreateFileEntry(string path)
    {
        FileInfo fi = new(path);
        if (!fi.Exists)
        {
            _logger.LogWarning("CreateFileEntry: File does not exist: {Path}", path);
            return null;
        }
        _logger.LogTrace("Creating file entry for {Path}", path);

        // Normalize paths to handle different formats (forward/back slashes, trailing slashes, case)
        var fullName = NormalizePath(fi.FullName);
        var modDirectory = NormalizePath(_ipcManager.Penumbra.ModDirectory!);

        if (!fullName.StartsWith(modDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("CreateFileEntry: Path {FullName} is not under mod directory {ModDirectory}", fullName, modDirectory);
            return null;
        }

        // Build prefixed path by replacing the mod directory with the prefix
        var relativePath = fullName[modDirectory.Length..].TrimStart('\\');
        string prefixedPath = $"{PenumbraPrefix}\\{relativePath}";
        return CreateFileCacheEntity(fi, prefixedPath);
    }

    public void ClearAll()
    {
        lock (_fileCachesLock)
        {
            _fileCaches.Clear();
        }
        _validationCache.Clear();
        _logger.LogInformation("Cleared all file caches");
    }

    public List<FileCacheEntity> GetAllFileCaches()
    {
        lock (_fileCachesLock)
        {
            return [.. _fileCaches.Values.SelectMany(v => v)];
        }
    }

    public List<FileCacheEntity> GetAllFileCachesByHash(string hash, bool ignoreCacheEntries = false, bool validate = true)
    {
        List<FileCacheEntity> output = [];
        List<FileCacheEntity> entitiesToProcess;
        lock (_fileCachesLock)
        {
            if (!_fileCaches.TryGetValue(hash, out var fileCacheEntities))
            {
                return output;
            }
            entitiesToProcess = [.. fileCacheEntities.Where(c => !ignoreCacheEntries || !c.IsCacheEntry)];
        }

        foreach (var fileCache in entitiesToProcess)
        {
            if (!validate) output.Add(fileCache);
            else
            {
                var validated = GetValidatedFileCache(fileCache);
                if (validated != null) output.Add(validated);
            }
        }

        return output;
    }

    public async Task<List<FileCacheEntity>> ValidateLocalIntegrity(IProgress<(int, int, FileCacheEntity)> progress, CancellationToken cancellationToken)
    {
        _syncMediator.Publish(new HaltScanMessage(nameof(ValidateLocalIntegrity)));
        _logger.LogInformation("Validating local storage");
        List<FileCacheEntity> cacheEntries;
        lock (_fileCachesLock)
        {
            cacheEntries = [.. _fileCaches.SelectMany(v => v.Value).Where(v => v.IsCacheEntry)];
        }

        var brokenEntities = new ConcurrentBag<FileCacheEntity>();
        var processedCount = 0;
        var totalCount = cacheEntries.Count;

        // Use parallel processing for hash validation (I/O and CPU bound)
        // Limit to half the cores to avoid saturating CPU and disk
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            CancellationToken = cancellationToken,
        };

        try
        {
            await Parallel.ForEachAsync(cacheEntries, parallelOptions, async (fileCache, ct) =>
            {
                var currentProgress = Interlocked.Increment(ref processedCount);

                _logger.LogDebug("Validating {File} ({Current}/{Total})", fileCache.ResolvedFilepath, currentProgress, totalCount);
                progress.Report((currentProgress, totalCount, fileCache));

                if (!File.Exists(fileCache.ResolvedFilepath))
                {
                    brokenEntities.Add(fileCache);
                    return;
                }

                try
                {
                    string computedHash;
                    if (fileCache.ResolvedFilepath.EndsWith(".llz4", StringComparison.OrdinalIgnoreCase))
                    {
                        await _decompressionSemaphore.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var compressed = await File.ReadAllBytesAsync(fileCache.ResolvedFilepath, ct).ConfigureAwait(false);
                            var decompressed = LZ4Wrapper.Unwrap(compressed);
                            computedHash = Crypto.GetHashFromBytes(decompressed);
                        }
                        finally { _decompressionSemaphore.Release(); }
                    }
                    else
                    {
                        computedHash = await Task.Run(() => Crypto.GetFileHash(fileCache.ResolvedFilepath), ct).ConfigureAwait(false);
                    }

                    if (!string.Equals(computedHash, fileCache.Hash, StringComparison.Ordinal))
                    {
                        _logger.LogInformation("Failed to validate {File}, got hash {ActualHash}, expected hash {ExpectedHash}",
                            fileCache.ResolvedFilepath, computedHash, fileCache.Hash);
                        brokenEntities.Add(fileCache);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // Propagate cancellation
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Error during validation of {File}", fileCache.ResolvedFilepath);
                    brokenEntities.Add(fileCache);
                }

                // Yield periodically to reduce pressure on CPU and disk
                if (currentProgress % 50 == 0)
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Validation cancelled");
        }

        var brokenList = brokenEntities.ToList();

        foreach (var brokenEntity in brokenList)
        {
            RemoveHashedFile(brokenEntity.Hash, brokenEntity.PrefixedFilePath);

            try
            {
                File.Delete(brokenEntity.ResolvedFilepath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete {File}", brokenEntity.ResolvedFilepath);
            }
        }

        _syncMediator.Publish(new ResumeScanMessage(nameof(ValidateLocalIntegrity)));
        return brokenList;
    }

    public string GetCacheFilePath(string hash, string extension)
    {
        return Path.Combine(_configService.Current.CacheFolder, hash + "." + extension);
    }

    /// <summary>
    /// Attempts to acquire the right to download a file by hash.
    /// If the file already exists, returns immediately with the existing path.
    /// If another download is in progress for this hash, waits for it to complete.
    /// If no download is in progress, grants the caller the right to download and returns shouldDownload=true.
    ///
    /// When the downloader cancels, the download is handed off to another waiter if one exists,
    /// preventing the scenario where Character A's cancellation causes Character B to fail.
    /// </summary>
    /// <param name="hash">The file hash to acquire</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>A tuple indicating whether the caller should download the file, and the existing path if available</returns>
    public async Task<(bool ShouldDownload, string? ExistingPath)> AcquireFileAsync(string hash, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // First check if file already exists in cache
            var existing = GetFileCacheByHash(hash);
            if (existing != null)
            {
                _logger.LogTrace("File {Hash} already exists in cache at {Path}", hash, existing.ResolvedFilepath);
                return (false, existing.ResolvedFilepath);
            }

            PendingDownloadInfo? existingInfo;
            lock (_pendingDownloadsLock)
            {
                // Try to become the downloader for this hash
                if (!_pendingDownloads.TryGetValue(hash, out existingInfo))
                {
                    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var info = new PendingDownloadInfo(tcs);
                    _pendingDownloads[hash] = info;
                    _logger.LogTrace("Acquired download rights for {Hash}", hash);
                    return (true, null); // We're responsible for downloading
                }

                // Another download is in progress - increment waiter count and wait
                existingInfo.WaiterCount++;
                _logger.LogTrace("Waiting for pending download of {Hash} (waiter count: {Count})", hash, existingInfo.WaiterCount);
            }

            try
            {
                var path = await existingInfo.Tcs.Task.WaitAsync(ct).ConfigureAwait(false);
                _logger.LogTrace("Pending download of {Hash} completed with path {Path}", hash, path);
                return (false, path);
            }
            catch (DownloadHandoffException)
            {
                // The original downloader cancelled but we should retry to become the new downloader
                _logger.LogTrace("Download handoff for {Hash}, retrying to acquire", hash);
                // Loop back to try again - we might become the new downloader
                continue;
            }
            catch (OperationCanceledException)
            {
                // Our own cancellation - decrement waiter count and propagate
                lock (_pendingDownloadsLock)
                {
                    if (_pendingDownloads.TryGetValue(hash, out var info))
                    {
                        info.WaiterCount--;
                        _logger.LogTrace("Waiter cancelled for {Hash} (remaining waiters: {Count})", hash, info.WaiterCount);
                    }
                }
                _logger.LogTrace("Wait for pending download of {Hash} was cancelled", hash);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for pending download of {Hash}", hash);
                throw;
            }
        }
    }

    /// <summary>
    /// Exception used internally to signal that a download should be handed off to a waiter.
    /// </summary>
    private sealed class DownloadHandoffException : Exception
    {
        public DownloadHandoffException() : base("Download handed off to another waiter") { }
    }

    /// <summary>
    /// Signals that a file download has completed successfully.
    /// Must be called after AcquireFileAsync returns shouldDownload=true.
    /// </summary>
    /// <param name="hash">The file hash that was being downloaded</param>
    /// <param name="filePath">The path to the downloaded file</param>
    public void CompleteFileDownload(string hash, string? filePath)
    {
        lock (_pendingDownloadsLock)
        {
            if (_pendingDownloads.TryRemove(hash, out var info))
            {
                _logger.LogTrace("Completed download for {Hash} with path {Path} (waiters: {Count})", hash, filePath, info.WaiterCount);
                info.Tcs.SetResult(filePath);
            }
            else
            {
                _logger.LogWarning("CompleteFileDownload called for {Hash} but no pending download was found", hash);
            }
        }
    }

    /// <summary>
    /// Signals that a file download has failed with an exception.
    /// Must be called after AcquireFileAsync returns shouldDownload=true.
    ///
    /// If the failure is due to cancellation and there are other waiters,
    /// the download will be handed off to one of them instead of failing everyone.
    /// </summary>
    /// <param name="hash">The file hash that was being downloaded</param>
    /// <param name="exception">The exception that caused the failure</param>
    public void FailFileDownload(string hash, Exception exception)
    {
        lock (_pendingDownloadsLock)
        {
            if (_pendingDownloads.TryGetValue(hash, out var info))
            {
                // Check if this is a cancellation and there are other waiters who could take over
                bool isCancellation = exception is OperationCanceledException;
                // WaiterCount > 1 means there's at least one other task waiting (besides the downloader)
                bool hasOtherWaiters = info.WaiterCount > 1;

                if (isCancellation && hasOtherWaiters)
                {
                    // Hand off to another waiter - remove the current entry and signal handoff
                    _pendingDownloads.TryRemove(hash, out _);
                    _logger.LogDebug("Download cancelled for {Hash}, handing off to one of {Count} remaining waiters", hash, info.WaiterCount - 1);
                    // Signal all waiters to retry - one of them will become the new downloader
                    info.Tcs.SetException(new DownloadHandoffException());
                }
                else if (hasOtherWaiters)
                {
                    // Propagate the failure to all waiters
                    _pendingDownloads.TryRemove(hash, out _);
                    _logger.LogTrace("Failed download for {Hash} with exception {Exception}", hash, exception.Message);
                    info.Tcs.SetException(exception);
                }
                else
                {
                    // No waiters - just clean up without setting exception to avoid unobserved task exception
                    _pendingDownloads.TryRemove(hash, out _);
                    _logger.LogTrace("Failed download for {Hash} with no waiters, cleaning up", hash);
                }
            }
            else
            {
                _logger.LogWarning("FailFileDownload called for {Hash} but no pending download was found", hash);
            }
        }
    }

    public async Task<(string, byte[])> GetCompressedFileData(string fileHash, CancellationToken uploadToken)
    {
        var fileCache = GetFileCacheByHash(fileHash)!.ResolvedFilepath;
        var fileBytes = await File.ReadAllBytesAsync(fileCache, uploadToken).ConfigureAwait(false);
        return (fileHash, LZ4Wrapper.WrapHC(fileBytes, 0, (int)new FileInfo(fileCache).Length));
    }

    public FileCacheEntity? GetFileCacheByHash(string hash)
    {
        FileCacheEntity? item;
        lock (_fileCachesLock)
        {
            if (!_fileCaches.TryGetValue(hash, out var hashes))
            {
                return null;
            }
            item = hashes.OrderBy(p => p.PrefixedFilePath.Contains(PenumbraPrefix) ? 0 : 1).FirstOrDefault();
        }
        if (item != null) return GetValidatedFileCache(item);
        return null;
    }

    private FileCacheEntity? GetFileCacheByPath(string path)
    {
        var cleanedPath = path.Replace("/", "\\", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()
            .Replace(_ipcManager.Penumbra.ModDirectory!.ToLowerInvariant(), "", StringComparison.OrdinalIgnoreCase);
        FileCacheEntity? entry;
        lock (_fileCachesLock)
        {
            entry = _fileCaches.SelectMany(v => v.Value).FirstOrDefault(f => f.ResolvedFilepath.EndsWith(cleanedPath, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null)
        {
            _logger.LogDebug("Found no entries for {Path}", cleanedPath);
            return CreateFileEntry(path);
        }

        var validatedCacheEntry = GetValidatedFileCache(entry);

        return validatedCacheEntry;
    }

    public Dictionary<string, FileCacheEntity?> GetFileCachesByPaths(string[] paths)
    {
        _getCachesByPathsSemaphore.Wait();

        try
        {
            var cleanedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p,
                p => p.Replace("/", "\\", StringComparison.OrdinalIgnoreCase)
                    .Replace(_ipcManager.Penumbra.ModDirectory!, _ipcManager.Penumbra.ModDirectory!.EndsWith('\\') ? PenumbraPrefix + '\\' : PenumbraPrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace(_configService.Current.CacheFolder, _configService.Current.CacheFolder.EndsWith('\\') ? CachePrefix + '\\' : CachePrefix, StringComparison.OrdinalIgnoreCase)
                    .Replace("\\\\", "\\", StringComparison.Ordinal),
                StringComparer.OrdinalIgnoreCase);

            Dictionary<string, FileCacheEntity?> result = new(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, FileCacheEntity> dict;
            lock (_fileCachesLock)
            {
                dict = _fileCaches.SelectMany(f => f.Value)
                    .ToDictionary(d => d.PrefixedFilePath, d => d, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var entry in cleanedPaths)
            {
                if (dict.TryGetValue(entry.Value, out var entity))
                {
                    var validatedCache = GetValidatedFileCache(entity);
                    result.Add(entry.Key, validatedCache);
                }
                else
                {
                    if (!entry.Value.Contains(CachePrefix, StringComparison.Ordinal))
                        result.Add(entry.Key, CreateFileEntry(entry.Key));
                    else
                        result.Add(entry.Key, CreateCacheEntry(entry.Key));
                }
            }

            return result;
        }
        finally
        {
            _getCachesByPathsSemaphore.Release();
        }
    }

    public void RemoveHashedFile(string hash, string prefixedFilePath)
    {
        lock (_fileCachesLock)
        {
            if (_fileCaches.TryGetValue(hash, out var caches))
            {
                // Invalidate validation cache for removed files
                foreach (var cache in caches?.Where(c => string.Equals(c.PrefixedFilePath, prefixedFilePath, StringComparison.Ordinal)) ?? [])
                {
                    InvalidateValidationCache(cache.ResolvedFilepath);
                }

                var removedCount = caches?.RemoveAll(c => string.Equals(c.PrefixedFilePath, prefixedFilePath, StringComparison.Ordinal));
                _logger.LogTrace("Removed from DB: {count} file(s) with hash {hash} and file cache {path}", removedCount, hash, prefixedFilePath);

                if (caches?.Count == 0)
                {
                    _fileCaches.Remove(hash, out _);
                }
            }
        }
    }

    /// <summary>
    /// Invalidates the validation cache entry for a specific file path.
    /// </summary>
    private void InvalidateValidationCache(string resolvedFilePath)
    {
        _validationCache.TryRemove(resolvedFilePath, out _);
    }

    public void UpdateHashedFile(FileCacheEntity fileCache, bool computeProperties = true)
    {
        _logger.LogTrace("Updating hash for {path}", fileCache.ResolvedFilepath);
        var oldHash = fileCache.Hash;
        var prefixedPath = fileCache.PrefixedFilePath;
        if (computeProperties)
        {
            var fi = new FileInfo(fileCache.ResolvedFilepath);
            fileCache.Size = fi.Length;
            fileCache.CompressedSize = null;
            fileCache.Hash = Crypto.GetFileHash(fileCache.ResolvedFilepath);
            fileCache.LastModifiedDateTicks = fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        RemoveHashedFile(oldHash, prefixedPath);
        AddHashedFile(fileCache);
    }

    public (FileState State, FileCacheEntity FileCache) ValidateFileCacheEntity(FileCacheEntity fileCache)
    {
        fileCache = ReplacePathPrefixes(fileCache);
        FileInfo fi = new(fileCache.ResolvedFilepath);
        if (!fi.Exists)
        {
            return (FileState.RequireDeletion, fileCache);
        }
        if (!string.Equals(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileCache.LastModifiedDateTicks, StringComparison.Ordinal))
        {
            return (FileState.RequireUpdate, fileCache);
        }

        return (FileState.Valid, fileCache);
    }

    private void FlushPendingCsvEntries()
    {
        if (_pendingCsvEntries.IsEmpty) return;
        var entries = new List<string>();
        while (_pendingCsvEntries.TryDequeue(out var entry))
            entries.Add(entry);
        if (entries.Count == 0) return;
        lock (_fileWriteLock)
        {
            File.AppendAllLines(_csvPath, entries);
        }
    }

    public void WriteOutFullCsv()
    {
        lock (_fileWriteLock)
        {
            // Drain the pending queue — full rewrite covers all entries already in _fileCaches
            while (_pendingCsvEntries.TryDequeue(out _)) { }
            StringBuilder sb = new();
            List<FileCacheEntity> entries;
            lock (_fileCachesLock)
            {
                entries = _fileCaches.SelectMany(k => k.Value).OrderBy(f => f.PrefixedFilePath, StringComparer.OrdinalIgnoreCase).ToList();
            }
            foreach (var entry in entries)
            {
                sb.AppendLine(entry.CsvEntry);
            }

            if (File.Exists(_csvPath))
            {
                File.Copy(_csvPath, CsvBakPath, overwrite: true);
            }

            try
            {
                File.WriteAllText(_csvPath, sb.ToString());
                File.Delete(CsvBakPath);
            }
            catch
            {
                File.WriteAllText(CsvBakPath, sb.ToString());
            }
        }
    }

    internal FileCacheEntity MigrateFileHashToExtension(FileCacheEntity fileCache, string ext)
    {
        try
        {
            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            var extensionPath = fileCache.ResolvedFilepath.ToUpper(CultureInfo.InvariantCulture) + "." + ext;
            File.Move(fileCache.ResolvedFilepath, extensionPath, overwrite: true);
            var newHashedEntity = new FileCacheEntity(fileCache.Hash, fileCache.PrefixedFilePath + "." + ext, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            newHashedEntity.SetResolvedFilePath(extensionPath);
            AddHashedFile(newHashedEntity);
            _logger.LogTrace("Migrated from {oldPath} to {newPath}", fileCache.ResolvedFilepath, newHashedEntity.ResolvedFilepath);
            return newHashedEntity;
        }
        catch (Exception ex)
        {
            AddHashedFile(fileCache);
            _logger.LogWarning(ex, "Failed to migrate entity {entity}", fileCache.PrefixedFilePath);
            return fileCache;
        }
    }

    private void AddHashedFile(FileCacheEntity fileCache)
    {
        lock (_fileCachesLock)
        {
            if (!_fileCaches.TryGetValue(fileCache.Hash, out var entries) || entries is null)
            {
                _fileCaches[fileCache.Hash] = entries = [];
            }

            if (!entries.Exists(u => string.Equals(u.PrefixedFilePath, fileCache.PrefixedFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(fileCache);
            }
        }
    }

    private FileCacheEntity? CreateFileCacheEntity(FileInfo fileInfo, string prefixedPath, string? hash = null)
    {
        hash ??= Crypto.GetFileHash(fileInfo.FullName);
        var entity = new FileCacheEntity(hash, prefixedPath, fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), fileInfo.Length);
        entity = ReplacePathPrefixes(entity);
        AddHashedFile(entity);
        _pendingCsvEntries.Enqueue(entity.CsvEntry);
        var result = GetFileCacheByPath(fileInfo.FullName);
        _logger.LogTrace("Creating cache entity for {name} success: {success}", fileInfo.FullName, (result != null));
        return result;
    }

    private FileCacheEntity? GetValidatedFileCache(FileCacheEntity fileCache)
    {
        var resultingFileCache = ReplacePathPrefixes(fileCache);
        resultingFileCache = Validate(resultingFileCache);
        return resultingFileCache;
    }

    private FileCacheEntity ReplacePathPrefixes(FileCacheEntity fileCache)
    {
        if (fileCache.PrefixedFilePath.StartsWith(PenumbraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(PenumbraPrefix, _ipcManager.Penumbra.ModDirectory, StringComparison.Ordinal));
        }
        else if (fileCache.PrefixedFilePath.StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase))
        {
            fileCache.SetResolvedFilePath(fileCache.PrefixedFilePath.Replace(CachePrefix, _configService.Current.CacheFolder, StringComparison.Ordinal));
        }

        return fileCache;
    }

    private FileCacheEntity? Validate(FileCacheEntity fileCache)
    {
        var resolvedPath = fileCache.ResolvedFilepath;
        var now = Environment.TickCount64;

        // Check validation cache first
        if (_validationCache.TryGetValue(resolvedPath, out var cached) && cached.ExpiresAt > now)
        {
            if (!cached.Exists)
            {
                RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
                return null;
            }

            // Check if last modified time changed since we cached it
            if (!string.Equals(cached.LastModifiedTicks.ToString(CultureInfo.InvariantCulture), fileCache.LastModifiedDateTicks, StringComparison.Ordinal))
            {
                UpdateHashedFile(fileCache);
            }
            return fileCache;
        }

        // Cache miss or expired - perform actual file check
        var file = new FileInfo(resolvedPath);
        var exists = file.Exists;
        var lastModifiedTicks = exists ? file.LastWriteTimeUtc.Ticks : 0;

        // Update cache
        _validationCache[resolvedPath] = (exists, lastModifiedTicks, now + ValidationCacheTtlMs);

        if (!exists)
        {
            RemoveHashedFile(fileCache.Hash, fileCache.PrefixedFilePath);
            return null;
        }

        if (!string.Equals(lastModifiedTicks.ToString(CultureInfo.InvariantCulture), fileCache.LastModifiedDateTicks, StringComparison.Ordinal))
        {
            UpdateHashedFile(fileCache);
        }

        return fileCache;
    }

    /// <summary>
    /// Cleans up orphaned temporary files from previous sessions.
    /// These files (.blk, .bin) are created during downloads and may be left behind if the process crashes.
    /// </summary>
    private void CleanupOrphanedTempFiles()
    {
        try
        {
            var cacheFolder = _configService.Current.CacheFolder;
            if (string.IsNullOrEmpty(cacheFolder) || !Directory.Exists(cacheFolder))
            {
                return;
            }

            var tempExtensions = new[] { "*.blk", "*.bin" };
            var cleanedCount = 0;
            long cleanedBytes = 0;

            foreach (var pattern in tempExtensions)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(cacheFolder, pattern, SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            cleanedBytes += fi.Length;
                            fi.Delete();
                            cleanedCount++;
                            _logger.LogDebug("Cleaned up orphaned temp file: {File}", file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete orphaned temp file: {File}", file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate temp files with pattern {Pattern}", pattern);
                }
            }

            if (cleanedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} orphaned temp files ({Size} bytes)", cleanedCount, cleanedBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup orphaned temp files");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting FileCacheManager");

        CleanupOrphanedTempFiles();

        lock (_fileWriteLock)
        {
            try
            {
                _logger.LogInformation("Checking for {bakPath}", CsvBakPath);

                if (File.Exists(CsvBakPath))
                {
                    _logger.LogInformation("{bakPath} found, moving to {csvPath}", CsvBakPath, _csvPath);

                    File.Move(CsvBakPath, _csvPath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to move BAK to ORG, deleting BAK");
                try
                {
                    if (File.Exists(CsvBakPath))
                        File.Delete(CsvBakPath);
                }
                catch (Exception ex1)
                {
                    _logger.LogWarning(ex1, "Could not delete bak file");
                }
            }
        }

        if (File.Exists(_csvPath))
        {
            if (!_ipcManager.Penumbra.APIAvailable || string.IsNullOrEmpty(_ipcManager.Penumbra.ModDirectory))
            {
                _syncMediator.Publish(new NotificationMessage("Penumbra not connected",
                    "Could not load local file cache data. Penumbra is not connected or not properly set up." +
                    " Please enable and/or configure Penumbra properly to use this plugin. After, reload the plugin in the Plugin installer.",
                    NotificationType.Error));
            }

            _logger.LogInformation("{CsvPath} found, parsing", _csvPath);

            bool success = false;
            string[] entries = [];
            int attempts = 0;
            int delayMs = 100;
            while (!success && attempts < 10)
            {
                try
                {
                    _logger.LogInformation("Attempting to read {CsvPath}", _csvPath);
                    entries = await File.ReadAllLinesAsync(_csvPath, cancellationToken).ConfigureAwait(false);
                    success = true;
                }
                catch (Exception ex)
                {
                    attempts++;
                    _logger.LogWarning(ex, "Could not open {File}, trying again (attempt {Attempt}/10)", _csvPath, attempts);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, 1000); // Exponential backoff up to 1 second
                }
            }

            if (entries.Length == 0)
            {
                _logger.LogWarning("Could not load entries from {Path}, continuing with empty file cache", _csvPath);
            }

            _logger.LogInformation("Found {Amount} files in {Path}", entries.Length, _csvPath);

            Dictionary<string, bool> processedFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var splittedEntry = entry.Split(CsvSplit, StringSplitOptions.None);
                try
                {
                    var hash = splittedEntry[0];
                    if (hash.Length != 40) throw new InvalidOperationException("Expected Hash length of 40, received " + hash.Length);
                    var path = splittedEntry[1];
                    var time = splittedEntry[2];

                    if (processedFiles.ContainsKey(path))
                    {
                        _logger.LogWarning("Already processed {file}, ignoring", path);
                        continue;
                    }

                    processedFiles.Add(path, value: true);

                    long size = -1;
                    long compressed = -1;
                    if (splittedEntry.Length > 3)
                    {
                        if (long.TryParse(splittedEntry[3], CultureInfo.InvariantCulture, out long result))
                        {
                            size = result;
                        }
                        if (long.TryParse(splittedEntry[4], CultureInfo.InvariantCulture, out long resultCompressed))
                        {
                            compressed = resultCompressed;
                        }
                    }
                    AddHashedFile(ReplacePathPrefixes(new FileCacheEntity(hash, path, time, size, compressed)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to initialize entry {entry}, ignoring", entry);
                }
            }

            if (processedFiles.Count != entries.Length)
            {
                WriteOutFullCsv();
            }
        }

        _csvFlushTimer = new Timer(_ => FlushPendingCsvEntries(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _logger.LogInformation("Started FileCacheManager");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _csvFlushTimer?.Dispose();
        _csvFlushTimer = null;
        WriteOutFullCsv();
        return Task.CompletedTask;
    }
}
