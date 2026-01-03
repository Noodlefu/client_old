using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using LaciSynchroni.Services;
using LaciSynchroni.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Text.Json;

namespace LaciSynchroni.UI;

/// <summary>
/// Partial class containing debug and file storage UI methods.
/// </summary>
public partial class SettingsUi
{
    private void DrawDebug()
    {
        _lastTab = "Debug";

        _uiShared.BigText("Debug");
#if DEBUG
        if (LastCreatedCharacterData != null && ImGui.TreeNode("Last created character data"))
        {
            foreach (var l in JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }).Split('\n'))
            {
                ImGui.TextUnformatted($"{l}");
            }

            ImGui.TreePop();
        }
#endif
        if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "[DEBUG] Copy Last created Character Data to clipboard"))
        {
            if (LastCreatedCharacterData != null)
            {
                ImGui.SetClipboardText(JsonSerializer.Serialize(LastCreatedCharacterData, new JsonSerializerOptions() { WriteIndented = true }));
            }
            else
            {
                ImGui.SetClipboardText("ERROR: No created character data, cannot copy.");
            }
        }
        UiSharedService.AttachToolTip("Use this when reporting mods being rejected from the server.");

        _uiShared.DrawCombo("Log Level", Enum.GetValues<LogLevel>(), (l) => l.ToString(), (l) =>
        {
            _configService.Current.LogLevel = l;
            _configService.Save();
        }, _configService.Current.LogLevel);

        bool logPerformance = _configService.Current.LogPerformance;
        if (ImGui.Checkbox("Log Performance Counters", ref logPerformance))
        {
            _configService.Current.LogPerformance = logPerformance;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Enabling this can incur a (slight) performance impact. Enabling this for extended periods of time is not recommended.");

        using (ImRaii.Disabled(!logPerformance))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Performance Stats to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats();
            }
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.StickyNote, "Print Performance Stats (last 60s) to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats(60);
            }
        }

        bool stopWhining = _configService.Current.DebugStopWhining;
        if (ImGui.Checkbox("Do not notify for modified game files or enabled LOD", ref stopWhining))
        {
            _configService.Current.DebugStopWhining = stopWhining;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Having modified game files will still mark your logs with UNSUPPORTED and you will not receive support, message shown or not." + UiSharedService.TooltipSeparator
            + "Keeping LOD enabled can lead to more crashes. Use at your own risk.");

        bool useExtendedUploadTimeout = _configService.Current.DebugExtendedUploadTimeout;
        if (ImGui.Checkbox("Use extended upload timeout.", ref useExtendedUploadTimeout))
        {
            _configService.Current.DebugExtendedUploadTimeout = useExtendedUploadTimeout;
            _configService.Save();
        }
        _uiShared.DrawHelpText("Unless you have been asked to enable this, please leave it unchecked." + UiSharedService.TooltipSeparator
            + "This may cause communication issues with servers. Any change to this setting requires a plugin restart.");

        DrawRenderLocks();


    }

    private void DrawFileStorageSettings()
    {
        _lastTab = "FileCache";

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        _uiShared.BigText("Storage");

        UiSharedService.TextWrapped($"{_dalamudUtilService.GetPluginName()} stores downloaded files from paired people permanently. This is to improve loading performance and requiring less downloads. " +
            "The storage governs itself by clearing data beyond the set storage size. Please set the storage size accordingly. It is not necessary to manually clear the storage.");

        _uiShared.DrawFileScanState();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Monitoring Penumbra Folder: " + (_cacheMonitor.PenumbraWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.PenumbraWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("penumbraMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
            }
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted($"Monitoring {_dalamudUtilService.GetPluginName()} Storage Folder: " + (_cacheMonitor.SyncWatcher?.Path ?? "Not monitoring"));
        if (string.IsNullOrEmpty(_cacheMonitor.SyncWatcher?.Path))
        {
            ImGui.SameLine();
            using var id = ImRaii.PushId("laciMonitor");
            if (_uiShared.IconTextButton(FontAwesomeIcon.ArrowsToCircle, "Try to reinitialize Monitor"))
            {
                _cacheMonitor.StartFileWatcher(_configService.Current.CacheFolder);
            }
        }
        if (_cacheMonitor.SyncWatcher == null || _cacheMonitor.PenumbraWatcher == null)
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Play, "Resume Monitoring"))
            {
                _cacheMonitor.StartFileWatcher(_configService.Current.CacheFolder);
                _cacheMonitor.StartPenumbraWatcher(_ipcManager.Penumbra.ModDirectory);
                _cacheMonitor.InvokeScan();
            }
            UiSharedService.AttachToolTip($"Attempts to resume monitoring for both Penumbra and {_dalamudUtilService.GetPluginName()} storage. "
                + "Resuming the monitoring will also force a full scan to run." + Environment.NewLine
                + "If the button remains present after clicking it, consult /xllog for errors");
        }
        else
        {
            using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.Stop, "Stop Monitoring"))
                {
                    _cacheMonitor.StopMonitoring();
                }
            }
            UiSharedService.AttachToolTip($"Stops the monitoring for both Penumbra and {_dalamudUtilService.GetPluginName()} storage. "
                + $"Do not stop the monitoring, unless you plan to move the storage folders, to ensure correct functionality." + Environment.NewLine
                + "If you stop the monitoring to move folders around, resume it after you are finished moving the files."
                + UiSharedService.TooltipSeparator + "Hold CTRL to enable this button");
        }

        _uiShared.DrawCacheDirectorySetting();
        ImGui.AlignTextToFramePadding();
        if (_cacheMonitor.FileCacheSize >= 0)
            ImGui.TextUnformatted($"Currently utilized local storage: {UiSharedService.ByteToString(_cacheMonitor.FileCacheSize)}");
        else
            ImGui.TextUnformatted($"Currently utilized local storage: Calculating...");
        ImGui.TextUnformatted($"Remaining space free on drive: {UiSharedService.ByteToString(_cacheMonitor.FileCacheDriveFree)}");
        bool useFileCompactor = _configService.Current.UseCompactor;
        bool isLinux = _dalamudUtilService.IsWine;
        if (!useFileCompactor && !isLinux)
        {
            UiSharedService.ColorTextWrapped($"Hint: To free up space when using {_dalamudUtilService.GetPluginName()} consider enabling the File Compactor", ImGuiColors.DalamudYellow);
        }

        using (ImRaii.Disabled(isLinux || !_cacheMonitor.StorageIsNtfs))
        {
            if (ImGui.Checkbox("Use file compactor", ref useFileCompactor))
            {
                _configService.Current.UseCompactor = useFileCompactor;
                _configService.Save();
            }

            _uiShared.DrawHelpText("The file compactor can massively reduce your saved files. It might incur a minor penalty on loading files on a slow CPU." + Environment.NewLine
           + "It is recommended to leave it enabled to save on space.");
            ImGui.SameLine();
            if (!_fileCompactor.MassCompactRunning)
            {
                if (_uiShared.IconTextButton(FontAwesomeIcon.FileArchive, "Compact all files in storage"))
                {
                    _ = Task.Run(() =>
                    {
                        _fileCompactor.CompactStorage(compress: true);
                        _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                    });
                }
                UiSharedService.AttachToolTip($"This will run compression on all files in your current {_dalamudUtilService.GetPluginName()} storage." + Environment.NewLine
                    + "You do not need to run this manually if you keep the file compactor enabled.");
                ImGui.SameLine();
                if (_uiShared.IconTextButton(FontAwesomeIcon.File, "Decompact all files in storage"))
                {
                    _ = Task.Run(() =>
                    {
                        _fileCompactor.CompactStorage(compress: false);
                        _cacheMonitor.RecalculateFileCacheSize(CancellationToken.None);
                    });
                }
                UiSharedService.AttachToolTip($"This will run decompression on all files in your current {_dalamudUtilService.GetPluginName()} storage.");
            }
            else
            {
                UiSharedService.ColorText($"File compactor currently running ({_fileCompactor.Progress})", ImGuiColors.DalamudYellow);
            }

            ImGui.TextUnformatted("The file compactor is only available on Windows and NTFS drives.");
        }

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));

        ImGui.Separator();
        UiSharedService.TextWrapped($"File Storage validation can make sure that all files in your local {_dalamudUtilService.GetPluginName()} storage are valid. " +
            "Run the validation before you clear the storage for no reason. " + Environment.NewLine +
            "This operation, depending on how many files you have in your storage, can take a while and will be CPU and drive intensive.");
        using (ImRaii.Disabled(_validationTask?.IsCompleted == false))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Check, "Start File Storage Validation"))
            {
                _validationCts?.Cancel();
                _validationCts?.Dispose();
                _validationCts = new CancellationTokenSource();
                var token = _validationCts.Token;
                _validationTask = Task.Run(() => _fileCacheManager.ValidateLocalIntegrity(_validationProgress, token));
            }
        }
        if (_validationTask != null && !_validationTask.IsCompleted)
        {
            ImGui.SameLine();
            if (_uiShared.IconTextButton(FontAwesomeIcon.Times, "Cancel"))
            {
                _validationCts?.Cancel();
            }
        }

        if (_validationTask != null)
        {
            using (ImRaii.PushIndent(20f))
            {
                if (_validationTask.IsCompleted)
                {
                    UiSharedService.TextWrapped($"The storage validation has completed and removed {_validationTask.Result.Count} invalid files from storage.");
                }
                else
                {
                    UiSharedService.TextWrapped($"Storage validation is running: {_currentProgress.Item1}/{_currentProgress.Item2}");
                    if (_currentProgress.Item3 != null)
                    {
                        UiSharedService.TextWrapped($"Current item: {_currentProgress.Item3.ResolvedFilepath}");
                    }
                }
            }
        }
        ImGui.Separator();

        ImGuiHelpers.ScaledDummy(new Vector2(10, 10));
        ImGui.TextUnformatted("To clear the local storage accept the following disclaimer");
        ImGui.Indent();
        ImGui.Checkbox("##readClearCache", ref _readClearCache);
        ImGui.SameLine();
        UiSharedService.TextWrapped("I understand that: " + Environment.NewLine + "- By clearing the local storage I put the file servers of my connected service under extra strain by having to redownload all data."
            + Environment.NewLine + "- This is not a step to try to fix sync issues."
            + Environment.NewLine + "- This can make the situation of not getting other players data worse in situations of heavy file server load.");

        using (ImRaii.Disabled(!_readClearCache))
        {
            if (_uiShared.IconTextButton(FontAwesomeIcon.Trash, "Clear local storage") && UiSharedService.CtrlPressed() && _readClearCache)
            {
                _ = Task.Run(() =>
                {
                    foreach (var file in Directory.GetFiles(_configService.Current.CacheFolder))
                    {
                        File.Delete(file);
                    }
                });
            }
            UiSharedService.AttachToolTip("You normally do not need to do this. THIS IS NOT SOMETHING YOU SHOULD BE DOING TO TRY TO FIX SYNC ISSUES." + Environment.NewLine
                + "This will solely remove all downloaded data from all players and will require you to re-download everything again." + Environment.NewLine
                + "Laci storage is self-clearing and will not surpass the limit you have set it to." + Environment.NewLine
                + "If you still think you need to do this hold CTRL while pressing the button.");
        }

        ImGui.Unindent();
    }

    private void DrawRenderLocks()
    {
        _uiShared.BigText("Debug - Render Locks");
        UiSharedService.TextWrapped(
            "Render locks are utilized in scenarios where you have more than one server connected. " +
            "One render lock should exist for each target Laci draws on.");
        UiSharedService.TextWrapped(
            "To force swap a lock for debug purposes, release the lock here. Then, pause and enable the pair" +
            "for the server you want to lock to exist for.");

        var currentLocks = _concurrentPairLockService.GetCurrentRenderLocks().ToList();
        if (currentLocks.Count <= 0)
        {
            UiSharedService.TextWrapped("No locks to display. Locks will only establish when a pair is actually visible to you.");
        }
        else
        {
            if (ImGui.BeginTable("LockDisplay", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Character Name", ImGuiTableColumnFlags.None, 2);
                ImGui.TableSetupColumn("Character Ident Hash (Shortened)", ImGuiTableColumnFlags.None, 4);
                ImGui.TableSetupColumn("Server Name", ImGuiTableColumnFlags.None, 5);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.None, 2);
                ImGui.TableHeadersRow();
                currentLocks.ForEach(DrawRenderLockRow);
                ImGui.EndTable();
            }
        }
    }

    private void DrawRenderLockRow(ConcurrentPairLockService.LockData lockData)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // Shorten the actual character name in case someone screenshots this - rest is shown on hover
        ImGui.TextUnformatted(AnonymityUtils.ShortenPlayerName(lockData.CharName));
        UiSharedService.AttachToolTip(lockData.CharName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(lockData.PlayerHash.Substring(0, 15));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(_serverConfigurationManager.GetServerNameByIndex(lockData.Index));

        ImGui.TableNextColumn();
        // It might be useful to allow force releases of locks for debugging purpose, for example to explicitly flip a lock
        // from one server to the next
        if (_uiShared.IconTextButton(FontAwesomeIcon.Stop, "Release lock"))
        {
            _concurrentPairLockService.ReleaseRenderLock(lockData.PlayerHash, lockData.Index);
        }
    }
}
