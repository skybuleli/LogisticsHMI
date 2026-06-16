using System;
using System.IO;
using System.Text.Json;
using App.Core;
using Microsoft.Extensions.Configuration;

namespace App.Infrastructure;

/// <summary>
/// 配置管理服务实现。
/// 基于 Microsoft.Extensions.Configuration 的 JSON 文件配置系统。
/// 支持加载、保存、热重载（文件变更监视）。
/// </summary>
public class ConfigurationService : IConfigurationService, IDisposable
{
    private const string ConfigFileName = "appsettings.json";
    private IConfigurationRoot _configuration;
    private readonly string _configPath;
    private FileSystemWatcher? _fileWatcher;
    private bool _suppressReload;
    private bool _disposed;

    public ConfigurationService()
    {
        // 配置文件路径（输出目录，由 csproj 的 CopyToOutputDirectory 确保）
        _configPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        _configuration = BuildConfiguration();
        LoadModels();

        // 启动文件变更监视（热重载）
        StartFileWatcher();
    }

    public AppConfig Current { get; private set; } = new();
    public LoggingConfig Logging => Current.Logging;
    public WindowConfig Window => Current.Window;
    public ThemeConfig Theme => Current.Theme;
    public CommunicationConfig Communication => Current.Communication;
    public DeviceScanConfig DeviceScan => Current.DeviceScan;

    public event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    public void Reload()
    {
        _configuration.Reload();
        LoadModels();
        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs
        {
            ChangedSections = ["*"],
        });
    }

    public void Save()
    {
        // 保存期间抑制文件监视器的重载
        _suppressReload = true;

        try
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(_configPath, json);
        }
        finally
        {
            // 手动重新加载
            Reload();
            _suppressReload = false;
        }
    }

    private IConfigurationRoot BuildConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(_configPath)!)
            .AddJsonFile(ConfigFileName, optional: true, reloadOnChange: true);

        return builder.Build();
    }

    private void LoadModels()
    {
        var config = new AppConfig();
        _configuration.Bind(config);
        Current = config;
    }

    private void StartFileWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir == null || !Directory.Exists(dir)) return;

            _fileWatcher = new FileSystemWatcher(dir, ConfigFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };

            // 防抖动：文件变更后延迟 500ms 再加载
            _fileWatcher.Changed += OnConfigFileChanged;
        }
        catch
        {
            // 文件监视在某些环境不可用（如单文件部署），不阻塞启动
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // 如果正在执行 Save() 中的手动写入，跳过文件监视器触发的重载
        if (_suppressReload) return;

        // 防抖：文件变更频繁触发，只处理最后一次
        System.Threading.Thread.Sleep(500);
        try
        {
            Reload();
        }
        catch
        {
            // 文件可能正在写入，忽略中间状态
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_fileWatcher != null)
        {
            _fileWatcher.Changed -= OnConfigFileChanged;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }
        (_configuration as IDisposable)?.Dispose();
    }
}
