using LaciSynchroni.Common.Data;
using System.Text.RegularExpressions;

namespace LaciSynchroni.PlayerData.Data;

public partial class FileReplacement(string[] gamePaths, string filePath)
{

    public HashSet<string> GamePaths { get; init; } = gamePaths.Select(g => g.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public bool HasFileReplacement => GamePaths.Count >= 1 && GamePaths.Any(p => !string.Equals(p, ResolvedPath, StringComparison.Ordinal));

    public string Hash { get; set; } = string.Empty;
    public bool IsFileSwap => !LocalPathRegex().IsMatch(ResolvedPath) && GamePaths.All(p => !LocalPathRegex().IsMatch(p));
    public string ResolvedPath { get; init; } = filePath.Replace('\\', '/');

    public FileReplacementData ToFileReplacementDto()
    {
        return new FileReplacementData
        {
            GamePaths = [.. GamePaths],
            Hash = Hash,
            FileSwapPath = IsFileSwap ? ResolvedPath : string.Empty,
        };
    }

    public override string ToString()
    {
        return $"HasReplacement:{HasFileReplacement},IsFileSwap:{IsFileSwap} - {string.Join(",", GamePaths)} => {ResolvedPath}";
    }

    [GeneratedRegex(@"^[a-zA-Z]:(/|\\)", RegexOptions.ECMAScript)]
    private static partial Regex LocalPathRegex();
}
