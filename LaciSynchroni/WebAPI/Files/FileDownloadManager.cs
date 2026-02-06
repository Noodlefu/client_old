using Dalamud.Utility;
using K4os.Compression.LZ4.Legacy;
using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Dto.Files;
using LaciSynchroni.Common.Routes;
using LaciSynchroni.FileCache;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace LaciSynchroni.WebAPI.Files;

/// <summary>
/// Exception thrown when a server returns an empty (0 byte) file response.
/// This typically indicates the file is missing or unavailable on the CDN.
/// </summary>
public sealed class EmptyFileResponseException(string url, long? contentLength) : Exception($"Server returned empty file (0 bytes) for {url}. Content-Length: {contentLength?.ToString() ?? "not set"}")
{
    public string Url { get; } = url;
    public long? ContentLength { get; } = contentLength;
}

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private const int MaxRetryAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    /// <summary>Timeout for read operations - if no data received within this time, the download is considered stalled.</summary>
    private static readonly TimeSpan ReadActivityTimeout = TimeSpan.FromSeconds(15);

    /// <summary>XOR key used for data munging/obfuscation.</summary>
    private const byte MungeXorKey = 42;
    /// <summary>Buffer size for small downloads (under 1MB).</summary>
    private const int SmallDownloadBufferSize = 8196;
    /// <summary>Buffer size for large downloads (1MB or more).</summary>
    private const int LargeDownloadBufferSize = 65536;
    /// <summary>Threshold in bytes above which large buffer is used.</summary>
    private const long LargeDownloadThreshold = 1024 * 1024;
    /// <summary>Timeout for polling download ready status.</summary>
    private static readonly TimeSpan DownloadReadyPollTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Delay between download ready polls.</summary>
    private static readonly TimeSpan DownloadReadyPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly Lock _downloadStatusLock = new();
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly List<ThrottledStream> _activeDownloadStreams;
    private readonly Lock _activeDownloadStreamsLock = new();
    private readonly SemaphoreSlim _decompressGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    public FileDownloadManager(ILogger<FileDownloadManager> logger, SyncMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _downloadStatus = new Dictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _serverManager = serverManager;
        _activeDownloadStreams = [];

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            try
            {
                List<ThrottledStream> streams;
                lock (_activeDownloadStreamsLock)
                {
                    streams = [.. _activeDownloadStreams];
                }
                if (streams.Count == 0) return;
                var newLimit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
                foreach (var stream in streams)
                {
                    stream.BandwidthLimit = newLimit;
                }
            }
            catch (Exception ex)
            {
                Logger.LogTrace(ex, "Error updating download speed limit");
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public bool IsDownloading => CurrentDownloads.Count > 0;

    public static void MungeBuffer(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; ++i)
        {
            buffer[i] ^= MungeXorKey;
        }
    }

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        lock (_downloadStatusLock)
        {
            _downloadStatus.Clear();
        }
    }

    private void SetDownloadStatus(string key, DownloadStatus status)
    {
        lock (_downloadStatusLock)
        {
            if (_downloadStatus.TryGetValue(key, out var value))
                value.DownloadStatus = status;
        }
    }

    private void AddTransferredBytes(string key, long bytes)
    {
        lock (_downloadStatusLock)
        {
            if (_downloadStatus.TryGetValue(key, out var value))
                value.TransferredBytes += bytes;
        }
    }

    private void SetTransferredFiles(string key, int files)
    {
        lock (_downloadStatusLock)
        {
            if (_downloadStatus.TryGetValue(key, out var value))
                value.TransferredFiles = files;
        }
    }

    public async Task DownloadFiles(int serverIndex, GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternal(serverIndex, gameObject, fileReplacementDto, ct).ConfigureAwait(false);
            await DirectDownloadFilesInternal(serverIndex, gameObject, fileReplacementDto, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            Mediator.Publish(new DownloadFinishedMessage(gameObject));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        List<ThrottledStream> streamsToDispose;
        lock (_activeDownloadStreamsLock)
        {
            streamsToDispose = [.. _activeDownloadStreams];
        }
        foreach (var stream in streamsToDispose)
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
                //
            }
        }
        base.Dispose(disposing);
    }

    private static byte MungeByte(int byteOrEof)
    {
        if (byteOrEof == -1)
        {
            throw new EndOfStreamException();
        }

        return (byte)(byteOrEof ^ MungeXorKey);
    }

    private static (string fileHash, long fileLengthBytes) ReadBlockFileHeader(FileStream fileBlockStream)
    {
        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = (char)MungeByte(fileBlockStream.ReadByte());
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            int readByte = fileBlockStream.ReadByte();
            if (readByte == -1)
                throw new EndOfStreamException();

            var readChar = (char)MungeByte(readByte);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash) hashName.Add(readChar);
            else fileLength.Add(readChar);
        }
        return (new string([.. hashName]), long.Parse(new string([.. fileLength])));
    }

    private async Task DownloadAndMungeFileHttpClientWithRetry(int serverIndex, string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, string tempPath, IProgress<long> progress, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                await DownloadAndMungeFileHttpClient(serverIndex, downloadGroup, requestId, fileTransfer, tempPath, progress, ct).ConfigureAwait(false);
                return; // Success
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (InvalidDataException)
            {
                throw; // Don't retry on 404/401 errors
            }
            catch (EmptyFileResponseException)
            {
                throw; // Don't retry when server returns empty file - it's not going to change
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (attempt < MaxRetryAttempts - 1)
                {
                    Logger.LogWarning(ex, "Download attempt {Attempt}/{MaxAttempts} failed for request {RequestId}, retrying in {Delay}...",
                        attempt + 1, MaxRetryAttempts, requestId, RetryDelays[attempt]);

                    await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                }
            }
        }

        // All retries exhausted
        throw new IOException($"Download failed after {MaxRetryAttempts} attempts for request {requestId}", lastException);
    }

    private async Task DownloadAndMungeFileHttpClient(int serverIndex, string downloadGroup, Guid requestId, List<DownloadFileTransfer> fileTransfer, string tempPath, IProgress<long> progress, CancellationToken ct)
    {
        Logger.LogDebug("GUID {requestId} on server {uri} for files {files}", requestId, fileTransfer[0].DownloadUri, string.Join(", ", fileTransfer.Select(c => c.Hash)));

        await WaitForDownloadReady(serverIndex, fileTransfer, requestId, ct).ConfigureAwait(false);

        _downloadStatus[downloadGroup].DownloadStatus = DownloadStatus.Downloading;

        HttpResponseMessage response = null!;
        var requestUrl = FilesRoutes.CacheGetFullPath(fileTransfer[0].DownloadUri, requestId);

        Logger.LogDebug("Downloading {requestUrl} for request {id}", requestUrl, requestId);
        try
        {
            response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {requestUrl}, HttpStatusCode: {code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(tempPath);
            await using (fileStream.ConfigureAwait(false))
            {
                var bufferSize = response.Content.Headers.ContentLength > LargeDownloadThreshold ? LargeDownloadBufferSize : SmallDownloadBufferSize;
                var buffer = new byte[bufferSize];

                var bytesRead = 0;
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download of {id} with a speed limit of {limit} to {tempPath}", requestId, limit, tempPath);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                lock (_activeDownloadStreamsLock)
                {
                    _activeDownloadStreams.Add(stream);
                }
                using var timeoutCts = new CancellationTokenSource(ReadActivityTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                while (true)
                {
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"Download stalled - no data received for {ReadActivityTimeout.TotalSeconds} seconds");
                    }

                    if (bytesRead == 0) break;

                    // Reset timeout for next read
                    timeoutCts.TryReset();
                    timeoutCts.CancelAfter(ReadActivityTimeout);

                    MungeBuffer(buffer.AsSpan(0, bytesRead));

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                }

                Logger.LogDebug("{requestUrl} downloaded to {tempPath}", requestUrl, tempPath);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!tempPath.IsNullOrEmpty())
                    File.Delete(tempPath);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                lock (_activeDownloadStreamsLock)
                {
                    _activeDownloadStreams.Remove(stream);
                }
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            response?.Dispose();
        }
    }

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(int serverIndex, GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(serverIndex, [.. fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal)], ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            _orchestrator.TryAddForbiddenTransfer(new DownloadFileTransfer(dto, serverIndex));
        }

        CurrentDownloads = [.. downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d, serverIndex)).Where(d => d.CanBeTransferred)];

        return CurrentDownloads;
    }

    /// <summary>
    /// Extracts and decompresses files from a downloaded block file.
    /// </summary>
    private async Task ExtractBlockFile(string blockFile, List<FileReplacementData> fileReplacement, string downloadName)
    {
        try
        {
            var fileBlockStream = File.OpenRead(blockFile);
            await using (fileBlockStream.ConfigureAwait(false))
            {
                while (fileBlockStream.Position < fileBlockStream.Length)
                {
                    (string fileHash, long fileLengthBytes) = ReadBlockFileHeader(fileBlockStream);

                    try
                    {
                        var fileExtension = fileReplacement.First(f => string.Equals(f.Hash, fileHash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                        var filePath = _fileDbManager.GetCacheFilePath(fileHash, fileExtension);
                        Logger.LogDebug("{dlName}: Decompressing {file}:{le} => {dest}", downloadName, fileHash, fileLengthBytes, filePath);

                        byte[] compressedFileContent = new byte[fileLengthBytes];
                        var readBytes = await fileBlockStream.ReadAsync(compressedFileContent, CancellationToken.None).ConfigureAwait(false);
                        if (readBytes != fileLengthBytes)
                        {
                            throw new EndOfStreamException();
                        }
                        MungeBuffer(compressedFileContent);

                        await DecompressAndWriteFile(fileHash, filePath, compressedFileContent, downloadName).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException ex)
                    {
                        Logger.LogWarning(ex, "{dlName}: Failure to extract file {fileHash}, stream ended prematurely", downloadName, fileHash);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning(e, "{dlName}: Error during decompression", downloadName);
                    }
                }
            }
        }
        catch (EndOfStreamException ex)
        {
            Logger.LogDebug(ex, "{dlName}: Failure to extract file header data, stream ended", downloadName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{dlName}: Error during block file read", downloadName);
        }
        finally
        {
            File.Delete(blockFile);
        }
    }

    /// <summary>
    /// Decompresses data and writes it to a file, with proper gating to prevent CPU exhaustion.
    /// Uses centralized file acquisition to prevent concurrent writes to the same file hash.
    /// </summary>
    /// <param name="fileHash">The hash of the file</param>
    /// <param name="filePath">The destination file path</param>
    /// <param name="compressedData">The compressed data to decompress and write</param>
    /// <param name="downloadName">Name for logging purposes</param>
    /// <param name="alreadyAcquired">If true, skips AcquireFileAsync (caller already holds the lock)</param>
    private async Task DecompressAndWriteFile(string fileHash, string filePath, byte[] compressedData, string downloadName, bool alreadyAcquired = false)
    {
        if (!alreadyAcquired)
        {
            // Use centralized coordination to prevent duplicate downloads/writes
            var (shouldDownload, existingPath) = await _fileDbManager.AcquireFileAsync(fileHash, CancellationToken.None).ConfigureAwait(false);

            if (!shouldDownload)
            {
                Logger.LogDebug("{dlName}: File {fileHash} already available at {existingPath}, skipping write", downloadName, fileHash, existingPath);
                return;
            }
        }

        try
        {
            // Gate decompression to prevent CPU exhaustion
            // Decompression is completed fully before releasing the gate to ensure
            // we don't lose decompressed data if file write fails
            byte[] decompressedFile;
            await _decompressGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (compressedData.Length == 0)
                {
                    throw new InvalidDataException("Downloaded file is empty (0 bytes)");
                }

                try
                {
                    decompressedFile = LZ4Wrapper.Unwrap(compressedData);
                }
                catch (Exception lz4Ex)
                {
                    // Log details about the failed data for debugging
                    var preview = compressedData.Length > 32 ? Convert.ToHexString(compressedData.AsSpan(0, 32)) : Convert.ToHexString(compressedData);
                    Logger.LogWarning(lz4Ex, "{dlName}: LZ4 decompression failed for {fileHash}. Data length: {len}, First bytes: {preview}",
                        downloadName, fileHash, compressedData.Length, preview);
                    throw;
                }
            }
            finally
            {
                _decompressGate.Release();
            }

            // Write decompressed file outside of the gate to not block other decompressions
            try
            {
                await _fileCompactor.WriteAllBytesAsync(filePath, decompressedFile, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                Logger.LogWarning(writeEx, "{dlName}: Failed to write decompressed file {fileHash} to {filePath}", downloadName, fileHash, filePath);
                // Clean up partial file if it exists
                try { File.Delete(filePath); } catch { /* ignore cleanup errors */ }
                throw;
            }

            PersistFileToStorage(fileHash, filePath);

            // Signal successful completion to any waiters
            _fileDbManager.CompleteFileDownload(fileHash, filePath);
        }
        catch (Exception ex)
        {
            // Signal failure to any waiters
            _fileDbManager.FailFileDownload(fileHash, ex);
            throw;
        }
    }

    private async Task DownloadFilesInternal(int serverIndex, GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        var downloadGroups = CurrentDownloads.Where(p => !p.IsDirectDownload).GroupBy(f => $"{f.DownloadUri.Host}:{f.DownloadUri.Port}", StringComparer.Ordinal);

        if (!downloadGroups.Any()) return;

        foreach (var downloadGroup in downloadGroups)
        {
            lock (_downloadStatusLock)
            {
                _downloadStatus[downloadGroup.Key] = new FileDownloadStatus()
                {
                    DownloadStatus = DownloadStatus.Initializing,
                    TotalBytes = downloadGroup.Sum(c => c.Total),
                    TotalFiles = 1,
                    TransferredBytes = 0,
                    TransferredFiles = 0,
                };
            }
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        await Parallel.ForEachAsync(downloadGroups, new ParallelOptions()
        {
            // Don't limit parallelism here - let the semaphore in AcquireDownloadSlotAsync control it.
            // This allows all groups to reach "WaitingForSlot" state and be ready to start immediately
            // when a slot becomes available, rather than waiting for Parallel.ForEachAsync to schedule them.
            CancellationToken = ct,
        },
        async (fileGroup, token) =>
        {
            // Acquire download slot BEFORE telling server to prepare files.
            // This prevents the server from timing out prepared downloads while we wait for a slot.
            SetDownloadStatus(fileGroup.Key, DownloadStatus.WaitingForSlot);
            var slotLease = await _orchestrator.AcquireDownloadSlotAsync(token).ConfigureAwait(false);
            await using (slotLease.ConfigureAwait(false))
            {
                // Now tell server to prepare files
                var requestIdResponse = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Post, FilesRoutes.RequestEnqueueFullPath(fileGroup.First().DownloadUri),
                    fileGroup.Select(c => c.Hash), token).ConfigureAwait(false);
                Logger.LogDebug("Sent request for {n} files on server {uri} with result {result}", fileGroup.Count(), fileGroup.First().DownloadUri,
                    await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false));

                Guid requestId = Guid.Parse((await requestIdResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim('"'));

                Logger.LogDebug("GUID {requestId} for {n} files on server {uri}", requestId, fileGroup.Count(), fileGroup.First().DownloadUri);

                var blockFile = _fileDbManager.GetCacheFilePath(requestId.ToString("N"), "blk");
                FileInfo fi = new(blockFile);

                try
                {
                    SetDownloadStatus(fileGroup.Key, DownloadStatus.WaitingForQueue);
                    Progress<long> progress = new((bytesDownloaded) =>
                    {
                        try
                        {
                            AddTransferredBytes(fileGroup.Key, bytesDownloaded);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Could not set download progress");
                        }
                    });
                    await DownloadAndMungeFileHttpClientWithRetry(serverIndex, fileGroup.Key, requestId, [.. fileGroup], blockFile, progress, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("{dlName}: Detected cancellation of download, partially extracting files for {id}", fi.Name, gameObjectHandler);
                }
                catch (Exception ex)
                {
                    File.Delete(blockFile);
                    Logger.LogError(ex, "{dlName}: Error during download of {id}", fi.Name, requestId);
                    ClearDownload();
                    return;
                }

                SetTransferredFiles(fileGroup.Key, 1);
                SetDownloadStatus(fileGroup.Key, DownloadStatus.Decompressing);
                await ExtractBlockFile(blockFile, fileReplacement, fi.Name).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task DirectDownloadFilesInternal(int serverIndex, GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        // Separate out the files with direct download URLs
        var directDownloads = CurrentDownloads.Where(download => download.IsDirectDownload && !string.IsNullOrEmpty(download.DownloadUri.AbsoluteUri)).ToList();
        if (directDownloads.Count == 0)
            return;

        // Create download status trackers for the direct downloads
        foreach (var directDownload in directDownloads)
        {
            lock (_downloadStatusLock)
            {
                _downloadStatus[directDownload.DownloadUri.AbsoluteUri!] = new FileDownloadStatus()
                {
                    DownloadStatus = DownloadStatus.Initializing,
                    TotalBytes = directDownload.Total,
                    TotalFiles = 1,
                    TransferredBytes = 0,
                    TransferredFiles = 0
                };
            }
        }

        Logger.LogInformation("Downloading {Direct} files directly.", directDownloads.Count);
        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        // Start downloading each of the direct downloads
        var directDownloadsTask = Parallel.ForEachAsync(directDownloads, new ParallelOptions()
        {
            // Don't limit parallelism here - let the semaphore in AcquireDownloadSlotAsync control it.
            CancellationToken = ct,
        },
        async (directDownload, token) =>
        {
            var directDownloadAbsoluteUri = directDownload.DownloadUri.AbsoluteUri;
            bool hasStatus;
            lock (_downloadStatusLock)
            {
                hasStatus = _downloadStatus.ContainsKey(directDownloadAbsoluteUri);
            }
            if (!hasStatus)
                return;

            // Acquire the right to download this file - prevents duplicate downloads when multiple
            // characters need the same file. If another download is in progress, we wait for it.
            // If that download cancels but we're still interested, we take over the download.
            var (shouldDownload, existingPath) = await _fileDbManager.AcquireFileAsync(directDownload.Hash, token).ConfigureAwait(false);
            if (!shouldDownload)
            {
                Logger.LogDebug("{Hash}: File already available at {Path}, skipping download", directDownload.Hash, existingPath);
                SetTransferredFiles(directDownloadAbsoluteUri, 1);
                SetDownloadStatus(directDownloadAbsoluteUri, DownloadStatus.Decompressing);
                return;
            }

            // We're responsible for downloading this file - ensure we always signal completion or failure
            string? tempFilename = null;
            try
            {
                var fileExtension = fileReplacement.First(f => string.Equals(f.Hash, directDownload.Hash, StringComparison.OrdinalIgnoreCase)).GamePaths[0].Split(".")[^1];
                var finalFilename = _fileDbManager.GetCacheFilePath(directDownload.Hash, fileExtension);
                // Use a unique temp filename per download attempt to avoid file locking issues
                tempFilename = _fileDbManager.GetCacheFilePath($"{directDownload.Hash}_{Guid.NewGuid():N}", "bin");

                Progress<long> progress = new((bytesDownloaded) =>
                {
                    try
                    {
                        AddTransferredBytes(directDownloadAbsoluteUri, bytesDownloaded);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Could not set download progress");
                    }
                });

                // Use slot lease pattern to ensure slot is always released
                SetDownloadStatus(directDownloadAbsoluteUri, DownloadStatus.WaitingForSlot);
                var slotLease = await _orchestrator.AcquireDownloadSlotAsync(token).ConfigureAwait(false);
                await using (slotLease.ConfigureAwait(false))
                {
                    // Download the compressed file directly with retry logic
                    SetDownloadStatus(directDownloadAbsoluteUri, DownloadStatus.Downloading);
                    Logger.LogDebug("{Hash} Beginning direct download of file from {Url}", directDownload.Hash, directDownloadAbsoluteUri);
                    await DownloadFileThrottledWithRetry(serverIndex, directDownload.DownloadUri, tempFilename, progress, token).ConfigureAwait(false);

                    SetTransferredFiles(directDownloadAbsoluteUri, 1);
                    SetDownloadStatus(directDownloadAbsoluteUri, DownloadStatus.Decompressing);

                    Logger.LogDebug("Decompressing direct download {Hash} from {CompressedFile} to {FinalFile}", directDownload.Hash, tempFilename, finalFilename);
                    byte[] compressedBytes = await File.ReadAllBytesAsync(tempFilename, token).ConfigureAwait(false);

                    // DecompressAndWriteFile handles calling CompleteFileDownload/FailFileDownload
                    // Pass alreadyAcquired: true since we already called AcquireFileAsync before downloading
                    await DecompressAndWriteFile(directDownload.Hash, finalFilename, compressedBytes, "DirectDownload", alreadyAcquired: true).ConfigureAwait(false);
                    Logger.LogDebug("Finished direct download of {Hash}.", directDownload.Hash);
                }
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogDebug(ex, "{Hash}: Detected cancellation of direct download, discarding file.", directDownload.Hash);
                // Signal cancellation so waiters can potentially take over the download
                _fileDbManager.FailFileDownload(directDownload.Hash, ex);
                ClearDownload();
            }
            catch (EmptyFileResponseException ex)
            {
                // Server returned empty file - this file is unavailable on the CDN
                // Add to forbidden transfers so we don't keep trying, and log once at Info level
                Logger.LogInformation(ex, "{Hash}: File unavailable on server (empty response from {Url}). Skipping this file.", directDownload.Hash, ex.Url);
                _orchestrator.TryAddForbiddenTransfer(directDownload);
                _fileDbManager.FailFileDownload(directDownload.Hash, ex);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Hash}: Error during direct download.", directDownload.Hash);
                _fileDbManager.FailFileDownload(directDownload.Hash, ex);
                ClearDownload();
            }
            finally
            {
                // Clean up temp file if it still exists
                if (tempFilename != null)
                {
                    try { File.Delete(tempFilename); } catch { /* ignore */ }
                }
            }
        });

        // Wait for all the batches and direct downloads to complete
        await directDownloadsTask.ConfigureAwait(false);

        Logger.LogDebug("Download end: {Id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task DownloadFileThrottledWithRetry(int serverIndex, Uri requestUrl, string destinationFilename, IProgress<long> progress, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            try
            {
                await DownloadFileThrottled(serverIndex, requestUrl, destinationFilename, progress, ct).ConfigureAwait(false);
                return; // Success
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (InvalidDataException)
            {
                throw; // Don't retry on 404/401 errors
            }
            catch (EmptyFileResponseException)
            {
                throw; // Don't retry when server returns empty file - it's not going to change
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (attempt < MaxRetryAttempts - 1)
                {
                    Logger.LogWarning(ex, "Download attempt {Attempt}/{MaxAttempts} failed for {Url}, retrying in {Delay}...",
                        attempt + 1, MaxRetryAttempts, requestUrl, RetryDelays[attempt]);

                    await Task.Delay(RetryDelays[attempt], ct).ConfigureAwait(false);
                }
            }
        }

        // All retries exhausted
        throw new IOException($"Download failed after {MaxRetryAttempts} attempts: {requestUrl}", lastException);
    }

    private async Task DownloadFileThrottled(int serverIndex, Uri requestUrl, string destinationFilename, IProgress<long> progress, CancellationToken ct)
    {
        HttpResponseMessage response = null!;
        try
        {
            response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, requestUrl, ct, HttpCompletionOption.ResponseHeadersRead, withAuthToken: false).ConfigureAwait(false);

            var headersBuilder = new StringBuilder();
            if (response.RequestMessage != null)
            {
                headersBuilder.AppendLine("DefaultRequestHeaders:");
                foreach (var header in _orchestrator.DefaultRequestHeaders)
                {
                    foreach (var value in header.Value)
                    {
                        headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                    }
                }
                headersBuilder.AppendLine("RequestMessage.Headers:");
                foreach (var header in response.RequestMessage.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                    }
                }
                if (response.RequestMessage.Content != null)
                {
                    headersBuilder.AppendLine("RequestMessage.Content.Headers:");
                    foreach (var header in response.RequestMessage.Content.Headers)
                    {
                        foreach (var value in header.Value)
                        {
                            headersBuilder.AppendLine($"\"{header.Key}\": \"{value}\"");
                        }
                    }
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dump some helpful debugging info
                string responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Logger.LogWarning("Unsuccessful status code for {RequestUrl} is {StatusCode}, request headers: \n{Headers}\n, response text: \n\"{ResponseText}\"", requestUrl, response.StatusCode, headersBuilder.ToString(), responseText);

                // Raise an exception etc
                response.EnsureSuccessStatusCode();
            }
            else
            {
                Logger.LogDebug("Successful response for {RequestUrl} is {StatusCode}, request headers: \n{Headers}", requestUrl, response.StatusCode, headersBuilder.ToString());
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex, "Error during download of {RequestUrl}, HttpStatusCode: {Code}", requestUrl, ex.StatusCode);
            if (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                throw new InvalidDataException($"Http error {ex.StatusCode} (cancelled: {ct.IsCancellationRequested}): {requestUrl}", ex);
            }

            // Re-throw so retry logic can handle it
            throw;
        }

        ThrottledStream? stream = null;
        try
        {
            var fileStream = File.Create(destinationFilename);
            await using (fileStream.ConfigureAwait(false))
            {
                var contentLength = response.Content.Headers.ContentLength;
                var bufferSize = contentLength > LargeDownloadThreshold ? LargeDownloadBufferSize : SmallDownloadBufferSize;
                var buffer = new byte[bufferSize];

                if (contentLength == 0)
                {
                    Logger.LogDebug("Server returned Content-Length: 0 for {RequestUrl}", requestUrl);
                }

                var bytesRead = 0;
                long totalBytesRead = 0;
                var limit = _orchestrator.DownloadLimitPerSlot();
                Logger.LogTrace("Starting Download with a speed limit of {Limit} to {TempPath}, Content-Length: {ContentLength}", limit, destinationFilename, contentLength);
                stream = new ThrottledStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), limit);
                lock (_activeDownloadStreamsLock)
                {
                    _activeDownloadStreams.Add(stream);
                }

                using var timeoutCts = new CancellationTokenSource(ReadActivityTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                while (true)
                {
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"Download stalled - no data received for {ReadActivityTimeout.TotalSeconds} seconds");
                    }

                    if (bytesRead == 0) break;

                    totalBytesRead += bytesRead;

                    // Reset timeout for next read
                    timeoutCts.TryReset();
                    timeoutCts.CancelAfter(ReadActivityTimeout);

                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);

                    progress.Report(bytesRead);
                }

                if (totalBytesRead == 0)
                {
                    throw new EmptyFileResponseException(requestUrl.ToString(), contentLength);
                }

                Logger.LogDebug("{RequestUrl} downloaded to {TempPath}, {TotalBytes} bytes", requestUrl, destinationFilename, totalBytesRead);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!destinationFilename.IsNullOrEmpty())
                    File.Delete(destinationFilename);
            }
            catch
            {
                // ignore if file deletion fails
            }
            throw;
        }
        finally
        {
            if (stream != null)
            {
                lock (_activeDownloadStreamsLock)
                {
                    _activeDownloadStreams.Remove(stream);
                }
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            response?.Dispose();
        }
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(int serverIndex, List<string> hashes, CancellationToken ct)
    {
        var fileCdnUri = _orchestrator.GetFileCdnUri(serverIndex) ?? throw new InvalidOperationException("FileTransferManager is not initialized");
        var route = _serverManager.GetServerByIndex(serverIndex).UsesTimeZone()
            ? FilesRoutes.ServerFilesGetSizesFullPath(fileCdnUri, LongitudinalRegion.OffsetFromLocalSystemTimeZone())
            : FilesRoutes.ServerFilesGetSizesFullPath(fileCdnUri);

        using var response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, route, hashes, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<DownloadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private void PersistFileToStorage(string fileHash, string filePath)
    {
        var fi = new FileInfo(filePath);

        static DateTime RandomDayInThePast()
        {
            DateTime start = new(1995, 1, 1, 1, 1, 1, DateTimeKind.Local);
            int range = (DateTime.Today - start).Days;
            return start.AddDays(Random.Shared.Next(range));
        }

        fi.CreationTime = RandomDayInThePast();
        fi.LastAccessTime = DateTime.Today;
        fi.LastWriteTime = RandomDayInThePast();
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath);
            if (entry == null)
            {
                Logger.LogError("Failed to create cache entry for downloaded file {filePath} with hash {hash} - file will be re-downloaded on next sync", filePath, fileHash);
            }
            else if (!string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError("Hash mismatch after extracting, got {hash}, expected {expectedHash}, deleting file", entry.Hash, fileHash);
                File.Delete(filePath);
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry for {filePath}", filePath);
        }
    }

    private async Task WaitForDownloadReady(int serverIndex, List<DownloadFileTransfer> downloadFileTransfer, Guid requestId, CancellationToken downloadCt)
    {
        bool alreadyCancelled = false;
        CancellationTokenSource? localTimeoutCts = null;
        CancellationTokenSource? composite = null;
        try
        {
            localTimeoutCts = new();
            localTimeoutCts.CancelAfter(DownloadReadyPollTimeout);
            composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);

            while (!_orchestrator.IsDownloadReady(requestId))
            {
                try
                {
                    await Task.Delay(DownloadReadyPollInterval, composite.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    if (downloadCt.IsCancellationRequested) throw;

                    var req = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, FilesRoutes.RequestCheckQueueFullPath(downloadFileTransfer[0].DownloadUri, requestId),
                        downloadFileTransfer.Select(c => c.Hash).ToList(), downloadCt).ConfigureAwait(false);
                    req.EnsureSuccessStatusCode();
                    localTimeoutCts.Dispose();
                    composite.Dispose();
                    localTimeoutCts = new();
                    localTimeoutCts.CancelAfter(DownloadReadyPollTimeout);
                    composite = CancellationTokenSource.CreateLinkedTokenSource(downloadCt, localTimeoutCts.Token);
                }
            }

            Logger.LogDebug("Download {requestId} ready", requestId);
        }
        catch (TaskCanceledException)
        {
            try
            {
                await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, FilesRoutes.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                alreadyCancelled = true;
            }
            catch
            {
                // ignore whatever happens here
            }

            throw;
        }
        finally
        {
            if (downloadCt.IsCancellationRequested && !alreadyCancelled)
            {
                try
                {
                    await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Get, FilesRoutes.RequestCancelFullPath(downloadFileTransfer[0].DownloadUri, requestId)).ConfigureAwait(false);
                }
                catch
                {
                    // ignore whatever happens here
                }
            }
            localTimeoutCts?.Dispose();
            composite?.Dispose();
            _orchestrator.ClearDownloadRequest(requestId);
        }
    }
}
