using LaciSynchroni.Common.Data;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.UI;
using LaciSynchroni.Utils;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Web;
using NotificationMessage = LaciSynchroni.Services.Mediator.NotificationMessage;

namespace LaciSynchroni.Interop.Ipc;

/// <summary>
/// Local HTTP server that listens for server join requests via browser links
/// Inspired by Heliosphere's implementation
/// </summary>
#pragma warning disable S2930 // CTS lifecycle managed via CancelDispose() before each reassignment and in Dispose
public class LocalHttpServer : DisposableMediatorSubscriberBase
{
    public enum HttpServerState
    {
        STOPPED,
        STARTING,
        STARTED,
        ERROR,
    }

    private readonly ILogger<LocalHttpServer> _logger;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public HttpServerState State { get; private set; }

    public LocalHttpServer(
        ILogger<LocalHttpServer> logger,
        ServerConfigurationManager serverConfigurationManager,
        SyncMediator mediator) : base(logger, mediator)
    {
        _logger = logger;
        _serverConfigurationManager = serverConfigurationManager;

        Mediator.Subscribe<HttpServerToggleMessage>(this, HandleToggleRequest);

        State = HttpServerState.STOPPED;
    }

    private void HandleToggleRequest(HttpServerToggleMessage message)
    {
        if (message.enable && State is HttpServerState.STOPPED or HttpServerState.ERROR)
        {
            _cts?.CancelDispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                await StartAsync(token).ConfigureAwait(false);
                await Task.Delay(10 * 60 * 1000, token).ConfigureAwait(false);
                if (_serverConfigurationManager.ServerIndexes.Any() && !token.IsCancellationRequested)
                {
                    await StopAsync().ConfigureAwait(false);
                }
            }, token);
        }
        else if (!message.enable && State is HttpServerState.STARTED or HttpServerState.ERROR)
        {
            _ = Task.Run(() => StopAsync(), CancellationToken.None);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
            State = HttpServerState.STARTING;
            _cts?.CancelDispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"{PluginHttpServerData.Hostname}:{PluginHttpServerData.Port}/");
            _listener.Start();

            _ = Task.Run(() => ListenAsync(token), token);

            State = HttpServerState.STARTED;

            _logger.LogInformation("Local HTTP server started on port {Port}", PluginHttpServerData.Port);
            _logger.LogInformation("Server join links: {Prefix}:{Port}/laci/join?&uri=...&secretkey=...", PluginHttpServerData.Hostname, PluginHttpServerData.Port);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogWarning(ex, "Failed to start HTTP server on port {Port}. Server join links will not work.", PluginHttpServerData.Port);
            State = HttpServerState.ERROR;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error starting HTTP server");
            State = HttpServerState.ERROR;
        }
    }

    public Task StopAsync()
    {
        if (State == HttpServerState.STOPPED)
        {
            return Task.CompletedTask;
        }
        try
        {
            _cts?.CancelDispose();
            _cts = null;
            _listener?.Close();
            _listener = null;

            State = HttpServerState.STOPPED;

            _logger.LogInformation("Local HTTP server stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping HTTP server");
            State = HttpServerState.ERROR;
        }
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && State != HttpServerState.STOPPED)
        {
            _cts?.CancelDispose();
            _cts = null;
            try { _listener?.Close(); } catch { /* best-effort */ }
            _listener = null;
            State = HttpServerState.STOPPED;
        }
        base.Dispose(disposing);
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context), token);
            }
            catch (HttpListenerException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HTTP listener loop");
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            _logger.LogInformation("Received request: {Method} {Url}", request.HttpMethod, request.Url);

            var path = request.Url?.AbsolutePath.TrimStart('/');

            if (string.IsNullOrEmpty(path))
            {
                await SendResponseAsync(response, 400, "Invalid request").ConfigureAwait(false);
                return;
            }

            var parts = path.Split('/');
            if (parts.Length < 2 || !string.Equals(parts[0], "laci", StringComparison.OrdinalIgnoreCase))
            {
                await SendResponseAsync(response, 404, "Not found").ConfigureAwait(false);
                return;
            }

            var action = parts[1];
            var queryParams = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);

            if (string.Equals(action, "join", StringComparison.OrdinalIgnoreCase))
            {
                HandleJoinServer(queryParams);
                await SendResponseAsync(response, 200,
                    "<html><body><h1>Success!</h1><p>Check your game - a dialog should have appeared to add the server.</p><p>You can close this tab.</p></body></html>",
                    "text/html").ConfigureAwait(false);
            }
            else
            {
                await SendResponseAsync(response, 400, $"Unknown action: {action}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling HTTP request");
            try
            {
                await SendResponseAsync(context.Response, 500, "Internal server error").ConfigureAwait(false);
            }
            catch
            {
                // Ignore errors sending error response
            }
        }
    }

    private void HandleJoinServer(System.Collections.Specialized.NameValueCollection queryParams)
    {
        var serverUri = queryParams["uri"];
        var secretKey = queryParams["secretkey"];

        if (string.IsNullOrEmpty(serverUri))
        {
            _logger.LogWarning("Missing required parameters for server join");
            Mediator.Publish(new NotificationMessage("Invalid Link", "Server link is missing required information (URI).", NotificationType.Warning));
            return;
        }

        var normalizedUri = serverUri.TrimEnd('/');
        if (normalizedUri.EndsWith("/hub", StringComparison.OrdinalIgnoreCase))
        {
            normalizedUri = normalizedUri[..^4].TrimEnd('/');
        }

        var existingServers = _serverConfigurationManager.GetServerInfo();
        var existingServerWithUri = existingServers.FirstOrDefault(s => string.Equals(s.Uri, normalizedUri, StringComparison.OrdinalIgnoreCase));
        if (existingServerWithUri != null)
        {
            _logger.LogInformation("Server already exists: {ServerName}", existingServerWithUri.Name);
            Mediator.Publish(new NotificationMessage("Server Exists", $"The server '{existingServerWithUri.Name}' is already configured.", NotificationType.Info));
            return;
        }

        var newServer = new ServerStorage
        {
            ServerUri = normalizedUri,
            UseOAuth2 = true,
            UseAdvancedUris = false,
            SecretKeys = { { 0, new SecretKey() { FriendlyName = $"Secret Key added on Setup ({DateTime.Now:yyyy-MM-dd})", Key = secretKey ?? string.Empty } } },
        };

        Mediator.Publish(new ServerJoinRequestMessage(newServer));

        _logger.LogInformation("Server join request created for {ServerUri}", normalizedUri);
    }

    private static async Task SendResponseAsync(HttpListenerResponse response, int statusCode, string content, string contentType = "text/plain")
    {
        try
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            var buffer = System.Text.Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
            response.OutputStream.Close();
        }
        catch (Exception)
        {
            // Ignore errors writing response
        }
    }
}
