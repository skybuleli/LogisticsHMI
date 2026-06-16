using Avalonia;
using Avalonia.Controls;

namespace App.UI.Controls;

/// <summary>
/// 通知徽标控件。
/// 显示一个带数字角标的图标，用于指示未读通知/报警数量。
/// </summary>
public partial class NotificationBadge : UserControl
{
    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<NotificationBadge, int>(nameof(Count), 0);

    private Border? _badge;
    private TextBlock? _badgeText;

    public NotificationBadge()
    {
        InitializeComponent();
        _badge = this.FindControl<Border>("PART_Badge");
        _badgeText = this.FindControl<TextBlock>("PART_BadgeText");
        UpdateBadge();
    }

    /// <summary>
    /// 通知数量。0 时隐藏徽标。
    /// </summary>
    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CountProperty)
            UpdateBadge();
    }

    private void UpdateBadge()
    {
        if (_badge == null || _badgeText == null)
            return;

        var count = Count;
        _badge.IsVisible = count > 0;
        _badgeText.Text = count > 99 ? "99+" : count.ToString();
    }
}
