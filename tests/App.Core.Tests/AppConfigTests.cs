using Xunit;
using App.Core;

namespace App.Core.Tests;

public class AppConfigTests
{
    [Fact]
    public void AppConfig_Defaults_AreSensible()
    {
        var cfg = new AppConfig();

        Assert.NotNull(cfg.Logging);
        Assert.Equal(30, cfg.Logging.MainLogRetentionDays);
        Assert.Equal(14, cfg.Logging.CommLogRetentionDays);

        Assert.NotNull(cfg.Window);
        Assert.Equal(1280, cfg.Window.Width);
        Assert.Equal(800, cfg.Window.Height);
        Assert.True(cfg.Window.StartMaximized);

        Assert.NotNull(cfg.Communication);
        Assert.Equal(5000, cfg.Communication.DefaultTimeoutMs);
        Assert.Equal(30000, cfg.Communication.HeartbeatIntervalMs);
        Assert.True(cfg.Communication.AutoReconnect);
        Assert.Equal(5, cfg.Communication.ReconnectMaxRetries);
    }

    [Fact]
    public void CommunicationConfig_Timeout_IsPositive()
    {
        var cfg = new CommunicationConfig();
        Assert.True(cfg.DefaultTimeoutMs > 0);
        Assert.True(cfg.HeartbeatIntervalMs > 0);
        Assert.True(cfg.ReconnectMaxRetries >= 0);
    }

    [Fact]
    public void ThemeConfig_DefaultTheme_IsNonEmpty()
    {
        var cfg = new ThemeConfig();
        Assert.False(string.IsNullOrWhiteSpace(cfg.DefaultTheme));
    }

    [Fact]
    public void WindowConfig_MinSize_IsSmallerThanDefault()
    {
        var cfg = new WindowConfig();
        Assert.True(cfg.MinWidth <= cfg.Width);
        Assert.True(cfg.MinHeight <= cfg.Height);
    }
}
