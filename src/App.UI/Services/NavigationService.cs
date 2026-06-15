using System.Collections.ObjectModel;
using App.Core;

namespace App.UI.Services;

/// <summary>
/// 页面路由导航服务实现。
/// 维护页面注册表、导航历史、面包屑路径。
/// </summary>
public class NavigationService : INavigationService
{
    private readonly Dictionary<string, PageRegistration> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _history = new();
    private string _currentKey = string.Empty;

    public ViewModelBase? CurrentPage { get; private set; }

    public string CurrentPageKey => _currentKey;

    public bool CanGoBack => _history.Count > 0;

    private readonly List<BreadcrumbItem> _breadcrumbs = [];
    public IReadOnlyList<BreadcrumbItem> Breadcrumbs => _breadcrumbs.AsReadOnly();

    public void RegisterPage(string key, string title, string icon, Func<ViewModelBase> factory)
    {
        _pages[key] = new PageRegistration(title, icon, factory);
    }

    public void NavigateTo(string key)
    {
        if (!_pages.ContainsKey(key))
            return;

        // 防止重复导航到同一页（避免历史栈污染）
        if (key == _currentKey)
            return;

        if (!string.IsNullOrEmpty(_currentKey))
            _history.Push(_currentKey);

        ApplyPage(key);
    }

    public void GoBack()
    {
        if (_history.Count == 0)
            return;

        var key = _history.Pop();
        ApplyPage(key);
    }

    private void ApplyPage(string key)
    {
        if (!_pages.TryGetValue(key, out var reg))
            return;

        _currentKey = key;
        CurrentPage = reg.Factory();

        // 重建面包屑（反转历史栈 → 按时间顺序排列）
        _breadcrumbs.Clear();
        var tempList = _history.Reverse().ToList();
        foreach (var k in tempList)
        {
            if (_pages.TryGetValue(k, out var p))
                _breadcrumbs.Add(new BreadcrumbItem(p.Title, k, false));
        }
        _breadcrumbs.Add(new BreadcrumbItem(reg.Title, key, true));
    }

    /// <summary>
    /// 获取所有注册页面的菜单条目。
    /// </summary>
    public IEnumerable<(string Key, string Title, string Icon)> GetRegisteredPages()
    {
        foreach (var (key, reg) in _pages)
            yield return (key, reg.Title, reg.Icon);
    }
}
