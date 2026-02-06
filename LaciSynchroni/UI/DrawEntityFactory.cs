using LaciSynchroni.Common.Dto.Group;
using LaciSynchroni.PlayerData.Pairs;
using LaciSynchroni.Services.CharaData;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.Services.ServerConfiguration;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.UI.Components;
using LaciSynchroni.UI.Handlers;
using LaciSynchroni.WebAPI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;

namespace LaciSynchroni.UI;

public class DrawEntityFactory(ApiController apiController, IdDisplayHandler uidDisplayHandler,
    SelectTagForPairUi selectTagForPairUi, SyncMediator mediator,
    TagHandler tagHandler, SelectPairForTagUi selectPairForTagUi,
    ServerConfigurationManager serverConfigurationManager, UiSharedService uiSharedService,
    PlayerPerformanceConfigService playerPerformanceConfigService, CharaDataManager charaDataManager,
    SyncConfigService configService)
{
    private readonly ApiController _apiController = apiController;
    private readonly SyncMediator _mediator = mediator;
    private readonly SelectPairForTagUi _selectPairForTagUi = selectPairForTagUi;
    private readonly ServerConfigurationManager _serverConfigurationManager = serverConfigurationManager;
    private readonly UiSharedService _uiSharedService = uiSharedService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfigService = playerPerformanceConfigService;
    private readonly CharaDataManager _charaDataManager = charaDataManager;
    private readonly SelectTagForPairUi _selectTagForPairUi = selectTagForPairUi;
    private readonly TagHandler _tagHandler = tagHandler;
    private readonly IdDisplayHandler _uidDisplayHandler = uidDisplayHandler;
    private readonly SyncConfigService _configService = configService;

    public DrawFolderGroup CreateDrawGroupFolder(GroupFullInfoWithServer groupFullInfoDto,
        Dictionary<Pair, List<GroupFullInfoDto>> filteredPairs,
        IImmutableList<Pair> allPairs)
    {
        var pairsToRender = filteredPairs.Select(p => CreateDrawPair(groupFullInfoDto, p)).ToImmutableList();
        return new DrawFolderGroup(groupFullInfoDto.ServerIndex, groupFullInfoDto.GroupFullInfo, _apiController,
            pairsToRender,
            allPairs, _tagHandler, _uidDisplayHandler, _mediator, _uiSharedService);
    }

    public DrawFolderTag CreateDrawTagFolder(TagWithServerIndex tag,
        Dictionary<Pair, List<GroupFullInfoDto>> filteredPairs,
        IImmutableList<Pair> allPairs)
    {
        return new(tag, filteredPairs.Select(u => CreateDrawPair(tag.AsImGuiId(), u.Key, u.Value, null)).ToImmutableList(),
            allPairs, _tagHandler, _apiController, _selectPairForTagUi, _uiSharedService, _serverConfigurationManager);
    }

    public IDrawFolder CreateDrawTagFolderForCustomTag(string specialTag,
        Dictionary<Pair, List<GroupFullInfoDto>> filteredPairs,
        IImmutableList<Pair> allPairs)
    {
        var drawPairs = filteredPairs.Select(u => CreateDrawPair(specialTag, u.Key, u.Value, null)).ToImmutableList();

        if (string.Equals(specialTag, TagHandler.CustomVisibleTag, StringComparison.Ordinal))
        {
            return new DrawVisibleTagFolder(drawPairs, allPairs, _tagHandler, _uiSharedService, _apiController, _configService, _mediator, _playerPerformanceConfigService);
        }

        return new DrawCustomTag(specialTag, drawPairs,
            allPairs, _tagHandler, _uiSharedService);
    }

    public DrawUserPair CreateDrawPair(string id, Pair user, List<GroupFullInfoDto> groups, GroupFullInfoDto? currentGroup)
    {
        return new DrawUserPair(id + user.UserData.UID, user, groups, currentGroup, _apiController, _uidDisplayHandler,
            _mediator, _selectTagForPairUi, _serverConfigurationManager, _uiSharedService, _playerPerformanceConfigService,
            _charaDataManager);
    }

    private DrawUserPair CreateDrawPair(GroupFullInfoWithServer groupFullInfoWithServer, KeyValuePair<Pair, List<GroupFullInfoDto>> filteredPairs)
    {
        var pair = filteredPairs.Key;
        var groups = filteredPairs.Value;
        var id = groupFullInfoWithServer.GroupFullInfo.Group.GID + pair.UserData.UID;
        return CreateDrawPair(id, pair, groups, groupFullInfoWithServer.GroupFullInfo);
    }

}
