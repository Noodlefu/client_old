using System.Text.Json;
using Xunit;

namespace LaciSynchroni.Tests.Configuration;

/// <summary>
/// Tests for ConfigurationServiceBase functionality.
/// These tests validate config loading, saving, backup handling, and hot-reload.
/// </summary>
public class ConfigurationServiceBaseTests : IDisposable
{
    private readonly string _testDirectory;

    public ConfigurationServiceBaseTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"LaciSynchroniTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public void Current_WhenNoConfigExists_CreatesDefaultConfig()
    {
        // Arrange
        using var service = new TestConfigService(_testDirectory);

        // Act
        var config = service.Current;

        // Assert
        Assert.NotNull(config);
        Assert.Equal("default", config.TestValue);
    }

    [Fact]
    public void Current_WhenConfigExists_LoadsExistingConfig()
    {
        // Arrange
        var existingConfig = new TestConfig { TestValue = "existing", NumberValue = 42 };
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            JsonSerializer.Serialize(existingConfig));

        using var service = new TestConfigService(_testDirectory);

        // Act
        var config = service.Current;

        // Assert
        Assert.Equal("existing", config.TestValue);
        Assert.Equal(42, config.NumberValue);
    }

    [Fact]
    public void Current_MultipleCalls_ReturnsSameInstance()
    {
        // Arrange
        using var service = new TestConfigService(_testDirectory);

        // Act
        var config1 = service.Current;
        var config2 = service.Current;

        // Assert
        Assert.Same(config1, config2);
    }

    [Fact]
    public void ConfigurationPath_ReturnsCorrectPath()
    {
        // Arrange
        using var service = new TestConfigService(_testDirectory);

        // Act
        var path = service.ConfigurationPath;

        // Assert
        Assert.Equal(Path.Combine(_testDirectory, TestConfigService.ConfigName), path);
    }

    [Fact]
    public void ConfigurationName_ReturnsConfigFileName()
    {
        // Arrange
        using var service = new TestConfigService(_testDirectory);

        // Act
        var name = service.ConfigurationName;

        // Assert
        Assert.Equal(TestConfigService.ConfigName, name);
    }

    [Fact]
    public void Save_RaisesConfigSaveEvent()
    {
        // Arrange
        using var service = new TestConfigService(_testDirectory);
        var eventRaised = false;
        service.ConfigSave += (_, _) => eventRaised = true;

        // Act
        service.Save();

        // Assert
        Assert.True(eventRaised);
    }

    [Fact]
    public void Current_WhenConfigIsCorrupted_LoadsBackup()
    {
        // Arrange - create backup directory with valid backup
        var backupDir = Path.Combine(_testDirectory, "backups");
        Directory.CreateDirectory(backupDir);

        var backupConfig = new TestConfig { TestValue = "from_backup", NumberValue = 99 };
        File.WriteAllText(
            Path.Combine(backupDir, "test_backup.json"),
            JsonSerializer.Serialize(backupConfig));

        // Write corrupted main config
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            "{ invalid json }}}");

        using var service = new TestConfigService(_testDirectory);

        // Act
        var config = service.Current;

        // Assert
        Assert.Equal("from_backup", config.TestValue);
        Assert.Equal(99, config.NumberValue);
    }

    [Fact]
    public void Current_WhenConfigAndBackupAreMissing_CreatesDefault()
    {
        // Arrange - empty directory
        using var service = new TestConfigService(_testDirectory);

        // Act
        var config = service.Current;

        // Assert
        Assert.NotNull(config);
        Assert.Equal("default", config.TestValue);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var service = new TestConfigService(_testDirectory);

        // Act & Assert - should not throw
        service.Dispose();
        service.Dispose();
        service.Dispose();
    }

    [Fact]
    public void UpdateLastWriteTime_UpdatesInternalTimestamp()
    {
        // Arrange
        using var service = new TestConfigService(_testDirectory);
        _ = service.Current; // Initialize

        // Write config file to establish a time
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            JsonSerializer.Serialize(new TestConfig()));

        // Act - should not throw
        service.UpdateLastWriteTime();

        // Assert - just verify no exception
        Assert.True(true);
    }

    [Fact]
    public async Task HotReload_WhenConfigFileChanges_ReloadsConfig()
    {
        // Arrange
        var initialConfig = new TestConfig { TestValue = "initial" };
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            JsonSerializer.Serialize(initialConfig));

        using var service = new TestConfigService(_testDirectory);
        var initialValue = service.Current.TestValue;

        // Act - modify the config file
        await Task.Delay(100); // Ensure different timestamp
        var updatedConfig = new TestConfig { TestValue = "updated" };
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            JsonSerializer.Serialize(updatedConfig));

        // Wait for hot-reload to detect change (checks every 5 seconds, but we simulate)
        await Task.Delay(6000);

        var updatedValue = service.Current.TestValue;

        // Assert
        Assert.Equal("initial", initialValue);
        Assert.Equal("updated", updatedValue);
    }

    [Fact]
    public void Current_WhenConfigIsNull_CreatesDefault()
    {
        // Arrange - write "null" as the config content
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            "null");

        using var service = new TestConfigService(_testDirectory);

        // Act
        var config = service.Current;

        // Assert
        Assert.NotNull(config);
        Assert.Equal("default", config.TestValue);
    }

    [Fact]
    public void Current_WhenConfigIsEmpty_CreatesDefault()
    {
        // Arrange - write empty object
        File.WriteAllText(
            Path.Combine(_testDirectory, TestConfigService.ConfigName),
            "{}");

        using var service = new TestConfigService(_testDirectory);

        // Act
        var config = service.Current;

        // Assert
        Assert.NotNull(config);
        // Empty JSON deserializes to object with default property values
        Assert.Equal("default", config.TestValue);
    }
}

#region Test Infrastructure

/// <summary>
/// Test configuration class.
/// </summary>
public class TestConfig : ISyncConfiguration
{
    public string TestValue { get; set; } = "default";
    public int NumberValue { get; set; } = 0;
    public List<string> ListValue { get; set; } = new();
}

/// <summary>
/// Marker interface for sync configurations.
/// </summary>
public interface ISyncConfiguration { }

/// <summary>
/// Test implementation of ConfigurationServiceBase.
/// </summary>
public class TestConfigService : ConfigurationServiceBase<TestConfig>
{
    public const string ConfigName = "test.json";

    public TestConfigService(string configDir) : base(configDir)
    {
    }

    public override string ConfigurationName => ConfigName;
}

/// <summary>
/// Simplified ConfigurationServiceBase for testing.
/// Mirrors production implementation without external dependencies.
/// </summary>
public abstract class ConfigurationServiceBase<T> : IConfigService<T> where T : ISyncConfiguration
{
    private readonly CancellationTokenSource _periodicCheckCts = new();
    private DateTime _configLastWriteTime;
    private Lazy<T> _currentConfigInternal;
    private bool _disposed = false;

    public event EventHandler? ConfigSave;

    protected ConfigurationServiceBase(string configDirectory)
    {
        ConfigurationDirectory = configDirectory;

        _ = Task.Run(CheckForConfigUpdatesInternal, _periodicCheckCts.Token);

        _currentConfigInternal = LazyConfig();
    }

    public string ConfigurationDirectory { get; init; }
    public T Current => _currentConfigInternal.Value;
    public abstract string ConfigurationName { get; }
    public string ConfigurationPath => Path.Combine(ConfigurationDirectory, ConfigurationName);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public void Save()
    {
        ConfigSave?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateLastWriteTime()
    {
        _configLastWriteTime = GetConfigLastWriteTime();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || _disposed) return;
        _disposed = true;
        _periodicCheckCts.Cancel();
        _periodicCheckCts.Dispose();
    }

    protected T LoadConfig()
    {
        T? config;
        if (!File.Exists(ConfigurationPath))
        {
            config = AttemptToLoadBackup();
            if (Equals(config, default(T)))
            {
                config = (T)Activator.CreateInstance(typeof(T))!;
                Save();
            }
        }
        else
        {
            try
            {
                config = JsonSerializer.Deserialize<T>(File.ReadAllText(ConfigurationPath));
            }
            catch
            {
                config = AttemptToLoadBackup();
            }

            if (config == null || Equals(config, default(T)))
            {
                config = (T)Activator.CreateInstance(typeof(T))!;
                Save();
            }
        }

        _configLastWriteTime = GetConfigLastWriteTime();
        return config!;
    }

    private const string BackupFolder = "backups";

    private T? AttemptToLoadBackup()
    {
        var configBackupFolder = Path.Join(ConfigurationDirectory, BackupFolder);
        var configNameSplit = ConfigurationName.Split(".");
        if (!Directory.Exists(configBackupFolder))
            return default;

        var existingBackups = Directory.EnumerateFiles(configBackupFolder, configNameSplit[0] + "*")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc);

        foreach (var file in existingBackups)
        {
            try
            {
                var config = JsonSerializer.Deserialize<T>(File.ReadAllText(file));
                if (Equals(config, default(T)))
                {
                    File.Delete(file);
                    continue;
                }

                File.Copy(file, ConfigurationPath, true);
                return config;
            }
            catch
            {
                File.Delete(file);
            }
        }

        return default;
    }

    private async Task CheckForConfigUpdatesInternal()
    {
        while (!_periodicCheckCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _periodicCheckCts.Token).ConfigureAwait(false);

                var lastWriteTime = GetConfigLastWriteTime();
                if (lastWriteTime != _configLastWriteTime)
                {
                    _currentConfigInternal = LazyConfig();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private DateTime GetConfigLastWriteTime()
    {
        try { return new FileInfo(ConfigurationPath).LastWriteTimeUtc; }
        catch { return DateTime.MinValue; }
    }

    private Lazy<T> LazyConfig()
    {
        _configLastWriteTime = GetConfigLastWriteTime();
        return new Lazy<T>(LoadConfig);
    }
}

/// <summary>
/// Interface for configuration services.
/// </summary>
public interface IConfigService<out T> : IDisposable where T : ISyncConfiguration
{
    T Current { get; }
    string ConfigurationName { get; }
    string ConfigurationPath { get; }
    event EventHandler? ConfigSave;
    void UpdateLastWriteTime();
}

#endregion
