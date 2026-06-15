using CommunityToolkit.Mvvm.ComponentModel;

namespace App.UI.Services;

/// <summary>
/// 侧边栏菜单项模型。
/// </summary>
public partial class SidebarItem : ObservableObject
{
    public SidebarItem(string title, string pageKey, string icon)
    {
        Title = title;
        PageKey = pageKey;
        Icon = icon;
    }

    public string Title { get; }
    public string PageKey { get; }
    public string Icon { get; }

    [ObservableProperty]
    private bool _isSelected;
}
