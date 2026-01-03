using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using LaciSynchroni.Common.Routes;
using LaciSynchroni.Utils;
using LaciSynchroni.Common.SignalR;
using LaciSynchroni.SyncConfiguration.Models;
using LaciSynchroni.WebAPI;
using Microsoft.AspNetCore.Http.Connections;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace LaciSynchroni.UI;

/// <summary>
/// Partial class containing server configuration UI methods.
/// </summary>
public partial class SettingsUi
{
    private void DrawMultiServerInterfaceTable()
    {
        _uiShared.BigText($"Registered services");
        ImGuiHelpers.ScaledDummy(new Vector2(5, 5));

        if (ImGui.BeginTable("MultiServerInterface", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn($"Server Name", ImGuiTableColumnFlags.None, 3);
            ImGui.TableSetupColumn($"Status", ImGuiTableColumnFlags.None, 1);
            ImGui.TableSetupColumn($"Uri", ImGuiTableColumnFlags.None, 2);
            ImGui.TableSetupColumn($"Hub", ImGuiTableColumnFlags.None, 3);
            ImGui.TableSetupColumn($"Connection", ImGuiTableColumnFlags.None, 1);
            ImGui.TableHeadersRow();

            var serverList = _serverConfigurationManager.GetServerInfo();
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();

            foreach (var server in serverList)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);

                bool isSelected = (_lastSelectedServerIndex == server.Id);
                ImRaii.PushId(server.Id);
                if (ImGui.Selectable("##row", isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                    new Vector2(0, rowHeight)))
                {
                    if (_lastSelectedServerIndex != server.Id)
                    {
                        _uiShared.ResetOAuthTasksState();
                        _secretKeysConversionCts = _secretKeysConversionCts.CancelRecreate();
                        _secretKeysConversionTask = null;
                        _lastSelectedServerIndex = server.Id;
                    }
                }
                ImGui.PopID();

                ImGui.SameLine();
                ImGui.TextUnformatted(server.Name);

                ImGui.TableNextColumn();
                DrawServerStatus(server.Id);

                ImGui.TableNextColumn();
                ImGui.Text(server.Uri);

                ImGui.TableNextColumn();
                ImGui.Text(string.IsNullOrEmpty(server.HubUri) ? (server.Uri + IServerHub.Path) : server.HubUri);

                ImGui.TableNextColumn();
                DrawMultiServerConnectButton(server.Id, server.Name);

            }

            ImGui.EndTable();
        }
    }

    private void DrawServerStatus(int serverId)
    {
        if (_apiController.ConnectedServerIndexes.Any(p => p == serverId))
        {
            UiSharedService.ColorTextWrapped("Online", ImGuiColors.ParsedGreen);
        }
        else
            UiSharedService.ColorTextWrapped("Offline", ImGuiColors.DalamudRed);
    }

    private void DrawMultiServerConnectButton(int serverId, string serverName)
    {
        bool isConnected = _apiController.IsServerConnected(serverId);
        bool isConnecting = _apiController.IsServerConnecting(serverId);
        bool isDisconnectable = isConnecting || isConnected;
        var color = UiSharedService.GetBoolColor(!isDisconnectable);
        var connectedIcon = isDisconnectable ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;

        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            if (_uiShared.IconButton(connectedIcon, serverId.ToString()))
            {
                if (isDisconnectable)
                {
                    _serverConfigurationManager.GetServerByIndex(serverId).FullPause = true;
                    _serverConfigurationManager.Save();
                    _ = _apiController.PauseConnectionAsync(serverId);
                }
                else
                {
                    _serverConfigurationManager.GetServerByIndex(serverId).FullPause = false;
                    _serverConfigurationManager.Save();
                    _ = _apiController.CreateConnectionsAsync(serverId);
                }
            }
        }

        UiSharedService.AttachToolTip(isConnected ?
           "Disconnect from " + serverName :
           "Connect to " + serverName);
    }

    private void DrawSpeedTest()
    {
        using (ImRaii.Disabled(!_apiController.AnyServerConnected))
        {
            ImGuiHelpers.ScaledDummy(5);
            using var tree = ImRaii.TreeNode($"Speed Test to {_lastSelectedServerName}");
            if (tree)
            {
                if (_downloadServersTask == null || ((_downloadServersTask?.IsCompleted ?? false) && (!_downloadServersTask?.IsCompletedSuccessfully ?? false)))
                {
                    var isServiceConnected = _apiController.IsServerConnected(_lastSelectedServerIndex);
                    using (ImRaii.Disabled(!isServiceConnected))
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.GroupArrowsRotate, "Update Download Server List"))
                        {
                            _downloadServersTask = GetDownloadServerList(_lastSelectedServerIndex);
                        }
                    }
                    if (!isServiceConnected)
                    {
                        UiSharedService.AttachToolTip($"Connect to {_lastSelectedServerName} to run the speed test.");
                    }
                }
                if (_downloadServersTask != null && _downloadServersTask.IsCompleted && !_downloadServersTask.IsCompletedSuccessfully)
                {
                    UiSharedService.ColorTextWrapped("Failed to get download servers from service, see /xllog for more information", ImGuiColors.DalamudRed);
                }
                if (_downloadServersTask != null && _downloadServersTask.IsCompleted && _downloadServersTask.IsCompletedSuccessfully)
                {
                    if (_speedTestTask == null || _speedTestTask.IsCompleted)
                    {
                        if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowRight, "Start Speedtest"))
                        {
                            _speedTestTask = RunSpeedTest(_downloadServersTask.Result!, _speedTestCts?.Token ?? CancellationToken.None);
                        }
                    }
                    else if (!_speedTestTask.IsCompleted)
                    {
                        UiSharedService.ColorTextWrapped("Running Speedtest to File Servers...", ImGuiColors.DalamudYellow);
                        UiSharedService.ColorTextWrapped("Please be patient, depending on usage and load this can take a while.", ImGuiColors.DalamudYellow);
                        if (_uiShared.IconTextButton(FontAwesomeIcon.Ban, "Cancel speedtest"))
                        {
                            _speedTestCts?.Cancel();
                            _speedTestCts?.Dispose();
                            _speedTestCts = new();
                        }
                    }
                    if (_speedTestTask != null && _speedTestTask.IsCompleted)
                    {
                        if (_speedTestTask.Result != null && _speedTestTask.Result.Count != 0)
                        {
                            foreach (var result in _speedTestTask.Result)
                            {
                                UiSharedService.TextWrapped(result);
                            }
                        }
                        else
                        {
                            UiSharedService.ColorTextWrapped("Speedtest completed with no results", ImGuiColors.DalamudYellow);
                        }
                    }
                }
            }
            ImGuiHelpers.ScaledDummy(5);
        }
    }

    private async Task<(bool Success, bool partialSuccess, string Result)> ConvertSecretKeysToUIDs(ServerStorage serverStorage, CancellationToken token)
    {
        List<Authentication> failedConversions = serverStorage.Authentications.Where(u => u.SecretKeyIdx == -1 && string.IsNullOrEmpty(u.UID)).ToList();
        List<Authentication> conversionsToAttempt = serverStorage.Authentications.Where(u => u.SecretKeyIdx != -1 && string.IsNullOrEmpty(u.UID)).ToList();
        List<Authentication> successfulConversions = [];
        Dictionary<string, List<Authentication>> secretKeyMapping = new(StringComparer.Ordinal);
        foreach (var authEntry in conversionsToAttempt)
        {
            if (!serverStorage.SecretKeys.TryGetValue(authEntry.SecretKeyIdx, out var secretKey))
            {
                failedConversions.Add(authEntry);
                continue;
            }

            if (!secretKeyMapping.TryGetValue(secretKey.Key, out List<Authentication>? authList))
            {
                secretKeyMapping[secretKey.Key] = authList = [];
            }

            authList.Add(authEntry);
        }

        if (secretKeyMapping.Count == 0)
        {
            return (false, false, $"Failed to convert {failedConversions.Count} entries: " + string.Join(", ", failedConversions.Select(k => k.CharacterName)));
        }

        var baseUri = serverStorage.GetAuthServerUri().Replace("wss://", "https://").Replace("ws://", "http://");
        var oauthCheckUri = AuthRoutes.GetUIDsBasedOnSecretKeyFullPath(new Uri(baseUri));
        var requestContent = JsonContent.Create(secretKeyMapping.Select(k => k.Key).ToList());
        HttpRequestMessage requestMessage = new(HttpMethod.Post, oauthCheckUri);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverStorage.OAuthToken);
        requestMessage.Content = requestContent;

        using var response = await _httpClient.SendAsync(requestMessage, token).ConfigureAwait(false);
        Dictionary<string, string>? secretKeyUidMapping = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>
            (await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
        if (secretKeyUidMapping == null)
        {
            return (false, false, $"Failed to parse the server response. Failed to convert all entries.");
        }

        foreach (var entry in secretKeyMapping)
        {
            if (!secretKeyUidMapping.TryGetValue(entry.Key, out var assignedUid) || string.IsNullOrEmpty(assignedUid))
            {
                failedConversions.AddRange(entry.Value);
                continue;
            }

            foreach (var auth in entry.Value)
            {
                auth.UID = assignedUid;
                successfulConversions.Add(auth);
            }
        }

        if (successfulConversions.Count > 0)
            _serverConfigurationManager.Save();

        StringBuilder sb = new();
        sb.Append("Conversion complete." + Environment.NewLine);
        sb.Append($"Successfully converted {successfulConversions.Count} entries." + Environment.NewLine);
        if (failedConversions.Count > 0)
        {
            sb.Append($"Failed to convert {failedConversions.Count} entries, assign those manually: ");
            sb.Append(string.Join(", ", failedConversions.Select(k => k.CharacterName)));
        }

        return (true, failedConversions.Count != 0, sb.ToString());
    }
}
