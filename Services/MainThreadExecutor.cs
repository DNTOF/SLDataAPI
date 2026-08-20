using System;
using System.Threading;
using MEC;

namespace SLDataAPI.Services;

/// <summary>
/// HttpServer 的连接处理在线程池线程上运行，但游戏 / Mirror 网络相关的 API
/// （踢人、传送、执行控制台命令等）必须在 Unity 主线程调用，否则有崩服风险。
/// 这里用 MEC 的 Timing.CallDelayed(0f, ...) 把委托丢回主线程下一帧执行，
/// 并用 ManualResetEventSlim 同步等待结果（带超时），
/// 这样对调用方（HTTP handler）而言接口依然是“同步返回”的。
///
/// ⚠ 注意：本类名不能叫 MainThreadDispatcher —— 游戏程序集 Assembly-CSharp
/// 里已存在同名类型，同名会导致 CS0436 冲突警告并带来运行时混淆风险。
/// </summary>
public static class MainThreadExecutor
{
    public static bool RunOnMainThread(Action action, out Exception error, int timeoutMs = 5000)
    {
        Exception? captured = null;
        var done = new ManualResetEventSlim(false);
        bool cancelled = false; // 超时后置位：迟到的 action 跳过执行（尽力而为）

        try
        {
            Timing.CallDelayed(0f, () =>
            {
                try
                {
                    if (cancelled) return; // 已超时：调用方不再等待，跳过执行（若已在执行则无法阻止）
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    try { done.Set(); } catch (ObjectDisposedException) { /* 超时后 done 已释放 */ }
                }
            });
        }
        catch (Exception ex)
        {
            // Timing 未初始化（服务器退出 / 插件加载早期）时直接失败，不阻塞调用方
            done.Dispose();
            error = ex;
            return false;
        }

        bool completed = done.Wait(timeoutMs);
        done.Dispose();
        if (!completed)
        {
            cancelled = true; // N-02：标记迟到 action 跳过，避免"报超时但实际执行"导致的重试双重执行
            error = new TimeoutException(
                "主线程执行超时（服务器可能卡顿、正在切图或已停止响应）" +
                "——若主线程在超时后恢复且操作已经开始，仍可能执行一次，请勿盲目重试非幂等操作");
            return false;
        }

        error = captured!;
        return captured == null;
    }

    public static T RunOnMainThread<T>(Func<T> func, out Exception error, int timeoutMs = 5000)
    {
        T result = default!;
        Exception? captured = null;
        var done = new ManualResetEventSlim(false);
        bool cancelled = false; // 超时后置位：迟到的 action 跳过执行（尽力而为）

        try
        {
            Timing.CallDelayed(0f, () =>
            {
                try
                {
                    if (cancelled) return; // 已超时：跳过执行（若已在执行则无法阻止）
                    result = func();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    try { done.Set(); } catch (ObjectDisposedException) { /* 超时后 done 已释放 */ }
                }
            });
        }
        catch (Exception ex)
        {
            done.Dispose();
            error = ex;
            return default!;
        }

        bool completed = done.Wait(timeoutMs);
        done.Dispose();
        if (!completed)
        {
            cancelled = true; // N-02
            error = new TimeoutException(
                "主线程执行超时（服务器可能卡顿、正在切图或已停止响应）" +
                "——若主线程在超时后恢复且操作已经开始，仍可能执行一次，请勿盲目重试非幂等操作");
            return default!;
        }

        error = captured!;
        return captured == null ? result : default!;
    }
}
