using System;
using System.Threading;

namespace App.UI.Services;

/// <summary>
/// 单实例检测守卫。
/// 使用命名的系统 Mutex 确保只有一个应用实例运行。
/// </summary>
public class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "LogisticsHMI_SingleInstance_Mutex";
    private readonly Mutex _mutex;
    private bool _hasHandle;
    private bool _disposed;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(true, MutexName, out _hasHandle);
    }

    /// <summary>
    /// 当前进程是否拥有互斥体（即唯一实例）。
    /// </summary>
    public bool IsFirstInstance => _hasHandle;

    /// <summary>
    /// 尝试获取互斥体。返回 true 表示当前是第一个实例。
    /// </summary>
    public bool TryAcquire()
    {
        if (_hasHandle) return true;

        try
        {
            _hasHandle = _mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            // 前一个实例崩溃但互斥体未释放 — 我们可以获取
            _hasHandle = true;
        }

        return _hasHandle;
    }

    /// <summary>
    /// 通知已有实例激活其主窗口（通过发送信号等机制）。
    /// 当前实现简单记录日志——跨进程通信可用命名管道或 WCF 扩展。
    /// </summary>
    public static void NotifyExistingInstance()
    {
        // 预留：可在这里实现 IPC 通知（如命名管道、WM_COPYDATA）
        // 让已有实例的 WindowService.Show() 被调用
        Console.Error.WriteLine("已有实例正在运行。如果需要启动第二个实例，请先关闭已有实例。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hasHandle)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
