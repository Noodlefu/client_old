using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Dto.Files;
using LaciSynchroni.Common.Routes;
using LaciSynchroni.FileCache;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.UI;
using LaciSynchroni.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;

namespace LaciSynchroni.WebAPI.Files;

using ServerIndex = int;

/// <summary>
/// Represents a compressed file ready for upload.
/// </summary>
internal sealed record CompressedUploadItem(string Hash, byte[] Data);

public sealed class FileUploadManager : DisposableMediatorSubscriberBase
{
    /// <summary>
    /// Number of files to compress ahead of uploads (pipeline depth).
    /// </summary>
    private const int CompressionPipelineDepth = 2;

    /// <summary>
    /// Duration after which a verified upload hash expires and needs re-verification.
    /// </summary>
    private static readonly TimeSpan VerifiedHashExpiration = TimeSpan.FromMinutes(10);

    private readonly FileCacheManager _fileDbManager;
    private readonly SyncConfigService _syncConfigService;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly ServerConfigurationManager _serverManager;
    private readonly ConcurrentDictionary<ServerIndex, ConcurrentDictionary<string, DateTime>> _verifiedUploadedHashesByServer = new();
    /// <summary>
    /// One Cancellation token per server, since we can concurrently upload to each server connected.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, CancellationTokenSource> _cancellationTokens = new();
    /// <summary>
    /// Per-server upload tracking to prevent race conditions during concurrent uploads.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, ConcurrentDictionary<string, FileTransfer>> _currentUploads = new();

    public FileUploadManager(ILogger<FileUploadManager> logger, SyncMediator mediator,
        SyncConfigService syncConfigService,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileDbManager,
        ServerConfigurationManager serverManager) : base(logger, mediator)
    {
        _syncConfigService = syncConfigService;
        _orchestrator = orchestrator;
        _fileDbManager = fileDbManager;
        _serverManager = serverManager;

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            ResetForServer(msg.ServerIndex);
        });
    }

    /// <summary>
    /// Returns all uploads across all servers. Thread-safe aggregate view.
    /// </summary>
    public List<FileTransfer> CurrentUploads => _currentUploads.Values.SelectMany(x => x.Values).ToList();

    /// <summary>
    /// Gets the upload list for a specific server. Returns a new empty list if none exists.
    /// </summary>
    private ConcurrentDictionary<string, FileTransfer> GetServerUploads(ServerIndex serverIndex)
    {
        return _currentUploads.GetOrAdd(serverIndex, _ => new(StringComparer.Ordinal));
    }

    private ConcurrentDictionary<string, DateTime> GetVerifiedHashes(ServerIndex serverIndex)
    {
        return _verifiedUploadedHashesByServer.GetOrAdd(serverIndex, _ => new(StringComparer.Ordinal));
    }

    public async Task DeleteAllFiles(int serverIndex)
    {
        var uri = RequireUriForServer(serverIndex);

        await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesDeleteAllFullPath(uri)).ConfigureAwait(false);
    }

    public async Task<List<string>> UploadFiles(int serverIndex, List<string> hashesToUpload, IProgress<string> progress, CancellationToken ct)
    {
        Logger.LogDebug("Trying to upload files");
        var filesPresentLocally = hashesToUpload.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);
        var locallyMissingFiles = hashesToUpload.Except(filesPresentLocally, StringComparer.Ordinal).ToList();
        if (locallyMissingFiles.Any())
        {
            return locallyMissingFiles;
        }

        progress.Report($"Starting upload for {filesPresentLocally.Count} files");

        var filesToUpload = await FilesSend(serverIndex, [.. filesPresentLocally], [], ct).ConfigureAwait(false);

        if (filesToUpload.Exists(f => f.IsForbidden))
        {
            return [.. filesToUpload.Where(f => f.IsForbidden).Select(f => f.Hash)];
        }

        await UploadFilesWithPipeline(serverIndex, filesToUpload, progress, false, ct).ConfigureAwait(false);

        return [];
    }

    /// <summary>
    /// Uploads files using a producer-consumer pipeline where compression runs ahead of uploads.
    /// </summary>
    private async Task UploadFilesWithPipeline(int serverIndex, List<UploadFileDto> filesToUpload, IProgress<string>? progress, bool postProgress, CancellationToken ct)
    {
        if (filesToUpload.Count == 0) return;

        var channel = Channel.CreateBounded<CompressedUploadItem>(new BoundedChannelOptions(CompressionPipelineDepth)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,  // Allow multiple concurrent consumers for parallel uploads
            SingleWriter = true    // Keep single producer for ordered compression
        });

        var uploadCount = filesToUpload.Count;
        var uploadedCount = 0;

        // Producer: compress files and write to channel
        var producerTask = Task.Run(async () =>
        {
            try
            {
                foreach (var file in filesToUpload)
                {
                    ct.ThrowIfCancellationRequested();
                    Logger.LogDebug("[{hash}] Compressing (pipeline)", file.Hash);
                    var data = await _fileDbManager.GetCompressedFileData(file.Hash, ct).ConfigureAwait(false);

                    var serverUploads = GetServerUploads(serverIndex);
                    if (serverUploads.TryGetValue(data.Item1, out var uploadTransfer))
                    {
                        uploadTransfer.Total = data.Item2.Length;
                    }

                    await channel.Writer.WriteAsync(new CompressedUploadItem(data.Item1, data.Item2), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        // Consumer: read compressed items and upload
        var consumerTask = Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                uploadedCount++;
                progress?.Report($"Uploading file {uploadedCount}/{uploadCount}. Please wait until the upload is completed.");

                Logger.LogDebug("[{hash}] Starting upload for {filePath}", item.Hash, _fileDbManager.GetFileCacheByHash(item.Hash)?.ResolvedFilepath ?? "unknown");
                await UploadFile(serverIndex, item.Data, item.Hash, postProgress, ct).ConfigureAwait(false);
            }
        }, ct);

        await Task.WhenAll(producerTask, consumerTask).ConfigureAwait(false);
    }

    public async Task<CharacterData> UploadFiles(ServerIndex serverIndex, CharacterData data, List<UserData> visiblePlayers)
    {
        CancelUpload(serverIndex);

        var tokenSource = new CancellationTokenSource();
        if (!_cancellationTokens.TryAdd(serverIndex, tokenSource))
        {
            Logger.LogError("[{ServerIndex} Failed to add cancellation token, token already present.", serverIndex);
        }
        var uploadToken = tokenSource.Token;
        Logger.LogDebug("Sending Character data {Hash} to service {Url}", data.DataHash.Value, _serverManager.GetServerByIndex(serverIndex).ServerUri);

        HashSet<string> unverifiedUploads = GetUnverifiedFiles(serverIndex, data);
        if (unverifiedUploads.Any())
        {
            await UploadUnverifiedFiles(serverIndex, unverifiedUploads, visiblePlayers, uploadToken).ConfigureAwait(false);
            var serverName = _serverManager.GetServerByIndex(serverIndex).ServerName;
            Logger.LogInformation("Upload complete for {Hash} to {serverName}", data.DataHash.Value, serverName);
        }

        foreach (var kvp in data.FileReplacements)
        {
            data.FileReplacements[kvp.Key].RemoveAll(i => _orchestrator.IsForbidden(i.Hash));
        }

        return data;
    }

    private async Task<List<UploadFileDto>> FilesSend(int serverIndex, List<string> hashes, List<string> uids, CancellationToken ct)
    {
        var uri = RequireUriForServer(serverIndex);
        FilesSendDto filesSendDto = new()
        {
            FileHashes = hashes,
            UIDs = uids
        };
        using var response = await _orchestrator.SendRequestAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesFilesSendFullPath(uri), filesSendDto, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<UploadFileDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    private HashSet<string> GetUnverifiedFiles(ServerIndex serverIndex, CharacterData data)
    {
        HashSet<string> unverifiedUploadHashes = new(StringComparer.Ordinal);
        foreach (var item in data.FileReplacements.SelectMany(c => c.Value.Where(f => string.IsNullOrEmpty(f.FileSwapPath)).Select(v => v.Hash).Distinct(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList())
        {
            var verifiedHashes = GetVerifiedHashes(serverIndex);
            if (!verifiedHashes.TryGetValue(item, out var verifiedTime))
            {
                verifiedTime = DateTime.MinValue;
            }

            if (verifiedTime < DateTime.UtcNow.Subtract(VerifiedHashExpiration))
            {
                Logger.LogTrace("Verifying {item}, last verified: {date}", item, verifiedTime);
                unverifiedUploadHashes.Add(item);
            }
        }

        return unverifiedUploadHashes;
    }


    private async Task UploadFile(int serverIndex, byte[] compressedFile, string fileHash, bool postProgress, CancellationToken uploadToken)
    {
        var serverName = _serverManager.GetServerByIndex(serverIndex).ServerName;
        Logger.LogInformation("[{hash}] Uploading {size} to {serverName}", fileHash, UiSharedService.ByteToString(compressedFile.Length), serverName);

        if (uploadToken.IsCancellationRequested) return;

        try
        {
            await UploadFileStream(serverIndex, compressedFile, fileHash, _syncConfigService.Current.UseAlternativeFileUpload, postProgress, uploadToken).ConfigureAwait(false);
            GetVerifiedHashes(serverIndex)[fileHash] = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            if (!_syncConfigService.Current.UseAlternativeFileUpload && ex is not OperationCanceledException)
            {
                Logger.LogWarning(ex, "[{hash}] Error during file upload, trying alternative file upload", fileHash);
                await UploadFileStream(serverIndex, compressedFile, fileHash, munged: true, postProgress, uploadToken).ConfigureAwait(false);
            }
            else
            {
                Logger.LogWarning(ex, "[{hash}] File upload cancelled", fileHash);
            }
        }
    }

    private async Task UploadFileStream(int serverIndex, byte[] compressedFile, string fileHash, bool munged, bool postProgress, CancellationToken uploadToken)
    {
        var uri = RequireUriForServer(serverIndex);
        if (munged)
        {
            FileDownloadManager.MungeBuffer(compressedFile.AsSpan());
        }

        using var ms = new MemoryStream(compressedFile);

        Progress<UploadProgress>? prog = !postProgress ? null : new((prog) =>
        {
            try
            {
                var serverUploads = GetServerUploads(serverIndex);
                if (serverUploads.TryGetValue(fileHash, out var transfer))
                {
                    transfer.Transferred = prog.Uploaded;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{hash}] Could not set upload progress", fileHash);
            }
        });

        var streamContent = new ProgressableStreamContent(ms, prog);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        if (!munged)
            if (_serverManager.GetServerByIndex(serverIndex).UsesTimeZone())
                response = await _orchestrator.SendRequestStreamAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesUploadFullPath(uri, fileHash, LongitudinalRegion.OffsetFromLocalSystemTimeZone()), streamContent, uploadToken).ConfigureAwait(false);
            else
                response = await _orchestrator.SendRequestStreamAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesUploadFullPath(uri, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        else
            response = await _orchestrator.SendRequestStreamAsync(serverIndex, HttpMethod.Post, FilesRoutes.ServerFilesUploadMunged(uri, fileHash), streamContent, uploadToken).ConfigureAwait(false);
        Logger.LogDebug("[{hash}] Upload Status: {status}", fileHash, response.StatusCode);
        response.Dispose();
    }

    private async Task UploadUnverifiedFiles(int serverIndex, HashSet<string> unverifiedUploadHashes, List<UserData> visiblePlayers, CancellationToken uploadToken)
    {
        unverifiedUploadHashes = unverifiedUploadHashes.Where(h => _fileDbManager.GetFileCacheByHash(h) != null).ToHashSet(StringComparer.Ordinal);

        Logger.LogDebug("Verifying {count} files", unverifiedUploadHashes.Count);
        var filesToUpload = await FilesSend(serverIndex, [.. unverifiedUploadHashes], visiblePlayers.Select(p => p.UID).Distinct(StringComparer.Ordinal).ToList(), uploadToken).ConfigureAwait(false);

        var serverUploads = GetServerUploads(serverIndex);
        foreach (var file in filesToUpload.Where(f => !f.IsForbidden).DistinctBy(f => f.Hash))
        {
            try
            {
                var transfer = new UploadFileTransfer(file, serverIndex)
                {
                    Total = new FileInfo(_fileDbManager.GetFileCacheByHash(file.Hash)!.ResolvedFilepath).Length,
                };
                serverUploads.TryAdd(file.Hash, transfer);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Tried to request file {hash} but file was not present", file.Hash);
            }
        }

        foreach (var file in filesToUpload.Where(c => c.IsForbidden))
        {
            _orchestrator.TryAddForbiddenTransfer(new UploadFileTransfer(file, serverIndex)
            {
                LocalFile = _fileDbManager.GetFileCacheByHash(file.Hash)?.ResolvedFilepath ?? string.Empty,
            });

            GetVerifiedHashes(serverIndex)[file.Hash] = DateTime.UtcNow;
        }

        var totalSize = serverUploads.Values.Sum(c => c.Total);
        Logger.LogDebug("Compressing and uploading files using pipeline");

        // Get files that need to be uploaded
        var filesToProcess = serverUploads.Values
            .Where(f => f.CanBeTransferred && !f.IsTransferred)
            .Select(f => new UploadFileDto { Hash = f.Hash })
            .ToList();

        if (filesToProcess.Count > 0)
        {
            await UploadFilesWithPipeline(serverIndex, filesToProcess, null, true, uploadToken).ConfigureAwait(false);

            var compressedSize = serverUploads.Values.Sum(c => c.Total);
            var serverName = _serverManager.GetServerByIndex(serverIndex).ServerName;
            Logger.LogDebug("Upload complete to {serverName}, compressed {size} to {compressed}", serverName, UiSharedService.ByteToString(totalSize), UiSharedService.ByteToString(compressedSize));
        }

        foreach (var file in unverifiedUploadHashes.Where(c => !serverUploads.ContainsKey(c)))
        {
            GetVerifiedHashes(serverIndex)[file] = DateTime.UtcNow;
        }

        _currentUploads.TryRemove(serverIndex, out _);
    }

    private void CancelUpload(ServerIndex serverIndex)
    {
        CancelUploadsToServer(serverIndex);
    }

    private void CancelUploadsToServer(ServerIndex serverIndex)
    {
        if (_cancellationTokens.TryRemove(serverIndex, out var token))
        {
            token.Cancel();
            token.Dispose();
        }
        _currentUploads.TryRemove(serverIndex, out _);
    }

    private Uri RequireUriForServer(int serverIndex)
    {
        var uri = _orchestrator.GetFileCdnUri(serverIndex);
        if (uri == null) throw new InvalidOperationException("FileTransferManager is not initialized");
        return uri;
    }

    private void ResetForServer(ServerIndex serverIndex)
    {
        CancelUploadsToServer(serverIndex);
        _verifiedUploadedHashesByServer.TryRemove(serverIndex, out _);
    }

    private void Reset()
    {
        foreach (var c in _cancellationTokens.Values)
        {
            c.Cancel();
            c.Dispose();
        }
        _cancellationTokens.Clear();
        _currentUploads.Clear();
        _verifiedUploadedHashesByServer.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Reset();
    }
}
