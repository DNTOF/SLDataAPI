using System;
using System.Threading;
using MEC;

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
        using var done = new ManualResetEventSlim(false);

        try
        {
            Timing.CallDelayed(0f, () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    done.Set();
                }
            });
        }
        catch (Exception ex)
        {
            // Timing 未初始化（服务器退出 / 插件加载早期）时直接失败，不阻塞调用方
            error = ex;
            return false;
        }

        bool completed = done.Wait(timeoutMs);
        if (!completed)
        {
            error = new TimeoutException("主线程执行超时（服务器可能卡顿、正在切图或已停止响应）");
            return false;
        }

        error = captured!;
        return captured == null;
    }

    public static T RunOnMainThread<T>(Func<T> func, out Exception error, int timeoutMs = 5000)
    {
        T result = default!;
        Exception? captured = null;
        using var done = new ManualResetEventSlim(false);

        try
        {
            Timing.CallDelayed(0f, () =>
            {
                try
                {
                    result = func();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
                finally
                {
                    done.Set();
                }
            });
        }
        catch (Exception ex)
        {
            error = ex;
            return default!;
        }

        bool completed = done.Wait(timeoutMs);
        if (!completed)
        {
            error = new TimeoutException("主线程执行超时（服务器可能卡顿、正在切图或已停止响应）");
            return default!;
        }

        error = captured!;
        return captured == null ? result : default!;
    }
}
