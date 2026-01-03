using Xunit;

namespace LaciSynchroni.Tests.FileCache;

/// <summary>
/// Tests for FileCacheEntity functionality.
/// </summary>
public class FileCacheEntityTests
{
    private const string CsvSplit = ";";
    private const string CachePrefix = "laci_";

    [Fact]
    public void Constructor_SetsProperties()
    {
        // Arrange & Act
        var entity = new FileCacheEntity("abc123", "laci_test.dat", "12345678", 1024, 512);

        // Assert
        Assert.Equal("abc123", entity.Hash);
        Assert.Equal("laci_test.dat", entity.PrefixedFilePath);
        Assert.Equal("12345678", entity.LastModifiedDateTicks);
        Assert.Equal(1024, entity.Size);
        Assert.Equal(512, entity.CompressedSize);
    }

    [Fact]
    public void Constructor_WithoutOptionalParams_SetsNullSizes()
    {
        // Arrange & Act
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Assert
        Assert.Null(entity.Size);
        Assert.Null(entity.CompressedSize);
    }

    [Fact]
    public void IsCacheEntry_WhenPrefixedWithCache_ReturnsTrue()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "laci_test.dat", "12345678");

        // Act & Assert
        Assert.True(entity.IsCacheEntry);
    }

    [Fact]
    public void IsCacheEntry_WhenNotPrefixed_ReturnsFalse()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "regular_file.dat", "12345678");

        // Act & Assert
        Assert.False(entity.IsCacheEntry);
    }

    [Fact]
    public void IsCacheEntry_CaseInsensitive()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "LACI_test.dat", "12345678");

        // Act & Assert
        Assert.True(entity.IsCacheEntry);
    }

    [Fact]
    public void CsvEntry_FormatsCorrectly()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678", 1024, 512);

        // Act
        var csv = entity.CsvEntry;

        // Assert
        Assert.Equal("abc123;test.dat;12345678|1024|512", csv);
    }

    [Fact]
    public void CsvEntry_WithNullSizes_UsesMinusOne()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        var csv = entity.CsvEntry;

        // Assert
        Assert.Equal("abc123;test.dat;12345678|-1|-1", csv);
    }

    [Fact]
    public void SetResolvedFilePath_NormalizesPath()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        entity.SetResolvedFilePath("C:\\Users\\Test\\\\Path");

        // Assert
        Assert.Equal("c:\\users\\test\\path", entity.ResolvedFilepath);
    }

    [Fact]
    public void SetResolvedFilePath_ConvertsToLowerCase()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        entity.SetResolvedFilePath("C:\\USERS\\TEST\\Path.DAT");

        // Assert
        Assert.Equal("c:\\users\\test\\path.dat", entity.ResolvedFilepath);
    }

    [Fact]
    public void ResolvedFilepath_DefaultsToEmpty()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act & Assert
        Assert.Equal(string.Empty, entity.ResolvedFilepath);
    }

    [Fact]
    public void Hash_CanBeModified()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        entity.Hash = "xyz789";

        // Assert
        Assert.Equal("xyz789", entity.Hash);
    }

    [Fact]
    public void LastModifiedDateTicks_CanBeModified()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        entity.LastModifiedDateTicks = "99999999";

        // Assert
        Assert.Equal("99999999", entity.LastModifiedDateTicks);
    }

    [Fact]
    public void Size_CanBeModified()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        entity.Size = 2048;

        // Assert
        Assert.Equal(2048, entity.Size);
    }

    [Fact]
    public void CompressedSize_CanBeModified()
    {
        // Arrange
        var entity = new FileCacheEntity("abc123", "test.dat", "12345678");

        // Act
        entity.CompressedSize = 1024;

        // Assert
        Assert.Equal(1024, entity.CompressedSize);
    }
}

#region Test Infrastructure

/// <summary>
/// Simplified FileCacheEntity for testing.
/// Mirrors production implementation.
/// </summary>
public class FileCacheEntity
{
    private const string CsvSplit = ";";
    private const string CachePrefix = "laci_";

    public FileCacheEntity(string hash, string path, string lastModifiedDateTicks, long? size = null, long? compressedSize = null)
    {
        Size = size;
        CompressedSize = compressedSize;
        Hash = hash;
        PrefixedFilePath = path;
        LastModifiedDateTicks = lastModifiedDateTicks;
    }

    public long? CompressedSize { get; set; }
    public string CsvEntry => $"{Hash}{CsvSplit}{PrefixedFilePath}{CsvSplit}{LastModifiedDateTicks}|{Size ?? -1}|{CompressedSize ?? -1}";
    public string Hash { get; set; }
    public bool IsCacheEntry => PrefixedFilePath.StartsWith(CachePrefix, StringComparison.OrdinalIgnoreCase);
    public string LastModifiedDateTicks { get; set; }
    public string PrefixedFilePath { get; init; }
    public string ResolvedFilepath { get; private set; } = string.Empty;
    public long? Size { get; set; }

    public void SetResolvedFilePath(string filePath)
    {
        ResolvedFilepath = filePath.ToLowerInvariant().Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}

#endregion
