using System.Threading.Tasks;
using App.Core;

namespace App.UI.Services;

/// <summary>
/// 对话框服务接口。
/// 允许 ViewModel 在不解耦于具体 Window 的情况下显示弹窗。
/// </summary>
public interface IDialogService
{
    /// <summary>显示确认对话框。</summary>
    /// <param name="title">标题</param>
    /// <param name="message">消息</param>
    /// <returns>true 表示用户确认，false 表示取消</returns>
    Task<bool> ShowConfirmAsync(string title, string message);

    /// <summary>显示信息提示框。</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>显示警告提示框。</summary>
    Task ShowWarningAsync(string title, string message);

    /// <summary>显示错误提示框。</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>显示自定义对话框 (ViewModel-based)。</summary>
    /// <typeparam name="TResult">对话框返回结果类型</typeparam>
    /// <param name="viewModel">对话框 ViewModel</param>
    /// <returns>对话框结果，取消则为 default</returns>
    Task<TResult?> ShowCustomAsync<TResult>(IDialogViewModel<TResult?> viewModel);
}

/// <summary>
/// ViewModel 基类对话框接口。
/// 实现此接口的 ViewModel 可通过 DialogService 作为自定义弹窗内容。
/// </summary>
public interface IDialogViewModel<TResult>
{
    /// <summary>对话框标题。</summary>
    string Title { get; }

    /// <summary>对话框宽度。</summary>
    double Width { get; }

    /// <summary>对话框高度。</summary>
    double Height { get; }

    /// <summary>用户可以确认提交。</summary>
    bool CanConfirm { get; }

    /// <summary>确认对话框（返回结果）。</summary>
    TResult GetResult();

    /// <summary>取消对话框。</summary>
    void Cancel();
}
