using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using LaciSynchroni.FileCache;
using LaciSynchroni.Interop.GameModel;
using LaciSynchroni.PlayerData.Factories;
using LaciSynchroni.PlayerData.Handlers;
using LaciSynchroni.SyncConfiguration;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LaciSynchroni.Services;

public sealed class XivDataAnalyzer
{
    private readonly ILogger<XivDataAnalyzer> _logger;
    private readonly FileCacheManager _fileCacheManager;
    private readonly XivDataStorageService _configService;
    private readonly List<string> _failedCalculatedTris = [];

    public XivDataAnalyzer(ILogger<XivDataAnalyzer> logger, FileCacheManager fileCacheManager,
        XivDataStorageService configService)
    {
        _logger = logger;
        _fileCacheManager = fileCacheManager;
        _configService = configService;
    }

    public unsafe Dictionary<string, List<ushort>>? GetSkeletonBoneIndices(GameObjectHandler handler)
    {
        if (handler.Address == nint.Zero) return null;
        var chara = (CharacterBase*)(((Character*)handler.Address)->GameObject.DrawObject);
        if (chara->GetModelType() != CharacterBase.ModelType.Human) return null;
        var resHandles = chara->Skeleton->SkeletonResourceHandles;
        Dictionary<string, List<ushort>> outputIndices = [];
        try
        {
            for (int i = 0; i < chara->Skeleton->PartialSkeletonCount; i++)
            {
                var handle = *(resHandles + i);
                _logger.LogTrace("Iterating over SkeletonResourceHandle #{i}:{x}", i, ((nint)handle).ToString("X"));
                if ((nint)handle == nint.Zero) continue;
                var curBones = handle->BoneCount;
                // this is unrealistic, the filename shouldn't ever be that long
                if (handle->FileName.Length > 1024) continue;
                var skeletonName = handle->FileName.ToString();
                if (string.IsNullOrEmpty(skeletonName)) continue;
                outputIndices[skeletonName] = new();
                for (ushort boneIdx = 0; boneIdx < curBones; boneIdx++)
                {
                    var boneName = handle->HavokSkeleton->Bones[boneIdx].Name.String;
                    if (boneName == null) continue;
                    outputIndices[skeletonName].Add((ushort)(boneIdx + 1));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not process skeleton data");
        }

        return (outputIndices.Count != 0 && outputIndices.Values.All(u => u.Count > 0)) ? outputIndices : null;
    }

    public unsafe Dictionary<string, List<ushort>>? GetBoneIndicesFromPap(string hash)
    {
        if (_configService.Current.BonesDictionary.TryGetValue(hash, out var bones)) return bones;

        var cacheEntity = _fileCacheManager.GetFileCacheByHash(hash);
        if (cacheEntity == null) return null;

        using BinaryReader reader = new BinaryReader(File.Open(cacheEntity.ResolvedFilepath, FileMode.Open, FileAccess.Read, FileShare.Read));

        // most of this shit is from vfxeditor, surely nothing will change in the pap format :copium:
        reader.ReadInt32(); // ignore
        reader.ReadInt32(); // ignore
        reader.ReadInt16(); // read 2 (num animations)
        reader.ReadInt16(); // read 2 (modelid)
        var type = reader.ReadByte();// read 1 (type)
        if (type != 0) return null; // it's not human, just ignore it, whatever

        reader.ReadByte(); // read 1 (variant)
        reader.ReadInt32(); // ignore
        var havokPosition = reader.ReadInt32();
        var footerPosition = reader.ReadInt32();
        var havokDataSize = footerPosition - havokPosition;
        reader.BaseStream.Position = havokPosition;
        var havokData = reader.ReadBytes(havokDataSize);
        if (havokData.Length <= 8) return null; // no havok data

        var output = new Dictionary<string, List<ushort>>(StringComparer.OrdinalIgnoreCase);
        var tempHavokDataPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()) + ".hkx";
        var tempHavokDataPathAnsi = Marshal.StringToHGlobalAnsi(tempHavokDataPath);

        try
        {
            File.WriteAllBytes(tempHavokDataPath, havokData);

            var loadoptions = stackalloc hkSerializeUtil.LoadOptions[1];
            loadoptions->TypeInfoRegistry = hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry();
            loadoptions->ClassNameRegistry = hkBuiltinTypeRegistry.Instance()->GetClassNameRegistry();
            loadoptions->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int>
            {
                Storage = (int)(hkSerializeUtil.LoadOptionBits.Default)
            };

            var resource = hkSerializeUtil.LoadFromFile((byte*)tempHavokDataPathAnsi, null, loadoptions);
            if (resource == null)
            {
                throw new InvalidOperationException("Resource was null after loading");
            }

            var rootLevelName = @"hkRootLevelContainer"u8;
            fixed (byte* n1 = rootLevelName)
            {
                var container = (hkRootLevelContainer*)resource->GetContentsPointer(n1, hkBuiltinTypeRegistry.Instance()->GetTypeInfoRegistry());
                var animationName = @"hkaAnimationContainer"u8;
                fixed (byte* n2 = animationName)
                {
                    var animContainer = (hkaAnimationContainer*)container->findObjectByName(n2, null);
                    for (int i = 0; i < animContainer->Bindings.Length; i++)
                    {
                        var binding = animContainer->Bindings[i].ptr;
                        var boneTransform = binding->TransformTrackToBoneIndices;
                        string name = binding->OriginalSkeletonName.String! + "_" + i;
                        output[name] = [];
                        for (int boneIdx = 0; boneIdx < boneTransform.Length; boneIdx++)
                        {
                            output[name].Add((ushort)boneTransform[boneIdx]);
                        }
                        output[name].Sort();
                    }

                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load havok file in {path}", tempHavokDataPath);
        }
        finally
        {
            Marshal.FreeHGlobal(tempHavokDataPathAnsi);
            File.Delete(tempHavokDataPath);
        }

        _configService.Current.BonesDictionary[hash] = output;
        _configService.Save();
        return output;
    }

    public async Task<long> GetTrianglesByHash(string hash)
    {
        if (_configService.Current.TriangleDictionary.TryGetValue(hash, out var cachedTris) && cachedTris > 0)
            return cachedTris;

        if (_failedCalculatedTris.Contains(hash, StringComparer.Ordinal))
            return 0;

        var path = _fileCacheManager.GetFileCacheByHash(hash);
        if (path == null || !path.ResolvedFilepath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return 0;

        var filePath = path.ResolvedFilepath;

        try
        {
            _logger.LogDebug("Detected Model File {path}, calculating Tris", filePath);
            var file = new MdlFile(filePath);
            if (file.LodCount <= 0)
            {
                _failedCalculatedTris.Add(hash);
                _configService.Current.TriangleDictionary[hash] = 0;
                _configService.Save();
                return 0;
            }

            long tris = 0;
            for (int i = 0; i < file.LodCount; i++)
            {
                try
                {
                    var meshIdx = file.Lods[i].MeshIndex;
                    var meshCnt = file.Lods[i].MeshCount;
                    tris = file.Meshes.Skip(meshIdx).Take(meshCnt).Sum(p => p.IndexCount) / 3;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not load lod mesh {mesh} from path {path}", i, filePath);
                    continue;
                }

                if (tris > 0)
                {
                    _logger.LogDebug("TriAnalysis: {filePath} => {tris} triangles", filePath, tris);
                    _configService.Current.TriangleDictionary[hash] = tris;
                    _configService.Save();
                    break;
                }
            }

            return tris;
        }
        catch (Exception e)
        {
            _failedCalculatedTris.Add(hash);
            _configService.Current.TriangleDictionary[hash] = 0;
            _configService.Save();
            _logger.LogWarning(e, "Could not parse file {file}", filePath);
            return 0;
        }
    }

    // Regex patterns for canonicalizing skeleton keys (e.g., extracting "c0101" from paths)
    private static readonly Regex SkeletonKeyPattern1 = new(@"chara/human/(c\d{4})/", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SkeletonKeyPattern2 = new(@"(c\d{4})_", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SkeletonKeyPattern3 = new(@"_(c\d{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Canonicalizes a skeleton key from a full path or skeleton name to a bucket code like "c0101".
    /// </summary>
    public static string CanonicalizeSkeletonKey(string skeletonKey)
    {
        // Try pattern 1: chara/human/c0101/...
        var match = SkeletonKeyPattern1.Match(skeletonKey);
        if (match.Success) return match.Groups[1].Value.ToLowerInvariant();

        // Try pattern 2: c0101_something
        match = SkeletonKeyPattern2.Match(skeletonKey);
        if (match.Success) return match.Groups[1].Value.ToLowerInvariant();

        // Try pattern 3: something_c0101
        match = SkeletonKeyPattern3.Match(skeletonKey);
        if (match.Success) return match.Groups[1].Value.ToLowerInvariant();

        // Return original if no pattern matches
        return skeletonKey.ToLowerInvariant();
    }

    /// <summary>
    /// Checks if a PAP file's bone indices are compatible with the local skeleton.
    /// </summary>
    /// <param name="localBoneBuckets">Dictionary of canonical skeleton keys to their bone indices</param>
    /// <param name="papBoneIndices">Dictionary of skeleton names from the PAP to their bone indices</param>
    /// <param name="mode">Validation strictness mode</param>
    /// <param name="allowOneBasedShift">If true, allows for one-based vs zero-based index differences</param>
    /// <param name="allowNeighborTolerance">If true, allows neighboring index tolerance (±1)</param>
    /// <param name="reason">Output parameter explaining why validation failed, if it did</param>
    /// <returns>True if the PAP is compatible, false otherwise</returns>
    public bool IsPapCompatible(
        Dictionary<string, List<ushort>> localBoneBuckets,
        Dictionary<string, List<ushort>> papBoneIndices,
        AnimationValidationMode mode,
        bool allowOneBasedShift,
        bool allowNeighborTolerance,
        out string reason)
    {
        reason = string.Empty;

        if (mode == AnimationValidationMode.Unsafe)
        {
            return true; // No validation in unsafe mode
        }

        // Canonicalize local bone buckets
        var canonicalLocalBuckets = new Dictionary<string, HashSet<ushort>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in localBoneBuckets)
        {
            var canonicalKey = CanonicalizeSkeletonKey(kvp.Key);
            if (!canonicalLocalBuckets.TryGetValue(canonicalKey, out var set))
            {
                set = new HashSet<ushort>();
                canonicalLocalBuckets[canonicalKey] = set;
            }
            foreach (var idx in kvp.Value)
            {
                set.Add(idx);
            }
        }

        // Create a combined "any" bucket with all local bone indices for fallback matching
        var anyBucket = new HashSet<ushort>();
        foreach (var set in canonicalLocalBuckets.Values)
        {
            foreach (var idx in set)
            {
                anyBucket.Add(idx);
            }
        }

        // Check each PAP skeleton's bone indices
        foreach (var papKvp in papBoneIndices)
        {
            var papCanonicalKey = CanonicalizeSkeletonKey(papKvp.Key);
            var papBones = papKvp.Value;

            if (papBones.Count == 0) continue;

            // Find matching local bucket or use "any" bucket as fallback
            HashSet<ushort>? targetBucket = null;
            if (canonicalLocalBuckets.TryGetValue(papCanonicalKey, out var matchedBucket))
            {
                targetBucket = matchedBucket;
            }
            else
            {
                // Use combined bucket as fallback
                targetBucket = anyBucket;
                _logger.LogTrace("No exact skeleton match for {papKey}, using combined bucket", papCanonicalKey);
            }

            if (targetBucket.Count == 0)
            {
                reason = $"No local bone data available for skeleton {papCanonicalKey}";
                return false;
            }

            var maxLocalBone = targetBucket.Max();

            foreach (var papBoneIdx in papBones)
            {
                bool isValid = false;

                // Direct match
                if (targetBucket.Contains(papBoneIdx))
                {
                    isValid = true;
                }
                // One-based shift tolerance (PAP might use 0-based while skeleton uses 1-based or vice versa)
                else if (allowOneBasedShift && mode != AnimationValidationMode.Safest)
                {
                    if (targetBucket.Contains((ushort)(papBoneIdx + 1)) ||
                        (papBoneIdx > 0 && targetBucket.Contains((ushort)(papBoneIdx - 1))))
                    {
                        isValid = true;
                    }
                }
                // Neighbor tolerance (allows ±1 for edge cases)
                else if (allowNeighborTolerance && mode == AnimationValidationMode.Safe)
                {
                    if (targetBucket.Contains((ushort)(papBoneIdx + 1)) ||
                        (papBoneIdx > 0 && targetBucket.Contains((ushort)(papBoneIdx - 1))))
                    {
                        isValid = true;
                    }
                }

                // In Safe mode, also allow if bone index is within the max range
                if (!isValid && mode == AnimationValidationMode.Safe && papBoneIdx <= maxLocalBone)
                {
                    isValid = true;
                }

                if (!isValid)
                {
                    reason = $"PAP bone index {papBoneIdx} (skeleton: {papCanonicalKey}) not found in local skeleton (max: {maxLocalBone})";
                    _logger.LogDebug("Animation validation failed: {reason}", reason);
                    return false;
                }
            }
        }

        return true;
    }
}
