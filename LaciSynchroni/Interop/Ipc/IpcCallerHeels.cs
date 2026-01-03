using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Interop.Ipc;

public sealed class IpcCallerHeels : IpcCallerBase
{
    private readonly ICallGateSubscriber<(int, int)> _heelsGetApiVersion;
    private readonly ICallGateSubscriber<string> _heelsGetOffset;
    private readonly ICallGateSubscriber<string, object?> _heelsOffsetUpdate;
    private readonly ICallGateSubscriber<int, string, object?> _heelsRegisterPlayer;
    private readonly ICallGateSubscriber<int, object?> _heelsUnregisterPlayer;

    protected override string TargetPluginName => "SimpleHeels";

    public IpcCallerHeels(ILogger<IpcCallerHeels> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil, SyncMediator syncMediator)
        : base(logger, pi, dalamudUtil, syncMediator)
    {
        _heelsGetApiVersion = pi.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        _heelsGetOffset = pi.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
        _heelsRegisterPlayer = pi.GetIpcSubscriber<int, string, object?>("SimpleHeels.RegisterPlayer");
        _heelsUnregisterPlayer = pi.GetIpcSubscriber<int, object?>("SimpleHeels.UnregisterPlayer");
        _heelsOffsetUpdate = pi.GetIpcSubscriber<string, object?>("SimpleHeels.LocalChanged");

        _heelsOffsetUpdate.Subscribe(HeelsOffsetChange);

        CheckAPI();
    }

    protected override bool CheckApiViaIpc()
    {
        try
        {
            return _heelsGetApiVersion.InvokeFunc() is { Item1: 2, Item2: >= 1 };
        }
        catch
        {
            return false;
        }
    }

    private void HeelsOffsetChange(string offset)
    {
        Mediator.Publish(new HeelsOffsetMessage());
    }

    public async Task<string> GetOffsetAsync()
    {
        return await SafeInvokeAsync(
            async () => await DalamudUtil.RunOnFrameworkThread(_heelsGetOffset.InvokeFunc).ConfigureAwait(false),
            string.Empty).ConfigureAwait(false) ?? string.Empty;
    }

    public async Task RestoreOffsetForPlayerAsync(IntPtr character)
    {
        await SafeInvokeAsync(async () =>
        {
            await DalamudUtil.RunOnFrameworkThread(() =>
            {
                var gameObj = DalamudUtil.CreateGameObject(character);
                if (gameObj != null)
                {
                    Logger.LogTrace("Restoring Heels data to {chara}", character.ToString("X"));
                    _heelsUnregisterPlayer.InvokeAction(gameObj.ObjectIndex);
                }
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task SetOffsetForPlayerAsync(IntPtr character, string data)
    {
        await SafeInvokeAsync(async () =>
        {
            await DalamudUtil.RunOnFrameworkThread(() =>
            {
                var gameObj = DalamudUtil.CreateGameObject(character);
                if (gameObj != null)
                {
                    Logger.LogTrace("Applying Heels data to {chara}", character.ToString("X"));
                    _heelsRegisterPlayer.InvokeAction(gameObj.ObjectIndex, data);
                }
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _heelsOffsetUpdate.Unsubscribe(HeelsOffsetChange);
        }
    }
}
