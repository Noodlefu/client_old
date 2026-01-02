using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using LaciSynchroni.Common.Data;
using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.Common.Data.Extensions;
using LaciSynchroni.Common.Dto.User;
using LaciSynchroni.PlayerData.Factories;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.Utils;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.PlayerData.Pairs;

public class Pair
{
    private static readonly SeString _openProfileSeString = new SeStringBuilder().AddText("Open Profile").Build();
    private static readonly SeString _reapplyDataSeString = new SeStringBuilder().AddText("Reapply last data").Build();
    private static readonly SeString _cyclePauseStateSeString = new SeStringBuilder().AddText("Cycle pause state").Build();
    private static readonly SeString _changePermissionsSeString = new SeStringBuilder().AddText("Change Permissions").Build();

    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly SemaphoreSlim _creationSemaphore = new(1);
    private readonly ILogger<Pair> _logger;
    private readonly SyncMediator _mediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    /// <summary>
    /// The server from which this pair originates
    /// </summary>
    public readonly int ServerIndex;
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    private ushort? _visibleHomeWorldId;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory,
        SyncMediator mediator, ServerConfigurationManager serverConfigurationManager, int serverIndex)
    {
        _logger = logger;
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _mediator = mediator;
        _serverConfigurationManager = serverConfigurationManager;
        ServerIndex = serverIndex;
    }

    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public IndividualPairStatus IndividualPairStatus => UserPair.IndividualPairStatus;
    public bool IsDirectlyPaired => IndividualPairStatus != IndividualPairStatus.None;
    public bool IsOneSidedPair => IndividualPairStatus == IndividualPairStatus.OneSided;
    public bool IsOnline => CachedPlayer != null;

    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    public CharacterData? LastReceivedCharacterData { get; set; }
    public string? PlayerName => CachedPlayer?.PlayerName ?? string.Empty;
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;
    public ushort? VisibleHomeWorldId => _visibleHomeWorldId;

    public UserData UserData => UserPair.User;

    public UserFullPairDto UserPair { get; set; }
    private PairHandler? CachedPlayer { get; set; }

    public void AddContextMenu(IMenuOpenedArgs args)
    {
        if (CachedPlayer == null || (args.Target is not MenuTargetDefault target) || target.TargetObjectId != CachedPlayer.PlayerCharacterId || IsPaused) return;

        args.AddMenuItem(new MenuItem()
        {
            Name = _openProfileSeString,
            OnClicked = (a) => _mediator.Publish(new ProfileOpenStandaloneMessage(this)),
            UseDefaultPrefix = false,
            PrefixChar = 'M',
            PrefixColor = 526
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = _reapplyDataSeString,
            OnClicked = (a) => ApplyLastReceivedData(forced: true),
            UseDefaultPrefix = false,
            PrefixChar = 'M',
            PrefixColor = 526
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = _changePermissionsSeString,
            OnClicked = (a) => _mediator.Publish(new OpenPermissionWindow(this)),
            UseDefaultPrefix = false,
            PrefixChar = 'M',
            PrefixColor = 526
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = _cyclePauseStateSeString,
            OnClicked = (a) => _mediator.Publish(new CyclePauseMessage(ServerIndex, UserData)),
            UseDefaultPrefix = false,
            PrefixChar = 'M',
            PrefixColor = 526
        });
    }

    public void ApplyData(OnlineUserCharaDataDto data)
    {
        _applicationCts = _applicationCts.CancelRecreate();
        LastReceivedCharacterData = data.CharaData;

        if (CachedPlayer == null)
        {
            _logger.LogDebug("Received Data for {uid} but CachedPlayer does not exist, waiting", data.User.UID);
            _ = Task.Run(async () =>
            {
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                var appToken = _applicationCts.Token;
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, appToken);
                while (CachedPlayer == null && !combined.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, combined.Token).ConfigureAwait(false);
                }

                if (!combined.IsCancellationRequested)
                {
                    _logger.LogDebug("Applying delayed data for {uid}", data.User.UID);
                    ApplyLastReceivedData();
                }
            });
            return;
        }

        ApplyLastReceivedData();
    }

    public void ApplyLastReceivedData(bool forced = false)
    {
        if (CachedPlayer == null) return;
        if (LastReceivedCharacterData == null) return;

        CachedPlayer.ApplyCharacterData(Guid.NewGuid(), RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!, forced);
    }

    public void CreateCachedPlayer(OnlineUserIdentDto? dto = null)
    {
        try
        {
            _creationSemaphore.Wait();

            if (CachedPlayer != null) return;

            if (dto == null && _onlineUserIdentDto == null)
            {
                CachedPlayer?.Dispose();
                CachedPlayer = null;
                return;
            }
            if (dto != null)
            {
                _onlineUserIdentDto = dto;
            }

            CachedPlayer?.Dispose();
            CachedPlayer = _cachedPlayerFactory.Create(this);
        }
        finally
        {
            _creationSemaphore.Release();
        }
    }

    public string? GetNote()
    {
        return _serverConfigurationManager.GetNoteForUid(ServerIndex, UserData.UID);
    }

    public string GetPlayerNameHash()
    {
        return CachedPlayer?.PlayerNameHash ?? string.Empty;
    }

    public bool HasAnyConnection()
    {
        return UserPair.Groups.Any() || UserPair.IndividualPairStatus != IndividualPairStatus.None;
    }

    public void MarkOffline(bool wait = true)
    {
        try
        {
            if (wait)
                _creationSemaphore.Wait();
            LastReceivedCharacterData = null;
            var player = CachedPlayer;
            CachedPlayer = null;
            player?.Dispose();
            _onlineUserIdentDto = null;
            UpdateVisibleHomeWorldId(null);
        }
        finally
        {
            if (wait)
                _creationSemaphore.Release();
        }
    }

    public void SetNote(string note)
    {
        _serverConfigurationManager.SetNoteForUid(ServerIndex, UserData.UID, note);
    }

    internal void UpdateVisibleHomeWorldId(ushort? homeWorldId)
    {
        if (_visibleHomeWorldId == homeWorldId)
            return;

        _visibleHomeWorldId = homeWorldId;
        _mediator.Publish(new RefreshUiMessage());
    }

    internal void SetIsUploading()
    {
        CachedPlayer?.SetUploading();
    }

    private CharacterData? RemoveNotSyncedFiles(CharacterData? data)
    {
        _logger.LogTrace("Removing not synced files");
        if (data == null)
        {
            _logger.LogTrace("Nothing to remove");
            return data;
        }

        bool disableIndividualAnimations = (UserPair.OtherPermissions.IsDisableAnimations() || UserPair.OwnPermissions.IsDisableAnimations());
        bool disableIndividualVFX = (UserPair.OtherPermissions.IsDisableVFX() || UserPair.OwnPermissions.IsDisableVFX());
        bool disableIndividualSounds = (UserPair.OtherPermissions.IsDisableSounds() || UserPair.OwnPermissions.IsDisableSounds());

        _logger.LogTrace("Disable: Sounds: {disableIndividualSounds}, Anims: {disableIndividualAnims}; " +
            "VFX: {disableGroupSounds}",
            disableIndividualSounds, disableIndividualAnimations, disableIndividualVFX);

        if (disableIndividualAnimations || disableIndividualSounds || disableIndividualVFX)
        {
            _logger.LogTrace("Data cleaned up: Animations disabled: {disableAnimations}, Sounds disabled: {disableSounds}, VFX disabled: {disableVFX}",
                disableIndividualAnimations, disableIndividualSounds, disableIndividualVFX);
            foreach (var objectKind in data.FileReplacements.Select(k => k.Key))
            {
                data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                    .Where(f => (!disableIndividualSounds || !f.GamePaths.Any(p => p.EndsWith("scd", StringComparison.OrdinalIgnoreCase)))
                        && (!disableIndividualAnimations || !f.GamePaths.Any(p => p.EndsWith("tmb", StringComparison.OrdinalIgnoreCase) || p.EndsWith("pap", StringComparison.OrdinalIgnoreCase)))
                        && (!disableIndividualVFX || !f.GamePaths.Any(p => p.EndsWith("atex", StringComparison.OrdinalIgnoreCase) || p.EndsWith("avfx", StringComparison.OrdinalIgnoreCase))))
                    .ToList();
            }
        }

        return data;
    }
}
