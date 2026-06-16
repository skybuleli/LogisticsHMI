using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using App.Core;

namespace App.UI.Services;

/// <summary>
/// 对话框服务实现。
/// 使用 Avalonia 原生 Window 作为弹窗宿主。
/// </summary>
public class DialogService : IDialogService
{
    /// <summary>显示确认对话框。</summary>
    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var owner = GetMainWindow();
        if (owner == null) return false;

        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 16 };

        stack.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var cancelBtn = new Button { Content = "取消", Padding = new Thickness(16, 8), MinWidth = 80 };
        var confirmBtn = new Button
        {
            Content = "确认",
            Padding = new Thickness(16, 8),
            MinWidth = 80,
        };

        cancelBtn.Click += (_, _) => dialog.Close(false);
        confirmBtn.Click += (_, _) => dialog.Close(true);

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);
        stack.Children.Add(btnPanel);

        dialog.Content = stack;
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>显示信息提示框。</summary>
    public async Task ShowMessageAsync(string title, string message)
    {
        await ShowSimpleDialogAsync(title, message, "ℹ️", Brushes.DodgerBlue);
    }

    /// <summary>显示警告提示框。</summary>
    public async Task ShowWarningAsync(string title, string message)
    {
        await ShowSimpleDialogAsync(title, message, "⚠️", Brushes.Orange);
    }

    /// <summary>显示错误提示框。</summary>
    public async Task ShowErrorAsync(string title, string message)
    {
        await ShowSimpleDialogAsync(title, message, "❌", Brushes.Red);
    }

    /// <summary>显示自定义对话框。</summary>
    public async Task<TResult?> ShowCustomAsync<TResult>(IDialogViewModel<TResult?> viewModel)
    {
        var owner = GetMainWindow();
        if (owner == null) return default;

        var dialog = new Window
        {
            Title = viewModel.Title,
            Width = viewModel.Width,
            Height = viewModel.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        // 简单内容展示（实际场景可用 DataTemplate 自动匹配）
        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        stack.Children.Add(new TextBlock
        {
            Text = $"自定义对话框: {viewModel.Title}",
            FontSize = 14,
        });

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var cancelBtn = new Button { Content = "取消", Padding = new Thickness(16, 8), MinWidth = 80 };
        var confirmBtn = new Button
        {
            Content = "确认",
            Padding = new Thickness(16, 8),
            MinWidth = 80,
            IsEnabled = viewModel.CanConfirm,
        };

        var result = default(TResult);
        cancelBtn.Click += (_, _) => { viewModel.Cancel(); dialog.Close(); };
        confirmBtn.Click += (_, _) => { result = viewModel.GetResult(); dialog.Close(); };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);
        stack.Children.Add(btnPanel);

        dialog.Content = stack;
        await dialog.ShowDialog<TResult?>(owner);
        return result;
    }

    private static async Task ShowSimpleDialogAsync(string title, string message, string icon, IBrush iconColor)
    {
        var owner = GetMainWindow();
        if (owner == null) return;

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var stack = new StackPanel { Margin = new Thickness(24), Spacing = 12 };

        stack.Children.Add(new TextBlock
        {
            Text = $"{icon} {title}",
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = iconColor,
        });

        stack.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var okBtn = new Button
        {
            Content = "确定",
            Padding = new Thickness(16, 8),
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Click += (_, _) => dialog.Close();

        stack.Children.Add(okBtn);
        dialog.Content = stack;
        await dialog.ShowDialog(owner);
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
