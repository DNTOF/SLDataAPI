using System.Threading;

namespace SLDataAPI.Control;

/// <summary>
/// 控制 WS 单连接并发 call 闸。403（ACL）与 429（超限）都必须配对 Exit，
/// 否则 <c>_pendingCalls</c> 泄漏，满 4 次未授权后该会话会永久 429。
/// 无网络 / LabAPI 依赖，可供单元测试直接链接。
/// </summary>
public static class WsCallGate
{
    public const int DefaultMaxConcurrent = 4;

    /// <summary>占用一个并发槽。超限时已回滚并返回 false（调用方回 429）。</summary>
    public static bool TryEnter(ref int pending, int max)
    {
        if (max <= 0) max = DefaultMaxConcurrent;
        if (Interlocked.Increment(ref pending) > max)
        {
            Interlocked.Decrement(ref pending);
            return false;
        }
        return true;
    }

    /// <summary>释放槽：ACL 失败（403）与 call 完成（finally）都必须调用。</summary>
    public static void Exit(ref int pending) => Interlocked.Decrement(ref pending);
}
