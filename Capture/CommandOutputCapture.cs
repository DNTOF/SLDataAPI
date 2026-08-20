using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace SLDataAPI.Capture;

/// <summary>
/// 捕获服务器控制台输出（命令执行的"回显"）。
///
/// 背景：Server.RunCommand 只返回命令的直接返回值，而插件注册的命令
/// （如 SLPlayer 的 .m 系列、游戏内控制台命令）通过 CommandSender 消息通道
/// 输出 —— 最终都汇聚到 ServerConsole.AddLog（LocalAdmin 上看到的就是它）。
/// 这里用 Harmony Postfix patch AddLog，在命令执行窗口内把日志行追加到缓冲，
/// 随 /control/command 响应一起返回，让 WebUI 控制台能看到完整回显。
///
/// 线程模型：AddLog 在 Unity 主线程被调用；Begin/End 在 HttpServer 的请求线程，
/// 用 lock 保护缓冲。
/// </summary>
public static class CommandOutputCapture
{
    private static readonly object Lock = new object();
    // 会话栈：并发 /control/command（多请求同时捕获）各自独立缓冲，End 弹栈取回自己的输出——
    // 修复"全局单缓冲串台"（A 的输出被 B 的 End 取走）
    private static readonly Stack<StringBuilder> CaptureStack = new Stack<StringBuilder>();

    private static Harmony? _harmony;

    /// <summary>服务器启动时调用一次（Plugin.Enable）。任何失败只警告，不影响插件其他功能。</summary>
    public static void Init()
    {
        if (_harmony != null)
            return;

        try
        {
            _harmony = new Harmony("com.dntof.sldataapi");
            int patched = 0;

            // 通道 1：ServerConsole.AddLog —— EXILED 日志等控制台日志
            if (PatchIfFound(typeof(ServerConsole), "AddLog",
                new[] { typeof(string), typeof(ConsoleColor), typeof(bool) },
                nameof(OnAddLogPostfix)))
                patched++;

            // 通道 2：CommandSender.Respond / Print —— 命令系统响应主通道。
            // 注意：这些是 CommandSender 基类声明的虚方法，Harmony 规定虚方法
            // 必须在声明类型上 patch（在子类上 patch 会抛 ArgumentException）。
            // 在基类声明处 patch 能捕获所有子类（ServerConsoleSender / PlayerCommandSender 等）的响应。
            Type? senderType = typeof(ServerConsole).Assembly.GetType("CommandSender");
            if (senderType != null)
            {
                if (PatchIfFound(senderType, "Respond", new[] { typeof(string), typeof(bool) }, nameof(OnMessagePostfix)))
                    patched++;
                if (PatchIfFound(senderType, "Print", new[] { typeof(string) }, nameof(OnMessagePostfix)))
                    patched++;
                if (PatchIfFound(senderType, "Print", new[] { typeof(string), typeof(ConsoleColor) }, nameof(OnMessagePostfix)))
                    patched++;
                if (PatchIfFound(senderType, "Print", new[] { typeof(string), typeof(ConsoleColor), typeof(UnityEngine.Color) }, nameof(OnMessagePostfix)))
                    patched++;
            }

            if (patched == 0)
                Log.Warn("[SLDataAPI] 命令输出捕获 patch 全部失败，控制台回显不可用");
            else
                Log.Debug($"[SLDataAPI] 命令输出捕获已就绪（{patched} 个 patch）");
        }
        catch (Exception ex)
        {
            // 捕获初始化失败绝不能让插件 enable 崩溃
            Log.Error($"[SLDataAPI] 命令输出捕获初始化失败（不影响其他功能）: {ex.Message}");
            _harmony = null;
        }
    }

    /// <summary>按签名查找目标方法并 patch postfix；找不到/不可 patch 返回 false。</summary>
    private static bool PatchIfFound(Type targetType, string method, Type[] argTypes, string postfixName)
    {
        MethodInfo? target = targetType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance, null, argTypes, null);
        // 抽象方法没有方法体，无法 patch；跳过
        if (target == null || target.IsAbstract)
            return false;

        MethodInfo? postfix = typeof(CommandOutputCapture).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
        if (postfix == null)
            return false;

        try
        {
            _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
            return true;
        }
        catch (Exception ex)
        {
            // 单个方法 patch 失败不影响其他通道
            Log.Warn($"[SLDataAPI] patch {method} 失败（忽略）: {ex.Message}");
            return false;
        }
    }

    /// <summary>服务器关闭时调用（Plugin.Disable）。</summary>
    public static void Shutdown()
    {
        if (_harmony == null)
            return;
        try
        {
            _harmony.UnpatchAll("com.dntof.sldataapi");
        }
        catch { /* 卸载期间异常忽略 */ }
        _harmony = null;
    }

    /// <summary>开始捕获（命令执行前调用）；支持并发嵌套（栈式，各会话独立缓冲）。</summary>
    public static void BeginCapture()
    {
        lock (Lock)
        {
            CaptureStack.Push(new StringBuilder());
        }
    }

    /// <summary>结束捕获并返回本会话积累的输出（命令执行后调用）。</summary>
    public static string EndCapture()
    {
        lock (Lock)
        {
            if (CaptureStack.Count == 0) return "";
            return CaptureStack.Pop().ToString().TrimEnd('\r', '\n');
        }
    }

    // Harmony Postfix：AddLog(string message, ConsoleColor color, bool fromConsole)
    private static void OnAddLogPostfix(string __0)
    {
        Append(__0);
    }

    // Harmony Postfix：ServerConsoleSender.Respond(string, bool) / Print(...)
    // __0 始终是消息文本（不带 "[UNENCRYPTED FROM SERVER]" 前缀，前缀是 Print 内部加的）
    private static void OnMessagePostfix(string __0)
    {
        Append(__0);
    }

    private static void Append(string message)
    {
        lock (Lock)
        {
            if (CaptureStack.Count > 0 && message != null)
                CaptureStack.Peek().AppendLine(message);
        }
    }
}
