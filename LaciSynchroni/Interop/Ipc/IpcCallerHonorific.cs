using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace LaciSynchroni.Interop.Ipc;

public sealed class IpcCallerHonorific : IpcCallerBase
{
    private readonly ICallGateSubscriber<(uint major, uint minor)> _honorificApiVersion;
    private readonly ICallGateSubscriber<int, object> _honorificClearCharacterTitle;
    private readonly ICallGateSubscriber<object> _honorificDisposing;
    private readonly ICallGateSubscriber<string> _honorificGetLocalCharacterTitle;
    private readonly ICallGateSubscriber<string, object> _honorificLocalCharacterTitleChanged;
    private readonly ICallGateSubscriber<object> _honorificReady;
    private readonly ICallGateSubscriber<int, string, object> _honorificSetCharacterTitle;
    private readonly ConcurrentDictionary<int, string> _appliedTitleByIndex = new();

    protected override string TargetPluginName => "Honorific";

    public IpcCallerHonorific(ILogger<IpcCallerHonorific> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SyncMediator syncMediator)
        : base(logger, pi, dalamudUtil, syncMediator)
    {
        _honorificApiVersion = pi.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");
        _honorificGetLocalCharacterTitle = pi.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
        _honorificClearCharacterTitle = pi.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
        _honorificSetCharacterTitle = pi.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        _honorificLocalCharacterTitleChanged = pi.GetIpcSubscriber<string, object>("Honorific.LocalCharacterTitleChanged");
        _honorificDisposing = pi.GetIpcSubscriber<object>("Honorific.Disposing");
        _honorificReady = pi.GetIpcSubscriber<object>("Honorific.Ready");

        _honorificLocalCharacterTitleChanged.Subscribe(OnHonorificLocalCharacterTitleChanged);
        _honorificDisposing.Subscribe(OnHonorificDisposing);
        _honorificReady.Subscribe(OnHonorificReady);

        CheckAPI();
    }

    protected override bool CheckApiViaIpc()
    {
        try
        {
            return _honorificApiVersion.InvokeFunc() is { Item1: 3, Item2: >= 1 };
        }
        catch
        {
            return false;
        }
    }

    public void ClearTitleCacheForObjectIndex(int objectIndex)
    {
        _appliedTitleByIndex.TryRemove(objectIndex, out _);
    }

    public async Task ClearTitleAsync(nint character)
    {
        await SafeInvokeAsync(async () =>
        {
            await DalamudUtil.RunOnFrameworkThread(() =>
            {
                var gameObj = DalamudUtil.CreateGameObject(character);
                if (gameObj is IPlayerCharacter c)
                {
                    Logger.LogTrace("Honorific removing for {addr}", c.Address.ToString("X"));
                    _honorificClearCharacterTitle!.InvokeAction(c.ObjectIndex);
                    ClearTitleCacheForObjectIndex(c.ObjectIndex);
                }
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<string> GetTitle()
    {
        var title = await SafeInvokeAsync(
            async () => await DalamudUtil.RunOnFrameworkThread(() => _honorificGetLocalCharacterTitle.InvokeFunc()).ConfigureAwait(false),
            string.Empty).ConfigureAwait(false);
        return string.IsNullOrEmpty(title) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
    }

    public async Task SetTitleAsync(IntPtr character, string honorificDataB64)
    {
        Logger.LogTrace("Applying Honorific data to {chara}", character.ToString("X"));
        await SafeInvokeAsync(async () =>
        {
            await DalamudUtil.RunOnFrameworkThread(() =>
            {
                var gameObj = DalamudUtil.CreateGameObject(character);
                if (gameObj is IPlayerCharacter pc)
                {
                    if (_appliedTitleByIndex.TryGetValue(pc.ObjectIndex, out var cached) &&
                        string.Equals(cached, honorificDataB64, StringComparison.Ordinal))
                    {
                        Logger.LogTrace("Honorific unchanged for {addr}, skipping IPC", pc.Address.ToString("X"));
                        return;
                    }

                    string honorificData = string.IsNullOrEmpty(honorificDataB64) ? string.Empty : Encoding.UTF8.GetString(Convert.FromBase64String(honorificDataB64));
                    if (string.IsNullOrEmpty(honorificData))
                    {
                        _honorificClearCharacterTitle!.InvokeAction(pc.ObjectIndex);
                    }
                    else
                    {
                        _honorificSetCharacterTitle!.InvokeAction(pc.ObjectIndex, honorificData);
                    }
                    _appliedTitleByIndex[pc.ObjectIndex] = honorificDataB64;
                }
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private void OnHonorificDisposing()
    {
        Mediator.Publish(new HonorificMessage(string.Empty));
    }

    private void OnHonorificLocalCharacterTitleChanged(string titleJson)
    {
        string titleData = string.IsNullOrEmpty(titleJson) ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(titleJson));
        Mediator.Publish(new HonorificMessage(titleData));
    }

    private void OnHonorificReady()
    {
        CheckAPI();
        Mediator.Publish(new HonorificReadyMessage());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _honorificLocalCharacterTitleChanged.Unsubscribe(OnHonorificLocalCharacterTitleChanged);
            _honorificDisposing.Unsubscribe(OnHonorificDisposing);
            _honorificReady.Unsubscribe(OnHonorificReady);
        }
    }
}
