using App.Core;

namespace App.UI.Services;

/// <summary>
/// 页面路由导航服务接口。
/// 支持按名称注册/导航页面、面包屑追踪、返回导航。 
/// </summary>
public interface INavigationService
{
    /// <summary>当前显示的页面 ViewModel。</summary>
    ViewModelBase? CurrentPage { get; }

    /// <summary>当前页面的注册键。</summary>
    string CurrentPageKey { get; }

    /// <summary>面包屑路径（从根到当前页）。</summary>
    IReadOnlyList<BreadcrumbItem> Breadcrumbs { get; }

    /// <summary>是否可以返回上一页。</summary>
    bool CanGoBack { get; }

    /// <summary>注册一个可导航的页面。</summary>
    void RegisterPage(string key, string title, string icon, Func<ViewModelBase> factory);

    /// <summary>导航到指定页面。</summary>
    void NavigateTo(string key);

    /// <summary>返回上一页。</summary>
    void GoBack();

    /// <summary>
    /// 获取所有注册页面的菜单信息。
    /// </summary>
    IEnumerable<(string Key, string Title, string Icon)> GetRegisteredPages();
}

/// <summary>
/// 面包屑节点。
/// </summary>
public record BreadcrumbItem(string Title, string PageKey, bool IsCurrent);

/// <summary>
/// 页面注册信息。
/// </summary>
public record PageRegistration(string Title, string Icon, Func<ViewModelBase> Factory);


