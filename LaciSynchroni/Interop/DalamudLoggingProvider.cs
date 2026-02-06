using Dalamud.Plugin.Services;
using LaciSynchroni.SyncConfiguration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace LaciSynchroni.Interop;

[ProviderAlias("Dalamud")]
public sealed class DalamudLoggingProvider(SyncConfigService syncConfigService, IPluginLog pluginLog, bool hasModifiedGameFiles) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, DalamudLogger> _loggers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SyncConfigService _syncConfigService = syncConfigService;
    private readonly IPluginLog _pluginLog = pluginLog;
    private readonly bool _hasModifiedGameFiles = hasModifiedGameFiles;

    public ILogger CreateLogger(string categoryName)
    {
        string catName = categoryName.Split(".", StringSplitOptions.RemoveEmptyEntries)[^1];
        if (catName.Length > 15)
        {
            catName = string.Join("", catName.Take(6)) + "..." + string.Join("", catName.TakeLast(6));
        }
        else
        {
            catName = string.Join("", Enumerable.Range(0, 15 - catName.Length).Select(_ => " ")) + catName;
        }

        return _loggers.GetOrAdd(catName, name => new DalamudLogger(name, _syncConfigService, _pluginLog, _hasModifiedGameFiles));
    }

    public void Dispose()
    {
        _loggers.Clear();
        GC.SuppressFinalize(this);
    }
}
