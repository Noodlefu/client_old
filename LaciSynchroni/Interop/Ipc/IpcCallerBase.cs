using Dalamud.Plugin;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration.Models;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Interop.Ipc;

/// <summary>
/// Base class for IPC callers that communicate with other Dalamud plugins.
/// Provides common functionality for API availability checking and error handling.
/// </summary>
public abstract class IpcCallerBase : DisposableMediatorSubscriberBase
{
    protected readonly IDalamudPluginInterface PluginInterface;
    protected readonly DalamudUtilService DalamudUtil;
    private bool _shownUnavailableWarning;
    private int _failedCheckCount;

    /// <summary>
    /// Gets whether the target plugin's API is available and ready.
    /// </summary>
    public bool APIAvailable { get; protected set; }

    /// <summary>
    /// Gets the internal name of the target plugin (e.g., "Penumbra", "Glamourer").
    /// </summary>
    protected abstract string TargetPluginName { get; }

    /// <summary>
    /// Gets the minimum required version of the target plugin.
    /// Return null if no minimum version is required or if using IPC-based version checks.
    /// </summary>
    protected virtual Version? MinimumVersion => null;

    /// <summary>
    /// Gets a user-friendly display name for the target plugin.
    /// </summary>
    protected virtual string DisplayName => TargetPluginName;

    /// <summary>
    /// Gets whether to show a notification when the plugin is unavailable.
    /// </summary>
    protected virtual bool ShowUnavailableNotification => false;

    /// <summary>
    /// Gets whether to use plugin list-based version checking (true) or IPC-based checking (false).
    /// Default is false (IPC-based), override to true for plugin list-based checking.
    /// </summary>
    protected virtual bool UsePluginListVersionCheck => false;

    /// <summary>
    /// Gets the number of failed checks before showing the unavailable notification.
    /// This allows time for other plugins to initialize their IPC endpoints.
    /// Default is 3 (roughly 3 seconds with DelayedFrameworkUpdate).
    /// </summary>
    protected virtual int FailedChecksBeforeNotification => 3;

    protected IpcCallerBase(
        ILogger logger,
        IDalamudPluginInterface pluginInterface,
        DalamudUtilService dalamudUtil,
        SyncMediator mediator) : base(logger, mediator)
    {
        PluginInterface = pluginInterface;
        DalamudUtil = dalamudUtil;

        // Reset warning and check count on login so user sees it again if still unavailable
        Mediator.Subscribe<DalamudLoginMessage>(this, _ =>
        {
            _shownUnavailableWarning = false;
            _failedCheckCount = 0;
        });
    }

    /// <summary>
    /// Checks if the target plugin's API is available.
    /// Uses either plugin list-based or IPC-based checking depending on UsePluginListVersionCheck.
    /// </summary>
    public virtual void CheckAPI()
    {
        bool available = false;
        try
        {
            if (UsePluginListVersionCheck)
            {
                available = CheckApiViaPluginList();
            }
            else
            {
                available = CheckApiViaIpc();
            }

            if (available)
            {
                _failedCheckCount = 0;
            }
            else
            {
                _failedCheckCount++;
            }

            _shownUnavailableWarning = _shownUnavailableWarning && !available;
            APIAvailable = available;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error checking {PluginName} API availability", TargetPluginName);
            APIAvailable = false;
            _failedCheckCount++;
        }
        finally
        {
            if (!available && !_shownUnavailableWarning && ShowUnavailableNotification
                && _failedCheckCount >= FailedChecksBeforeNotification)
            {
                _shownUnavailableWarning = true;
                PublishUnavailableNotification();
            }
        }
    }

    /// <summary>
    /// Checks API availability via the installed plugins list.
    /// </summary>
    private bool CheckApiViaPluginList()
    {
        var pluginInfo = PluginInterface.InstalledPlugins
            .FirstOrDefault(p => string.Equals(p.InternalName, TargetPluginName, StringComparison.OrdinalIgnoreCase));

        if (pluginInfo == null)
        {
            Logger.LogDebug("{PluginName} is not installed", TargetPluginName);
            return false;
        }

        if (MinimumVersion != null && pluginInfo.Version < MinimumVersion)
        {
            Logger.LogDebug("{PluginName} version {Version} is below minimum {MinVersion}",
                TargetPluginName, pluginInfo.Version, MinimumVersion);
            return false;
        }

        return CheckPluginReady();
    }

    /// <summary>
    /// Checks API availability via IPC version call.
    /// Override this method to implement IPC-based version checking.
    /// </summary>
    protected virtual bool CheckApiViaIpc()
    {
        // Default implementation falls back to plugin list check
        return CheckApiViaPluginList();
    }

    /// <summary>
    /// Override to check if the plugin is ready beyond version checks.
    /// Called after version verification succeeds.
    /// </summary>
    protected virtual bool CheckPluginReady()
    {
        return true;
    }

    /// <summary>
    /// Override to customize the unavailable notification message.
    /// </summary>
    protected virtual void PublishUnavailableNotification()
    {
        Mediator.Publish(new NotificationMessage(
            $"{DisplayName} inactive",
            $"Your {DisplayName} installation is not active or out of date. " +
            $"Update {DisplayName} to continue using {DalamudUtil.GetPluginName()}.",
            NotificationType.Error));
    }

    /// <summary>
    /// Executes an IPC call with error handling.
    /// Returns default value if API is unavailable or call fails.
    /// </summary>
    protected T? SafeInvoke<T>(Func<T> ipcCall, T? defaultValue = default, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (!APIAvailable)
        {
            Logger.LogTrace("{PluginName} API not available for {Caller}", TargetPluginName, caller);
            return defaultValue;
        }

        try
        {
            return ipcCall();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {PluginName} IPC call: {Caller}", TargetPluginName, caller);
            return defaultValue;
        }
    }

    /// <summary>
    /// Executes an IPC call with error handling (void return).
    /// </summary>
    protected void SafeInvoke(Action ipcCall, [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (!APIAvailable)
        {
            Logger.LogTrace("{PluginName} API not available for {Caller}", TargetPluginName, caller);
            return;
        }

        try
        {
            ipcCall();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during {PluginName} IPC call: {Caller}", TargetPluginName, caller);
        }
    }

    /// <summary>
    /// Executes an async IPC call with error handling.
    /// </summary>
    protected async Task<T?> SafeInvokeAsync<T>(
        Func<Task<T>> ipcCall,
        T? defaultValue = default,
        [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (!APIAvailable)
        {
            Logger.LogTrace("{PluginName} API not available for {Caller}", TargetPluginName, caller);
            return defaultValue;
        }

        try
        {
            return await ipcCall().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during async {PluginName} IPC call: {Caller}", TargetPluginName, caller);
            return defaultValue;
        }
    }

    /// <summary>
    /// Executes an async IPC call with error handling (void return).
    /// </summary>
    protected async Task SafeInvokeAsync(
        Func<Task> ipcCall,
        [System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        if (!APIAvailable)
        {
            Logger.LogTrace("{PluginName} API not available for {Caller}", TargetPluginName, caller);
            return;
        }

        try
        {
            await ipcCall().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during async {PluginName} IPC call: {Caller}", TargetPluginName, caller);
        }
    }
}
