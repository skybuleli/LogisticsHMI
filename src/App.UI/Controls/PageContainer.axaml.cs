using Avalonia;
using Avalonia.Controls;

namespace App.UI.Controls;

/// <summary>
/// 页面容器控件。
/// 统一包裹页面内容，支持加载遮罩。
/// 标题由 ShellView 顶栏统一管理。
/// </summary>
public partial class PageContainer : UserControl
{
    public static readonly StyledProperty<object?> PageContentProperty =
        AvaloniaProperty.Register<PageContainer, object?>(nameof(PageContent));

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<PageContainer, bool>(nameof(IsLoading), false);

    public static readonly StyledProperty<string> LoadingTextProperty =
        AvaloniaProperty.Register<PageContainer, string>(nameof(LoadingText), "加载中…");

    /// <summary>页面主体内容。</summary>
    public object? PageContent
    {
        get => GetValue(PageContentProperty);
        set => SetValue(PageContentProperty, value);
    }

    /// <summary>是否显示加载遮罩。</summary>
    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>加载遮罩文本。</summary>
    public string LoadingText
    {
        get => GetValue(LoadingTextProperty);
        set => SetValue(LoadingTextProperty, value);
    }
}
