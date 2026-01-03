namespace LaciSynchroni.Exceptions;

/// <summary>
/// Base exception for all LaciSynchroni-specific errors.
/// </summary>
public class LaciSynchroniException : Exception
{
    public LaciSynchroniException()
    {
    }

    public LaciSynchroniException(string message)
        : base(message)
    {
    }

    public LaciSynchroniException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when network operations fail (HTTP, SignalR, etc.)
/// </summary>
public class NetworkException : LaciSynchroniException
{
    public int? StatusCode { get; }
    public string? Endpoint { get; }

    public NetworkException(string message)
        : base(message)
    {
    }

    public NetworkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NetworkException(string message, int? statusCode, string? endpoint = null)
        : base(message)
    {
        StatusCode = statusCode;
        Endpoint = endpoint;
    }

    public NetworkException(string message, int? statusCode, string? endpoint, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Endpoint = endpoint;
    }
}

/// <summary>
/// Exception thrown when configuration loading, saving, or validation fails.
/// </summary>
public class ConfigurationException : LaciSynchroniException
{
    public string? ConfigPath { get; }

    public ConfigurationException(string message)
        : base(message)
    {
    }

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConfigurationException(string message, string? configPath)
        : base(message)
    {
        ConfigPath = configPath;
    }

    public ConfigurationException(string message, string? configPath, Exception innerException)
        : base(message, innerException)
    {
        ConfigPath = configPath;
    }
}

/// <summary>
/// Exception thrown when file cache operations fail (hashing, storage, retrieval).
/// </summary>
public class FileCacheException : LaciSynchroniException
{
    public string? FilePath { get; }
    public string? FileHash { get; }

    public FileCacheException(string message)
        : base(message)
    {
    }

    public FileCacheException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FileCacheException(string message, string? filePath, string? fileHash = null)
        : base(message)
    {
        FilePath = filePath;
        FileHash = fileHash;
    }

    public FileCacheException(string message, string? filePath, string? fileHash, Exception innerException)
        : base(message, innerException)
    {
        FilePath = filePath;
        FileHash = fileHash;
    }
}

/// <summary>
/// Exception thrown when file transfer operations fail (download/upload).
/// </summary>
public class FileTransferException : LaciSynchroniException
{
    public string? FileHash { get; }
    public string? ServerUri { get; }
    public bool IsUpload { get; }

    public FileTransferException(string message, bool isUpload = false)
        : base(message)
    {
        IsUpload = isUpload;
    }

    public FileTransferException(string message, Exception innerException, bool isUpload = false)
        : base(message, innerException)
    {
        IsUpload = isUpload;
    }

    public FileTransferException(string message, string? fileHash, string? serverUri, bool isUpload, Exception? innerException = null)
        : base(message, innerException ?? new Exception())
    {
        FileHash = fileHash;
        ServerUri = serverUri;
        IsUpload = isUpload;
    }
}

/// <summary>
/// Exception thrown when IPC communication with other plugins fails.
/// </summary>
public class IpcException : LaciSynchroniException
{
    public string? PluginName { get; }

    public IpcException(string message)
        : base(message)
    {
    }

    public IpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public IpcException(string message, string? pluginName)
        : base(message)
    {
        PluginName = pluginName;
    }

    public IpcException(string message, string? pluginName, Exception innerException)
        : base(message, innerException)
    {
        PluginName = pluginName;

    }
}
