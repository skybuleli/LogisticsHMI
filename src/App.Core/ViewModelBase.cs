using CommunityToolkit.Mvvm.ComponentModel;

namespace App.Core;

/// <summary>
/// 所有 ViewModel 的基类。
/// 提供 IsBusy、错误处理等通用功能。
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>
    /// 指示当前是否有后台操作正在进行。
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// 当前的错误消息，若为空则表示无错误。
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// 标题（用于页面标题栏或面包屑导航）。
    /// </summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>
    /// 设置错误消息。
    /// </summary>
    protected void SetError(string message)
    {
        ErrorMessage = message;
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>
    /// 清除错误消息。
    /// </summary>
    protected void ClearError()
    {
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>
    /// 是否存在错误。
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// 在 Busy 状态下执行异步操作，自动处理异常。
    /// </summary>
    protected async Task ExecuteBusyAsync(Func<Task> operation, string? errorContext = null)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            ClearError();
            await operation();
        }
        catch (Exception ex)
        {
            var context = errorContext ?? GetType().Name;
            SetError($"[{context}] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
