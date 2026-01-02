using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace LaciSynchroni.WebAPI.Files;

using ServerIndex = int;

public class FileTransferOrchestrator : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<Guid, bool> _downloadReady = new();
    private readonly ConcurrentDictionary<ServerIndex, Uri> _cdnUris = new();
    private readonly HttpClient _httpClient;
    private readonly SyncConfigService _syncConfig;
    private readonly Lock _semaphoreModificationLock = new();
    private readonly MultiConnectTokenService _multiConnectTokenService;
    private int _availableDownloadSlots;
    private SemaphoreSlim _downloadSemaphore;
    private int CurrentlyUsedDownloadSlots => _availableDownloadSlots - _downloadSemaphore.CurrentCount;

    public HttpRequestHeaders DefaultRequestHeaders => _httpClient.DefaultRequestHeaders;

    public FileTransferOrchestrator(ILogger<FileTransferOrchestrator> logger, SyncConfigService syncConfig,
        SyncMediator mediator, HttpClient httpClient, MultiConnectTokenService multiConnectTokenService) : base(logger, mediator)
    {
        _syncConfig = syncConfig;
        _httpClient = httpClient;
        _multiConnectTokenService = multiConnectTokenService;
        var ver = Assembly.GetExecutingAssembly().GetName().Version!;
        var versionString = string.Create(CultureInfo.InvariantCulture, $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}");
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LaciSynchroni", versionString));

        _availableDownloadSlots = syncConfig.Current.ParallelDownloads;
        _downloadSemaphore = new(_availableDownloadSlots, _availableDownloadSlots);

        Mediator.Subscribe<ConnectedMessage>(this, (msg) =>
        {
            var newUri = msg.Connection.ServerInfo.FileServerAddress;
            _cdnUris.AddOrUpdate(msg.serverIndex, i => newUri, (i, uri) => newUri);
        });

        Mediator.Subscribe<DisconnectedMessage>(this, (msg) =>
        {
            _cdnUris.TryRemove(msg.ServerIndex, out _);
        });
        Mediator.Subscribe<DownloadReadyMessage>(this, (msg) =>
        {
            _downloadReady[msg.RequestId] = true;
        });
    }

    private readonly ConcurrentDictionary<string, FileTransfer> _forbiddenTransfers = new(StringComparer.Ordinal);

    public List<FileTransfer> ForbiddenTransfers => _forbiddenTransfers.Values.ToList();

    public bool IsForbidden(string hash) => _forbiddenTransfers.ContainsKey(hash);

    public bool TryAddForbiddenTransfer(FileTransfer transfer) => _forbiddenTransfers.TryAdd(transfer.Hash, transfer);

    public Uri? GetFileCdnUri(int serverIndex)
    {
        _cdnUris.TryGetValue(serverIndex, out var uri);
        return uri;
    }

    public void ClearDownloadRequest(Guid guid)
    {
        _downloadReady.Remove(guid, out _);
    }

    public bool IsDownloadReady(Guid guid)
    {
        if (_downloadReady.TryGetValue(guid, out bool isReady) && isReady)
        {
            return true;
        }

        return false;
    }

    public void ReleaseDownloadSlot()
    {
        try
        {
            _downloadSemaphore.Release();
            Mediator.Publish(new DownloadLimitChangedMessage());
        }
        catch (SemaphoreFullException)
        {
            // ignore
        }
    }

    /// <summary>
    /// Acquires a download slot and returns a lease that automatically releases it on dispose.
    /// Use this with 'await using' to ensure slots are always released, even in error paths.
    /// </summary>
    public async Task<DownloadSlotLease> AcquireDownloadSlotAsync(CancellationToken token)
    {
        await WaitForDownloadSlotAsync(token).ConfigureAwait(false);
        return new DownloadSlotLease(this);
    }

    /// <summary>
    /// A lease that holds a download slot and releases it on dispose.
    /// Ensures download slots are always released even in error paths.
    /// </summary>
    public sealed class DownloadSlotLease : IAsyncDisposable
    {
        private readonly FileTransferOrchestrator _orchestrator;
        private bool _released;

        internal DownloadSlotLease(FileTransferOrchestrator orchestrator)
        {
            _orchestrator = orchestrator;
        }

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                _orchestrator.ReleaseDownloadSlot();
            }
            return ValueTask.CompletedTask;
        }
    }

    public async Task<HttpResponseMessage> SendRequestAsync(int serverIndex, HttpMethod method, Uri uri,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead, bool withAuthToken = true)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        return await SendRequestInternalAsync(serverIndex, requestMessage, ct, httpCompletionOption, withAuthToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestAsync<T>(int serverIndex, HttpMethod method, Uri uri, T content, CancellationToken ct, bool withAuthToken = true) where T : class
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        if (content is not ByteArrayContent)
            requestMessage.Content = JsonContent.Create(content);
        else
            requestMessage.Content = content as ByteArrayContent;
        return await SendRequestInternalAsync(serverIndex, requestMessage, ct, withAuthToken: withAuthToken).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendRequestStreamAsync(int serverIndex, HttpMethod method, Uri uri, ProgressableStreamContent content, CancellationToken ct, bool withAuthToken = true)
    {
        using var requestMessage = new HttpRequestMessage(method, uri);
        requestMessage.Content = content;
        return await SendRequestInternalAsync(serverIndex, requestMessage, ct, withAuthToken: withAuthToken).ConfigureAwait(false);
    }

    public async Task WaitForDownloadSlotAsync(CancellationToken token)
    {
        lock (_semaphoreModificationLock)
        {
            if (_availableDownloadSlots != _syncConfig.Current.ParallelDownloads && _availableDownloadSlots == _downloadSemaphore.CurrentCount)
            {
                _availableDownloadSlots = _syncConfig.Current.ParallelDownloads;
                _downloadSemaphore = new(_availableDownloadSlots, _availableDownloadSlots);
            }
        }

        await _downloadSemaphore.WaitAsync(token).ConfigureAwait(false);
        Mediator.Publish(new DownloadLimitChangedMessage());
    }

    public long DownloadLimitPerSlot()
    {
        var limit = _syncConfig.Current.DownloadSpeedLimitInBytes;
        if (limit <= 0) return 0;
        limit = _syncConfig.Current.DownloadSpeedType switch
        {
            DownloadSpeeds.Bps => limit,
            DownloadSpeeds.KBps => limit * 1024,
            DownloadSpeeds.MBps => limit * 1024 * 1024,
            _ => limit,
        };
        var currentUsedDlSlots = CurrentlyUsedDownloadSlots;
        var avaialble = _availableDownloadSlots;
        var currentCount = _downloadSemaphore.CurrentCount;
        var dividedLimit = limit / (currentUsedDlSlots == 0 ? 1 : currentUsedDlSlots);
        if (dividedLimit < 0)
        {
            Logger.LogWarning("Calculated Bandwidth Limit is negative, returning Infinity: {Value}, CurrentlyUsedDownloadSlots is {CurrentSlots}, " +
                "DownloadSpeedLimit is {Limit}, available slots: {Avail}, current count: {Count}", dividedLimit, currentUsedDlSlots, limit, avaialble, currentCount);
            return long.MaxValue;
        }
        return Math.Clamp(dividedLimit, 1, long.MaxValue);
    }

    private const int MaxTransientRetries = 2;
    private const int TransientRetryDelayMs = 200;

    private async Task<HttpResponseMessage> SendRequestInternalAsync(ServerIndex serverIndex, HttpRequestMessage requestMessage,
        CancellationToken? ct = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead, bool withAuthToken = true)
    {
        if (withAuthToken)
        {
            var token = await _multiConnectTokenService.GetCachedToken(serverIndex).ConfigureAwait(false);
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (requestMessage.Content != null && requestMessage.Content is not StreamContent && requestMessage.Content is not ByteArrayContent)
        {
            var content = await ((JsonContent)requestMessage.Content).ReadAsStringAsync().ConfigureAwait(false);
            Logger.LogDebug("Sending {Method} to {Uri} (Content: {Content})", requestMessage.Method, requestMessage.RequestUri, content);
        }
        else
        {
            Logger.LogDebug("Sending {Method} to {Uri}", requestMessage.Method, requestMessage.RequestUri);
        }

        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxTransientRetries; attempt++)
        {
            try
            {
                if (ct != null)
                    return await _httpClient.SendAsync(requestMessage, httpCompletionOption, ct.Value).ConfigureAwait(false);
                return await _httpClient.SendAsync(requestMessage, httpCompletionOption).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsTransientError(ex) && attempt < MaxTransientRetries)
            {
                lastException = ex;
                Logger.LogWarning(ex, "Transient error during SendRequestInternal for {Uri}, retrying ({Attempt}/{MaxRetries})",
                    requestMessage.RequestUri, attempt + 1, MaxTransientRetries);

                try
                {
                    await Task.Delay(TransientRetryDelayMs, ct ?? CancellationToken.None).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error during SendRequestInternal for {Uri}", requestMessage.RequestUri);
                throw;
            }
        }

        // All retries exhausted
        Logger.LogError(lastException, "All retries exhausted for {Uri}", requestMessage.RequestUri);
        throw lastException!;
    }

    /// <summary>
    /// Determines if an HttpRequestException represents a transient error that should be retried.
    /// </summary>
    private static bool IsTransientError(HttpRequestException ex)
    {
        // Socket errors (connection reset, connection aborted, etc.)
        if (ex.InnerException is SocketException socketEx)
        {
            return socketEx.SocketErrorCode is
                SocketError.ConnectionReset or
                SocketError.ConnectionAborted or
                SocketError.TimedOut or
                SocketError.HostUnreachable or
                SocketError.NetworkUnreachable or
                SocketError.TryAgain;
        }

        // IO exceptions (broken pipe, etc.)
        if (ex.InnerException is IOException)
        {
            return true;
        }

        // HTTP 5xx errors are typically transient
        if (ex.StatusCode.HasValue)
        {
            var statusCode = (int)ex.StatusCode.Value;
            return statusCode >= 500 && statusCode < 600;
        }

        return false;
    }
}
