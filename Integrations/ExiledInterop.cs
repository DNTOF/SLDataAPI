using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SLDataAPI.Integrations;

/// <summary>
/// EXILED 运行时互操作桥（纯反射，零编译期依赖）。
/// 本插件已迁移为 LabAPI 原生插件，无法再引用 Exiled.Loader；
/// 但 EXILED 9 本身跑在 LabAPI 之上，若服务器同时安装了 EXILED
/// （Exiled.Loader 程序集已加载），即可在运行时反射读取其插件注册表，
/// 使 SLPlayer / OmegaWarhead 等 EXILED 插件的探测与控制继续可用。
/// 未安装 EXILED 时所有方法安全降级（返回空/失败），不抛异常、不报错。
/// </summary>
public static class ExiledInterop
{
    private static bool _resolved;

    private static MemberInfo? _pluginsMember;      // Exiled.Loader.Loader.Plugins（属性或字段）
    private static MethodInfo? _reloadMethod;       // Exiled.Loader.Loader.ReloadPlugins()
    private static PropertyInfo? _serializerProp;   // Exiled.Loader.Loader.Serializer

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Exiled.Loader", StringComparison.Ordinal));
            if (asm == null) return;

            var loader = asm.GetType("Exiled.Loader.Loader", throwOnError: false);
            if (loader == null) return;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            _pluginsMember = (MemberInfo?)loader.GetProperty("Plugins", flags)
                             ?? loader.GetField("Plugins", flags);
            _reloadMethod = loader.GetMethod("ReloadPlugins", flags, binder: null, Type.EmptyTypes, modifiers: null);
            _serializerProp = loader.GetProperty("Serializer", flags);
        }
        catch
        {
            // 任何解析失败都视为 EXILED 不可用
        }
    }

    /// <summary>当前进程内是否加载了可用的 EXILED Loader。</summary>
    public static bool IsAvailable
    {
        get { Resolve(); return _pluginsMember != null; }
    }

    /// <summary>EXILED 已加载的插件实例集合（可能为空；不可用时返回空集合）。</summary>
    public static IReadOnlyList<object> GetPlugins()
    {
        var list = new List<object>();
        Resolve();
        if (_pluginsMember == null) return list;
        try
        {
            object? value = _pluginsMember is PropertyInfo pi
                ? pi.GetValue(null)
                : ((FieldInfo?)_pluginsMember)?.GetValue(null);
            if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                    if (item != null)
                        list.Add(item);
            }
        }
        catch
        {
            // 读取失败视为无 EXILED 插件
        }
        return list;
    }

    /// <summary>按名称查找 EXILED 插件实例（大小写不敏感；找不到或 EXILED 不可用时返回 null）。</summary>
    public static object? FindPlugin(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            return GetPlugins().FirstOrDefault(p =>
                string.Equals(GetString(p, "Name"), name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static object? GetProp(object instance, string name)
    {
        try
        {
            var prop = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(instance);
        }
        catch { return null; }
    }

    private static string? GetString(object instance, string name) => GetProp(instance, name)?.ToString();

    /// <summary>读取 EXILED 插件的公开信息（Name/Author/Version/Prefix/Priority 的字符串形式）。</summary>
    public static (string Name, string Author, string Version, string Prefix, string Priority, string ConfigPath)? GetInfo(object plugin)
    {
        string name = GetString(plugin, "Name") ?? "";
        if (name.Length == 0) return null;
        return (
            name,
            GetString(plugin, "Author") ?? "",
            GetString(plugin, "Version") ?? "",
            GetString(plugin, "Prefix") ?? "",
            GetString(plugin, "Priority") ?? "",
            GetString(plugin, "ConfigPath") ?? ""
        );
    }

    /// <summary>读取 EXILED 插件配置里的 is_enabled（不可用/无配置时返回 null）。</summary>
    public static bool? GetIsEnabled(object plugin)
    {
        try
        {
            var config = GetProp(plugin, "Config");
            if (config == null) return null;
            var value = config.GetType()
                .GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(config);
            return value is bool b ? b : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 修改 EXILED 插件配置中的 is_enabled 并写回其配置文件（沿用 EXILED 自己的序列化器保证格式兼容），
    /// 随后调用方应再调用 <see cref="ReloadPlugins"/> 使其立即生效。
    /// 返回失败原因（成功返回 null）。
    /// </summary>
    public static string? SetPluginEnabled(object plugin, bool enabled)
    {
        try
        {
            var config = GetProp(plugin, "Config");
            if (config == null)
                return "插件没有配置对象";

            var isEnabled = config.GetType().GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance);
            if (isEnabled == null || !isEnabled.CanWrite)
                return "配置对象缺少可写的 IsEnabled";
            isEnabled.SetValue(config, enabled);

            string? configPath = GetString(plugin, "ConfigPath");
            if (string.IsNullOrWhiteSpace(configPath))
                return "插件未暴露 ConfigPath";

            Resolve();
            object? serializer = _serializerProp?.GetValue(null);
            if (serializer == null)
                return "EXILED 序列化器不可用";

            // 兼容 Serialize(object) 与 Serialize<T>(T) 两种签名
            var methods = serializer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Serialize").ToList();
            object? serialized = null;
            var plain = methods.FirstOrDefault(m => m.GetParameters().Length == 1 &&
                                                    m.GetParameters()[0].ParameterType == typeof(object));
            if (plain != null)
            {
                serialized = plain.Invoke(serializer, new[] { config });
            }
            else
            {
                var generic = methods.FirstOrDefault(m => m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                if (generic != null)
                    serialized = generic.MakeGenericMethod(config.GetType()).Invoke(serializer, new[] { config });
            }
            if (serialized is not string yaml)
                return "序列化结果不是文本";

            File.WriteAllText(configPath, yaml, System.Text.Encoding.UTF8);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>调用 EXILED 的 ReloadPlugins()（不可用或失败返回 false）。</summary>
    public static bool ReloadPlugins()
    {
        Resolve();
        if (_reloadMethod == null) return false;
        try { _reloadMethod.Invoke(null, null); return true; }
        catch { return false; }
    }
}
