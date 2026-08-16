using LabApi.Features.Console;

namespace SLDataAPI;

/// <summary>
/// 日志门面：统一走 LabAPI 的 Logger，同时保持项目原有的 Log.Info/Warn/Error/Debug 调用形态。
/// LabAPI 的 Logger.Debug 没有全局开关（只有逐调用参数），这里按 Config.Debug 统一门控。
/// </summary>
public static class Log
{
    public static void Debug(string message) =>
        Logger.Debug(message, Plugin.Instance?.Config.Debug ?? false);

    public static void Info(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);
}
