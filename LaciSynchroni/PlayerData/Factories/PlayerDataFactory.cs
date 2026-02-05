using FFXIVClientStructs.FFXIV.Client.Game.Character;
using LaciSynchroni.Common.Data.Enum;
using LaciSynchroni.FileCache;
using LaciSynchroni.Interop.Ipc;
using LaciSynchroni.PlayerData.Data;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.Services;
using LaciSynchroni.Services.Mediator;
using LaciSynchroni.SyncConfiguration;
using LaciSynchroni.SyncConfiguration.Models;
using Microsoft.Extensions.Logging;

namespace LaciSynchroni.PlayerData.Factories;

public class PlayerDataFactory
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileCacheManager _fileCacheManager;
    private readonly IpcManager _ipcManager;
    private readonly ILogger<PlayerDataFactory> _logger;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly XivDataAnalyzer _modelAnalyzer;
    private readonly SyncMediator _syncMediator;
    private readonly SyncConfigService _syncConfigService;
    private readonly TransientResourceManager _transientResourceManager;

    public PlayerDataFactory(ILogger<PlayerDataFactory> logger, DalamudUtilService dalamudUtil, IpcManager ipcManager,
        TransientResourceManager transientResourceManager, FileCacheManager fileReplacementFactory,
        PerformanceCollectorService performanceCollector, XivDataAnalyzer modelAnalyzer, SyncMediator syncMediator,
        SyncConfigService syncConfigService)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _transientResourceManager = transientResourceManager;
        _fileCacheManager = fileReplacementFactory;
        _performanceCollector = performanceCollector;
        _modelAnalyzer = modelAnalyzer;
        _syncMediator = syncMediator;
        _syncConfigService = syncConfigService;
        _logger.LogTrace("Creating {This}", GetType().Name);
    }

    public async Task<CharacterDataFragment?> BuildCharacterData(GameObjectHandler playerRelatedObject, CancellationToken token)
    {
        if (!_ipcManager.Initialized)
        {
            throw new InvalidOperationException("Penumbra or Glamourer is not connected");
        }

        bool pointerIsZero = true;
        try
        {
            pointerIsZero = playerRelatedObject.Address == IntPtr.Zero;
            try
            {
                pointerIsZero = await CheckForNullDrawObject(playerRelatedObject.Address).ConfigureAwait(false);
            }
            catch
            {
                pointerIsZero = true;
                _logger.LogDebug("NullRef for {object}", playerRelatedObject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data for {object}", playerRelatedObject);
        }

        if (pointerIsZero)
        {
            _logger.LogTrace("Pointer was zero for {objectKind}", playerRelatedObject.ObjectKind);
            return null;
        }

        try
        {
            return await _performanceCollector.LogPerformance(this, $"CreateCharacterData>{playerRelatedObject.ObjectKind}", async () =>
            {
                return await CreateCharacterData(playerRelatedObject, token).ConfigureAwait(false);
            }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cancelled creating Character data for {object}", playerRelatedObject);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to create {object} data", playerRelatedObject);
        }

        return null;
    }

    private async Task<bool> CheckForNullDrawObject(IntPtr playerPointer)
    {
        return await _dalamudUtil.RunOnFrameworkThread(() => CheckForNullDrawObjectUnsafe(playerPointer)).ConfigureAwait(false);
    }

    private unsafe bool CheckForNullDrawObjectUnsafe(IntPtr playerPointer)
    {
        if (playerPointer == IntPtr.Zero) return true;
        var character = (Character*)playerPointer;
        if (character == null) return true;
        return character->GameObject.DrawObject == null;
    }

    private async Task<CharacterDataFragment> CreateCharacterData(GameObjectHandler playerRelatedObject, CancellationToken ct)
    {
        var objectKind = playerRelatedObject.ObjectKind;
        CharacterDataFragment fragment = objectKind == ObjectKind.Player ? new CharacterDataFragmentPlayer() : new();

        _logger.LogDebug("Building character data for {obj}", playerRelatedObject);

        // wait until chara is not drawing and present so nothing spontaneously explodes
        await _dalamudUtil.WaitWhileCharacterIsDrawing(_logger, playerRelatedObject, Guid.NewGuid(), 30000, ct: ct).ConfigureAwait(false);
        int totalWaitTime = 10000;
        while (!await _dalamudUtil.IsObjectPresentAsync(await _dalamudUtil.CreateGameObjectAsync(playerRelatedObject.Address).ConfigureAwait(false)).ConfigureAwait(false) && totalWaitTime > 0)
        {
            _logger.LogTrace("Character is null but it shouldn't be, waiting");
            await Task.Delay(50, ct).ConfigureAwait(false);
            totalWaitTime -= 50;
        }

        ct.ThrowIfCancellationRequested();

        Dictionary<string, List<ushort>>? boneIndices =
            objectKind != ObjectKind.Player
            ? null
            : await _dalamudUtil.RunOnFrameworkThread(() => _modelAnalyzer.GetSkeletonBoneIndices(playerRelatedObject)).ConfigureAwait(false);

        DateTime start = DateTime.UtcNow;

        // penumbra call, it's currently broken
        Dictionary<string, HashSet<string>>? resolvedPaths;

        resolvedPaths = (await _ipcManager.Penumbra.GetCharacterData(_logger, playerRelatedObject).ConfigureAwait(false));
        if (resolvedPaths == null) throw new InvalidOperationException("Penumbra returned null data");

        ct.ThrowIfCancellationRequested();

        var fileReplacements = new HashSet<FileReplacement>(
            resolvedPaths.Select(c => new FileReplacement([.. c.Value], c.Key)),
            FileReplacementComparer.Instance);
        fileReplacements.RemoveWhere(p => !p.HasFileReplacement);
        fileReplacements.RemoveWhere(c => c.GamePaths.Any(g => !CacheMonitor.AllowedFileExtensions.Contains(Path.GetExtension(g), StringComparer.OrdinalIgnoreCase)));
        fragment.FileReplacements = fileReplacements;

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("== Static Replacements ==");
        foreach (var replacement in fragment.FileReplacements.Where(i => i.HasFileReplacement).OrderBy(i => i.GamePaths.First(), StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("=> {repl}", replacement);
            ct.ThrowIfCancellationRequested();
        }

        await _transientResourceManager.WaitForRecording(ct).ConfigureAwait(false);

        // if it's pet then it's summoner, if it's summoner we actually want to keep all filereplacements alive at all times
        // or we get into redraw city for every change and nothing works properly
        if (objectKind == ObjectKind.Pet)
        {
            foreach (var item in fragment.FileReplacements.Where(i => i.HasFileReplacement).SelectMany(p => p.GamePaths))
            {
                if (_transientResourceManager.AddTransientResource(objectKind, item))
                {
                    _logger.LogDebug("Marking static {item} for Pet as transient", item);
                }
            }

            _logger.LogTrace("Clearing {count} Static Replacements for Pet", fragment.FileReplacements.Count);
            fragment.FileReplacements.Clear();
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Handling transient update for {obj}", playerRelatedObject);

        // remove all potentially gathered paths from the transient resource manager that are resolved through static resolving
        _transientResourceManager.ClearTransientPaths(objectKind, fragment.FileReplacements.SelectMany(c => c.GamePaths).ToList());

        // get all remaining paths and resolve them
        var transientPaths = ManageSemiTransientData(objectKind);
        var resolvedTransientPaths = await GetFileReplacementsFromPaths(transientPaths, new HashSet<string>(StringComparer.Ordinal)).ConfigureAwait(false);

        _logger.LogDebug("== Transient Replacements ==");
        foreach (var replacement in resolvedTransientPaths.Select(c => new FileReplacement([.. c.Value], c.Key)).OrderBy(f => f.ResolvedPath, StringComparer.Ordinal))
        {
            _logger.LogDebug("=> {repl}", replacement);
            fragment.FileReplacements.Add(replacement);
        }

        // clean up all semi transient resources that don't have any file replacement (aka null resolve)
        _transientResourceManager.CleanUpSemiTransientResources(objectKind, [.. fragment.FileReplacements]);

        ct.ThrowIfCancellationRequested();

        // make sure we only return data that actually has file replacements
        fragment.FileReplacements = new HashSet<FileReplacement>(fragment.FileReplacements.Where(v => v.HasFileReplacement).OrderBy(v => v.ResolvedPath, StringComparer.Ordinal), FileReplacementComparer.Instance);

        // gather up data from ipc
        Task<string> getHeelsOffset = _ipcManager.Heels.GetOffsetAsync();
        Task<string> getGlamourerData = _ipcManager.Glamourer.GetCharacterCustomizationAsync(playerRelatedObject.Address);
        Task<string?> getCustomizeData = _ipcManager.CustomizePlus.GetScaleAsync(playerRelatedObject.Address);
        Task<string> getHonorificTitle = _ipcManager.Honorific.GetTitle();
        fragment.GlamourerString = await getGlamourerData.ConfigureAwait(false);
        _logger.LogDebug("Glamourer is now: {data}", fragment.GlamourerString);
        var customizeScale = await getCustomizeData.ConfigureAwait(false);
        fragment.CustomizePlusScale = customizeScale ?? string.Empty;
        _logger.LogDebug("Customize is now: {data}", fragment.CustomizePlusScale);

        if (objectKind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer)!;
            playerFragment.ManipulationString = _ipcManager.Penumbra.GetMetaManipulations();

            playerFragment!.HonorificData = await getHonorificTitle.ConfigureAwait(false);
            _logger.LogDebug("Honorific is now: {data}", playerFragment!.HonorificData);

            playerFragment!.HeelsData = await getHeelsOffset.ConfigureAwait(false);
            _logger.LogDebug("Heels is now: {heels}", playerFragment!.HeelsData);

            playerFragment!.MoodlesData = await _ipcManager.Moodles.GetStatusAsync(playerRelatedObject.Address).ConfigureAwait(false) ?? string.Empty;
            _logger.LogDebug("Moodles is now: {moodles}", playerFragment!.MoodlesData);

            playerFragment!.PetNamesData = _ipcManager.PetNames.GetLocalNames();
            _logger.LogDebug("Pet Nicknames is now: {petnames}", playerFragment!.PetNamesData);
        }

        ct.ThrowIfCancellationRequested();

        var toCompute = fragment.FileReplacements.Where(f => !f.IsFileSwap).ToArray();
        _logger.LogDebug("Getting Hashes for {amount} Files", toCompute.Length);
        var computedPaths = _fileCacheManager.GetFileCachesByPaths(toCompute.Select(c => c.ResolvedPath).ToArray());
        foreach (var file in toCompute)
        {
            ct.ThrowIfCancellationRequested();
            file.Hash = computedPaths[file.ResolvedPath]?.Hash ?? string.Empty;
        }
        var removed = fragment.FileReplacements.RemoveWhere(f => !f.IsFileSwap && string.IsNullOrEmpty(f.Hash));
        if (removed > 0)
        {
            _logger.LogDebug("Removed {amount} of invalid files", removed);
        }

        ct.ThrowIfCancellationRequested();

        if (objectKind == ObjectKind.Player)
        {
            try
            {
                await VerifyPlayerAnimationBones(boneIndices, (fragment as CharacterDataFragmentPlayer)!, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                _logger.LogDebug(e, "Cancelled during player animation verification");
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to verify player animations, continuing without further verification");
            }
        }

        _logger.LogInformation("Building character data for {obj} took {time}ms", objectKind, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start.Ticks).TotalMilliseconds);

        return fragment;
    }

    private async Task VerifyPlayerAnimationBones(Dictionary<string, List<ushort>>? boneIndices, CharacterDataFragmentPlayer fragment, CancellationToken ct)
    {
        if (boneIndices == null) return;

        // Early exit: check if we have any bone data at all
        bool hasAnyBones = false;
        ushort maxBoneIndex = 0;
        foreach (var kvp in boneIndices)
        {
            if (kvp.Value.Count > 0)
            {
                hasAnyBones = true;
                var localMax = kvp.Value.Max();
                if (localMax > maxBoneIndex) maxBoneIndex = localMax;
            }
            _logger.LogDebug("Found {skellyname} ({idx} bone indices) on player: {bones}", kvp.Key, kvp.Value.Count > 0 ? kvp.Value.Max() : 0, string.Join(',', kvp.Value));
        }

        if (!hasAnyBones) return;

        var validationMode = _syncConfigService.Current.AnimationValidationMode;
        var allowOneBasedShift = _syncConfigService.Current.AnimationAllowOneBasedShift;
        var allowNeighborTolerance = _syncConfigService.Current.AnimationAllowNeighborIndexTolerance;

        // If in Unsafe mode, skip validation entirely
        if (validationMode == AnimationValidationMode.Unsafe)
        {
            _logger.LogDebug("Animation validation mode is Unsafe, skipping bone validation");
            return;
        }

        // Get PAP files once upfront
        var papFiles = fragment.FileReplacements
            .Where(f => !f.IsFileSwap && f.GamePaths.First().EndsWith("pap", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (papFiles.Count == 0) return;

        _logger.LogDebug("Validating {count} PAP files against skeleton (mode: {mode})", papFiles.Count, validationMode);

        // Create validation context once for all files
        var validationContext = _modelAnalyzer.CreateValidationContext(boneIndices);

        int noValidationFailed = 0;
        foreach (var file in papFiles)
        {
            ct.ThrowIfCancellationRequested();

            var skeletonIndices = await _dalamudUtil.RunOnFrameworkThread(() => _modelAnalyzer.GetBoneIndicesFromPap(file.Hash)).ConfigureAwait(false);
            if (skeletonIndices == null) continue;

            // Fast path: check if all indices are vanilla (≤105)
            bool hasNonVanillaBones = false;
            foreach (var kvp in skeletonIndices)
            {
                if (kvp.Value.Count > 0 && kvp.Value.Max() > 105)
                {
                    hasNonVanillaBones = true;
                    break;
                }
            }

            if (!hasNonVanillaBones)
            {
                _logger.LogTrace("All indices of {path} are <= 105, ignoring", file.ResolvedPath);
                continue;
            }

            _logger.LogDebug("Verifying bone indices for {path}, found {x} skeletons", file.ResolvedPath, skeletonIndices.Count);

            // Use precomputed context for validation
            if (!_modelAnalyzer.IsPapCompatible(validationContext, skeletonIndices, validationMode, allowOneBasedShift, allowNeighborTolerance, out var failReason))
            {
                _logger.LogWarning("Animation validation failed for {path}: {reason}", file.ResolvedPath, failReason);
                noValidationFailed++;
                _logger.LogDebug("Removing {file} from sent file replacements and transient data", file.ResolvedPath);
                fragment.FileReplacements.Remove(file);
                foreach (var gamePath in file.GamePaths)
                {
                    _transientResourceManager.RemoveTransientResource(ObjectKind.Player, gamePath);
                }
            }
        }

        if (noValidationFailed > 0)
        {
            _syncMediator.Publish(new NotificationMessage("Invalid Skeleton Setup",
                $"Your client is attempting to send {noValidationFailed} animation files with invalid bone data. Those animation files have been removed from your sent data. " +
                $"Verify that you are using the correct skeleton for those animation files (Check /xllog for more information).",
                NotificationType.Warning, TimeSpan.FromSeconds(10)));
        }
    }

    private async Task<IReadOnlyDictionary<string, string[]>> GetFileReplacementsFromPaths(HashSet<string> forwardResolve, HashSet<string> reverseResolve)
    {
        var forwardPaths = forwardResolve.ToArray();
        var reversePaths = reverseResolve.ToArray();
        Dictionary<string, List<string>> resolvedPaths = new(StringComparer.Ordinal);
        var (forward, reverse) = await _ipcManager.Penumbra.ResolvePathsAsync(forwardPaths, reversePaths).ConfigureAwait(false);
        for (int i = 0; i < forwardPaths.Length; i++)
        {
            var filePath = forward[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.Add(forwardPaths[i].ToLowerInvariant());
            }
            else
            {
                resolvedPaths[filePath] = [forwardPaths[i].ToLowerInvariant()];
            }
        }

        for (int i = 0; i < reversePaths.Length; i++)
        {
            var filePath = reversePaths[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.AddRange(reverse[i].Select(c => c.ToLowerInvariant()));
            }
            else
            {
                resolvedPaths[filePath] = reverse[i].Select(c => c.ToLowerInvariant()).ToList();
            }
        }

        return resolvedPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase).AsReadOnly();
    }

    private HashSet<string> ManageSemiTransientData(ObjectKind objectKind)
    {
        _transientResourceManager.PersistTransientResources(objectKind);

        HashSet<string> pathsToResolve = new(StringComparer.Ordinal);
        foreach (var path in _transientResourceManager.GetSemiTransientResources(objectKind).Where(path => !string.IsNullOrEmpty(path)))
        {
            pathsToResolve.Add(path);
        }

        return pathsToResolve;
    }
}
