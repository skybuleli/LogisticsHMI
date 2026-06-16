namespace App.Core;

/// <summary>
/// 任务状态。
/// </summary>
public enum TaskStatus
{
    Pending = 0,
    Assigned = 1,
    Running = 2,
    Paused = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}
