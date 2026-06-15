using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using App.Core;
using App.UI.Services;

namespace App.UI.ViewModels;

/// <summary>
/// 主 Shell 布局 ViewModel。
/// 管理侧边栏菜单导航、内容区页面切换、面包屑、主题切换。
/// </summary>
public partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;
    private readonly ThemeService _themeService;

    public ShellViewModel(INavigationService navigationService, ThemeService themeService)
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _currentThemeName = _themeService.CurrentThemeName;
        Title = "物流上位机系统";

        // 注册可导航页面
        RegisterPages();

        // 初始化侧边栏菜单
        InitializeMenu();

        // 导航到默认页（仪表盘）
        _navigationService.NavigateTo("dashboard");
        UpdateFromNavigation();
    }

    /// <summary>当前显示的页面。</summary>
    [ObservableProperty]
    private ViewModelBase? _currentPage;

    /// <summary>当前主题名称。</summary>
    [ObservableProperty]
    private string _currentThemeName;

    /// <summary>侧边栏菜单项。</summary>
    public ObservableCollection<SidebarItem> MenuItems { get; } = [];

    /// <summary>面包屑路径。</summary>
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    /// <summary>是否可以返回上一页。</summary>
    [ObservableProperty]
    private bool _canGoBack;

    /// <summary>当前页面标题（用于顶栏）。</summary>
    [ObservableProperty]
    private string _pageTitle = string.Empty;

    /// <summary>面包屑文本（文本展示，无需 Converter）。</summary>
    [ObservableProperty]
    private string _breadcrumbText = string.Empty;

    // ═══════════════════════════════════════════════════════════
    // 命令
    // ═══════════════════════════════════════════════════════════

    /// <summary>导航到指定页面。</summary>
    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        _navigationService.NavigateTo(pageKey);
        UpdateFromNavigation();
    }

    /// <summary>返回上一页。</summary>
    [RelayCommand]
    private void GoBack()
    {
        if (!CanGoBack) return;
        _navigationService.GoBack();
        UpdateFromNavigation();
    }

    /// <summary>切换主题。</summary>
    [RelayCommand]
    private void SwitchTheme()
    {
        _themeService.SwitchToNextTheme();
        CurrentThemeName = _themeService.CurrentThemeName;
    }

    // ═══════════════════════════════════════════════════════════
    // 内部方法
    // ═══════════════════════════════════════════════════════════

    private void RegisterPages()
    {
        _navigationService.RegisterPage("dashboard", "仪表盘", "📊",
            () => new HomeViewModel(_themeService));
        _navigationService.RegisterPage("monitor", "实时监控", "📡",
            () => new MonitorViewModel());
        _navigationService.RegisterPage("settings", "系统设置", "⚙️",
            () => new SettingsViewModel());
    }

    private void InitializeMenu()
    {
        foreach (var (key, title, icon) in _navigationService.GetRegisteredPages())
        {
            MenuItems.Add(new SidebarItem(title, key, icon));
        }
    }

    private void UpdateFromNavigation()
    {
        CurrentPage = _navigationService.CurrentPage;
        CanGoBack = _navigationService.CanGoBack;

        // 更新面包屑
        Breadcrumbs.Clear();
        foreach (var crumb in _navigationService.Breadcrumbs)
            Breadcrumbs.Add(crumb);

        // 更新当前页面标题
        PageTitle = _navigationService.Breadcrumbs.LastOrDefault()?.Title ?? "";

        // 更新面包屑文本（首页 /> 上级 > 当前页）
        BreadcrumbText = string.Join(" › ", _navigationService.Breadcrumbs.Select(c => c.Title));

        // 更新菜单选中状态
        var currentKey = _navigationService.CurrentPageKey;
        foreach (var item in MenuItems)
            item.IsSelected = item.PageKey == currentKey;
    }
}
