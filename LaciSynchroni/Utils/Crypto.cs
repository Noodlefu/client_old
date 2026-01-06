using System.Security.Cryptography;
using System.Text;

namespace LaciSynchroni.Utils;

public static class Crypto
{
    private static readonly Dictionary<(string, ushort), string> _hashListPlayersSHA256 = new();
    private static readonly Dictionary<string, string> _hashListSHA256 = new(StringComparer.Ordinal);

    public static string GetFileHash(this string filePath)
    {
        // Use FileShare.Read to allow other readers but fail if a writer has the file open
        // This prevents computing a hash on a partially-written file
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hashBytes = SHA1.HashData(fs);
        return BitConverter.ToString(hashBytes).Replace("-", "", StringComparison.Ordinal);
    }

    public static string GetHash256(this (string, ushort) playerToHash)
    {
        if (_hashListPlayersSHA256.TryGetValue(playerToHash, out var hash))
            return hash;

        var inputBytes = Encoding.UTF8.GetBytes(playerToHash.Item1 + playerToHash.Item2.ToString());
        return _hashListPlayersSHA256[playerToHash] =
            BitConverter.ToString(SHA256.HashData(inputBytes)).Replace("-", "", StringComparison.Ordinal);
    }

    public static string GetHash256(this string stringToHash)
    {
        return GetOrComputeHashSHA256(stringToHash);
    }

    private static string GetOrComputeHashSHA256(string stringToCompute)
    {
        if (_hashListSHA256.TryGetValue(stringToCompute, out var hash))
            return hash;

        var inputBytes = Encoding.UTF8.GetBytes(stringToCompute);
        return _hashListSHA256[stringToCompute] =
            BitConverter.ToString(SHA256.HashData(inputBytes)).Replace("-", "", StringComparison.Ordinal);
    }
}
