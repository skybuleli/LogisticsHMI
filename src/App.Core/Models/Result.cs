namespace App.Core;

/// <summary>
/// 表示一个可能失败的操作结果。
/// </summary>
public class Result
{
    /// <summary>
    /// 成功结果。
    /// </summary>
    public static Result Success() => new() { IsSuccess = true };

    /// <summary>
    /// 失败结果。
    /// </summary>
    public static Result Failure(string error)
    {
        return new Result { IsSuccess = false, Error = error };
    }

    /// <summary>
    /// 是否成功。
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// 错误信息。
    /// </summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 表示一个可能失败、并携带泛型结果的操作结果。
/// </summary>
public sealed class Result<T> : Result
{
    /// <summary>
    /// 成功结果。
    /// </summary>
    public static Result<T> Success(T value) => new() { IsSuccess = true, Value = value };

    /// <summary>
    /// 失败结果。
    /// </summary>
    public static new Result<T> Failure(string error) => new() { IsSuccess = false, Error = error };

    /// <summary>
    /// 结果值。
    /// </summary>
    public T? Value { get; init; }
}
