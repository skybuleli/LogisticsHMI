using System;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace App.UI.Services;

/// <summary>
/// 运行时主题切换服务。
/// 管理三套内置主题（工业蓝/暗黑/高对比度）的切换。
/// </summary>
public class ThemeService
{
    private int _currentThemeIndex;
    private readonly ThemeInfo[] _themes;

    // 资源 URI 常量
    private static readonly Uri BaseColorsUri = new("avares://App.UI/Styles/Colors.axaml");
    private static readonly Uri DarkThemeUri = new("avares://App.UI/Styles/ThemeDark.axaml");
    private static readonly Uri HighContrastUri = new("avares://App.UI/Styles/ThemeHighContrast.axaml");

    public ThemeService()
    {
        _themes =
        [
            new ThemeInfo("工业蓝",   BaseColorsUri),
            new ThemeInfo("暗黑",     DarkThemeUri),
            new ThemeInfo("高对比度", HighContrastUri),
        ];
    }

    /// <summary>
    /// 当前主题名称。
    /// </summary>
    public string CurrentThemeName => _themes[_currentThemeIndex].Name;

    /// <summary>
    /// 切换主题（循环：工业蓝 → 暗黑 → 高对比度 → 工业蓝…）。
    /// </summary>
    public void SwitchToNextTheme()
    {
        _currentThemeIndex = (_currentThemeIndex + 1) % _themes.Length;
        ApplyTheme(_currentThemeIndex);
    }

    /// <summary>
    /// 切换到指定索引的主题。
    /// </summary>
    public void SwitchToTheme(int index)
    {
        if (index < 0 || index >= _themes.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        _currentThemeIndex = index;
        ApplyTheme(index);
    }

    /// <summary>
    /// 切换到指定名称的主题。
    /// </summary>
    public void SwitchToTheme(string name)
    {
        for (var i = 0; i < _themes.Length; i++)
        {
            if (_themes[i].Name == name)
            {
                SwitchToTheme(i);
                return;
            }
        }
    }

    /// <summary>
    /// 获取所有主题名称。
    /// </summary>
    public string[] GetThemeNames()
    {
        var names = new string[_themes.Length];
        for (var i = 0; i < _themes.Length; i++)
            names[i] = _themes[i].Name;
        return names;
    }

    /// <summary>
    /// 获取当前主题索引。
    /// </summary>
    public int CurrentThemeIndex => _currentThemeIndex;

    private static void ApplyTheme(int index)
    {
        if (Application.Current is not App app)
            return;

        // 始终以基准颜色字典为底层
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceInclude(BaseColorsUri) { Source = BaseColorsUri });

        // 主题覆盖字典作为上层（仅当非默认主题时添加）
        if (index > 0)
        {
            var overrideUri = index switch
            {
                1 => DarkThemeUri,
                2 => HighContrastUri,
                _ => null,
            };
            if (overrideUri != null)
            {
                app.Resources.MergedDictionaries.Add(new ResourceInclude(overrideUri) { Source = overrideUri });
            }
        }
    }

    private readonly record struct ThemeInfo(string Name, Uri ResourceUri);
}
