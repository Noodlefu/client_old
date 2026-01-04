using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration.Models;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Interop.Ipc;

public sealed class IpcCallerGlamourer : IpcCallerBase
{
    private readonly SyncMediator _syncMediator;
    private readonly RedrawManager _redrawManager;

    private readonly ApiVersion _glamourerApiVersions;
    private readonly ApplyState? _glamourerApplyAll;
    private readonly GetStateBase64? _glamourerGetAllCustomization;
    private readonly RevertState _glamourerRevert;
    private readonly RevertStateName _glamourerRevertByName;
    private readonly UnlockState _glamourerUnlock;
    private readonly UnlockStateName _glamourerUnlockByName;
    private readonly EventSubscriber<nint>? _glamourerStateChanged;

    private readonly uint LockCode = 0x6D617265;

    // IpcCallerBase overrides
    protected override string TargetPluginName => "Glamourer";
    protected override Version? MinimumVersion => new(1, 3, 0, 10);
    protected override bool ShowUnavailableNotification => true;

    public IpcCallerGlamourer(ILogger<IpcCallerGlamourer> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil, SyncMediator syncMediator,
        RedrawManager redrawManager) : base(logger, pi, dalamudUtil, syncMediator)
    {
        _glamourerApiVersions = new ApiVersion(pi);
        _glamourerGetAllCustomization = new GetStateBase64(pi);
        _glamourerApplyAll = new ApplyState(pi);
        _glamourerRevert = new RevertState(pi);
        _glamourerRevertByName = new RevertStateName(pi);
        _glamourerUnlock = new UnlockState(pi);
        _glamourerUnlockByName = new UnlockStateName(pi);

        _syncMediator = syncMediator;
        _redrawManager = redrawManager;
        CheckAPI();

        _glamourerStateChanged = StateChanged.Subscriber(pi, GlamourerChanged);
        _glamourerStateChanged.Enable();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _redrawManager.Cancel();
        _glamourerStateChanged?.Dispose();
    }

    protected override bool CheckApiViaIpc()
    {
        try
        {
            var version = _glamourerApiVersions.Invoke();
            return version is { Major: 1, Minor: >= 1 };
        }
        catch
        {
            return false;
        }
    }

    protected override void PublishUnavailableNotification()
    {
        Mediator.Publish(new NotificationMessage(
            "Glamourer inactive",
            $"Your Glamourer installation is not active or out of date. Update Glamourer to continue to use {DalamudUtil.GetPluginName()}." +
            " If you just updated Glamourer, ignore this message.",
            NotificationType.Error));
    }

    public async Task ApplyAllAsync(ILogger logger, GameObjectHandler handler, string? customization, Guid applicationId, CancellationToken token, bool fireAndForget = false)
    {
        if (!APIAvailable || string.IsNullOrEmpty(customization) || DalamudUtil.IsZoning) return;

        await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                try
                {
                    logger.LogDebug("[{appid}] Calling on IPC: GlamourerApplyAll", applicationId);
                    _glamourerApplyAll!.Invoke(customization, chara.ObjectIndex, LockCode);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{appid}] Failed to apply Glamourer data", applicationId);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task<string> GetCharacterCustomizationAsync(IntPtr character)
    {
        if (!APIAvailable) return string.Empty;
        try
        {
            return await DalamudUtil.RunOnFrameworkThread(() =>
            {
                var gameObj = DalamudUtil.CreateGameObject(character);
                if (gameObj is ICharacter c)
                {
                    return _glamourerGetAllCustomization!.Invoke(c.ObjectIndex).Item2 ?? string.Empty;
                }
                return string.Empty;
            }).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task RevertAsync(ILogger logger, GameObjectHandler handler, Guid applicationId, CancellationToken token)
    {
        if ((!APIAvailable) || DalamudUtil.IsZoning) return;

        await _redrawManager.RedrawSemaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _redrawManager.PenumbraRedrawInternalAsync(logger, handler, applicationId, (chara) =>
            {
                try
                {
                    logger.LogDebug("[{appid}] Calling On IPC: GlamourerUnlockName", applicationId);
                    _glamourerUnlock.Invoke(chara.ObjectIndex, LockCode);
                    logger.LogDebug("[{appid}] Calling On IPC: GlamourerRevert", applicationId);
                    _glamourerRevert.Invoke(chara.ObjectIndex, LockCode);
                    logger.LogDebug("[{appid}] Calling On IPC: PenumbraRedraw", applicationId);

                    _syncMediator.Publish(new PenumbraRedrawCharacterMessage(chara));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{appid}] Error during GlamourerRevert", applicationId);
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            _redrawManager.RedrawSemaphore.Release();
        }
    }

    public async Task RevertByNameAsync(ILogger logger, string name, Guid applicationId)
    {
        if ((!APIAvailable) || DalamudUtil.IsZoning) return;

        await DalamudUtil.RunOnFrameworkThread(() =>
        {
            RevertByName(logger, name, applicationId);

        }).ConfigureAwait(false);
    }

    public void RevertByName(ILogger logger, string name, Guid applicationId)
    {
        if ((!APIAvailable) || DalamudUtil.IsZoning) return;

        try
        {
            logger.LogDebug("[{appid}] Calling On IPC: GlamourerRevertByName", applicationId);
            _glamourerRevertByName.Invoke(name, LockCode);
            logger.LogDebug("[{appid}] Calling On IPC: GlamourerUnlockName", applicationId);
            _glamourerUnlockByName.Invoke(name, LockCode);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error during Glamourer RevertByName");
        }
    }

    private void GlamourerChanged(nint address)
    {
        _syncMediator.Publish(new GlamourerChangedMessage(address));
    }
}
