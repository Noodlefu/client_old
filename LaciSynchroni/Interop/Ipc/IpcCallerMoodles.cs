using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.Interop.Ipc;

public sealed class IpcCallerMoodles : IpcCallerBase
{
    private readonly ICallGateSubscriber<int> _moodlesApiVersion;
    private readonly ICallGateSubscriber<nint, object> _moodlesOnChange;
    private readonly ICallGateSubscriber<nint, string> _moodlesGetStatus;
    private readonly ICallGateSubscriber<nint, string, object> _moodlesSetStatus;
    private readonly ICallGateSubscriber<nint, object> _moodlesRevertStatus;

    protected override string TargetPluginName => "Moodles";

    public IpcCallerMoodles(ILogger<IpcCallerMoodles> logger, IDalamudPluginInterface pi, DalamudUtilService dalamudUtil,
        SyncMediator syncMediator)
        : base(logger, pi, dalamudUtil, syncMediator)
    {
        _moodlesApiVersion = pi.GetIpcSubscriber<int>("Moodles.Version");
        _moodlesOnChange = pi.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified");
        _moodlesGetStatus = pi.GetIpcSubscriber<nint, string>("Moodles.GetStatusManagerByPtrV2");
        _moodlesSetStatus = pi.GetIpcSubscriber<nint, string, object>("Moodles.SetStatusManagerByPtrV2");
        _moodlesRevertStatus = pi.GetIpcSubscriber<nint, object>("Moodles.ClearStatusManagerByPtrV2");

        _moodlesOnChange.Subscribe(OnMoodlesChange);

        CheckAPI();
    }

    protected override bool CheckApiViaIpc()
    {
        try
        {
            return _moodlesApiVersion.InvokeFunc() == 4;
        }
        catch
        {
            return false;
        }
    }

    private void OnMoodlesChange(nint characterAddress)
    {
        Mediator.Publish(new MoodlesMessage(characterAddress));
    }

    public async Task<string?> GetStatusAsync(nint address)
    {
        return await SafeInvokeAsync(
            async () => await DalamudUtil.RunOnFrameworkThread(() => _moodlesGetStatus.InvokeFunc(address)).ConfigureAwait(false),
            defaultValue: null).ConfigureAwait(false);
    }

    public async Task SetStatusAsync(nint pointer, string status)
    {
        await SafeInvokeAsync(async () =>
        {
            await DalamudUtil.RunOnFrameworkThread(() => _moodlesSetStatus.InvokeAction(pointer, status)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task RevertStatusAsync(nint pointer)
    {
        await SafeInvokeAsync(async () =>
        {
            await DalamudUtil.RunOnFrameworkThread(() => _moodlesRevertStatus.InvokeAction(pointer)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _moodlesOnChange.Unsubscribe(OnMoodlesChange);
        }
    }
}
