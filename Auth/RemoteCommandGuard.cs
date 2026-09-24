using System;
using System.Collections.Generic;

namespace SLDataAPI.Auth;

/// <summary>
/// SLDataAPI 自身管理 CLI（<c>sldataapi</c> / 别名 <c>slda</c>）的远程执行硬拒绝，
/// 以及"当前线程正在执行远程控制通道下发的命令"标记。
///
/// 动机：远程控制通道只能证明"请求持有某把 API Key"，无法证明操作者身份。
/// 一旦某把 Key 拿到 /control/console/ 授权，就能凭控制台命令无限增发新 Key，
/// 并从同一条通道取回明文（越权提升 + 密钥外泄）。因此密钥管理只在服务器本地控制台可用。
///
/// 纯逻辑，无 Unity / LabAPI 依赖，可供单元测试直接链接。
/// </summary>
public static class RemoteCommandGuard
{
    /// <summary>管理 CLI 的根命令名（含别名），大小写不敏感。</summary>
    private static readonly HashSet<string> ManagementRoots =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sldataapi", "slda" };

    private static readonly char[] TokenSeparators = { ' ', '\t', '\r', '\n', '\f', '\v' };

    /// <summary>给上游平台的机器可读拒绝码。</summary>
    public const string RemoteDenyCode = "sldataapi_cli_forbidden";

    /// <summary>控制面拒绝文案（HTTP 403 / WS result 的 message）。</summary>
    public const string RemoteDenyMessage =
        "拒绝执行：SLDataAPI 管理命令（sldataapi / slda）不允许通过远程控制通道执行。" +
        "API Key 的创建与吊销只能在服务器本地控制台（LocalAdmin / RemoteAdmin / 游戏内控制台）操作。";

    /// <summary>
    /// 命令是否会调起 SLDataAPI 管理 CLI。逐个空白分隔的 token 判定（不只看首 token，
    /// 顺带挡住任何把命令当参数转发的包装命令），token 两端的点号/斜杠/引号等非字词字符先剥离，
    /// 因此 <c>.sldataapi</c>、<c>"slda"</c>、<c>/SLDataAPI</c> 一并命中。
    /// 宁可多挡：误挡会返回明确错误，漏挡等于密钥可被远程增发。
    /// </summary>
    public static bool IsManagementCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        foreach (string token in command!.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ManagementRoots.Contains(TrimToWord(token)))
                return true;
        }
        return false;
    }

    /// <summary>剥离 token 两端的非字词字符（点号、斜杠、引号、零宽字符等）。</summary>
    private static string TrimToWord(string token)
    {
        int start = 0;
        int end = token.Length - 1;
        while (start <= end && !IsWordChar(token[start])) start++;
        while (end >= start && !IsWordChar(token[end])) end--;
        return start > end ? "" : token.Substring(start, end - start + 1);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ────────────── 远程执行上下文标记 ──────────────

    // 远程控制通道的命令在主线程同步执行，被它调起的子命令也在同一线程内同步执行，
    // 所以线程级计数即可精确区分"远程下发"与"本地操作者亲自敲的命令"，不会误伤并发请求。
    [ThreadStatic] private static int _remoteDepth;

    /// <summary>当前线程是否正在执行远程控制通道下发的命令。</summary>
    public static bool IsRemoteExecution => _remoteDepth > 0;

    /// <summary>远程命令执行作用域：<c>using var scope = RemoteExecutionScope.Enter();</c></summary>
    public struct RemoteExecutionScope : IDisposable
    {
        private bool _entered;

        public static RemoteExecutionScope Enter()
        {
            _remoteDepth++;
            return new RemoteExecutionScope { _entered = true };
        }

        public void Dispose()
        {
            if (!_entered) return;
            _entered = false;
            if (_remoteDepth > 0) _remoteDepth--;
        }
    }
}
