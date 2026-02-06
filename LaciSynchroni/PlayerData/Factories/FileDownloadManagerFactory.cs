using LaciSynchroni.FileCache;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration.Configurations;
using LaciSynchroni.WebAPI.Files;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.PlayerData.Factories;

public class FileDownloadManagerFactory(ILoggerFactory loggerFactory, SyncMediator syncMediator, FileTransferOrchestrator fileTransferOrchestrator,
    FileCacheManager fileCacheManager, FileCompactor fileCompactor, ServerConfigurationManager serverManager)
{
    private readonly FileCacheManager _fileCacheManager = fileCacheManager;
    private readonly FileCompactor _fileCompactor = fileCompactor;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator = fileTransferOrchestrator;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly SyncMediator _syncMediator = syncMediator;
    private readonly ServerConfigurationManager _serverManager = serverManager;

    public FileDownloadManager Create()
    {
        return new FileDownloadManager(_loggerFactory.CreateLogger<FileDownloadManager>(), _syncMediator, _fileTransferOrchestrator, _fileCacheManager, _fileCompactor, _serverManager);
    }
}
