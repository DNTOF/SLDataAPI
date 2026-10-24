// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace SLDataAPI.Services;

/// <summary>
/// 按名称查找游戏类型 / 成员并缓存结果（包括“找不到”）。用于节日专属功能：
/// 未来游戏版本删除或改名类型时，调用方得到 null 并返回 feature_unavailable，而不是 TypeLoadException。
/// 纯 .NET 反射，无 Unity 依赖，可单测。
/// </summary>
public static class ReflectionLookup
{
    private static readonly ConcurrentDictionary<string, Type?> Types = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, MemberInfo?> Members = new(StringComparer.Ordinal);

    /// <summary>查找次数（含缓存未命中），供单测确认缓存生效。</summary>
    internal static int ResolveCount;

    /// <summary>按完整类型名在已加载程序集中查找（结果缓存，找不到也缓存为 null）。</summary>
    public static Type? FindType(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        return Types.GetOrAdd(fullName, name =>
        {
            System.Threading.Interlocked.Increment(ref ResolveCount);
            try
            {
                var t = Type.GetType(name, throwOnError: false);
                if (t != null) return t;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        t = asm.GetType(name, throwOnError: false);
                        if (t != null) return t;
                    }
                    catch { /* 动态/损坏程序集：跳过 */ }
                }
            }
            catch { /* 查找失败按缺失处理 */ }
            return null;
        });
    }

    /// <summary>查找公开/非公开的实例或静态属性/字段（缓存）。</summary>
    public static MemberInfo? FindMember(Type? type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        string key = type.AssemblyQualifiedName + "::" + name;
        return Members.GetOrAdd(key, _ =>
        {
            System.Threading.Interlocked.Increment(ref ResolveCount);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static;
            try
            {
                return (MemberInfo?)type.GetProperty(name, flags) ?? type.GetField(name, flags);
            }
            catch (AmbiguousMatchException)
            {
                return type.GetProperties(flags).FirstOrDefault(p => p.Name == name);
            }
            catch { return null; }
        });
    }

    /// <summary>读取成员值；成员缺失或读取抛异常时返回 false（fail-closed）。target 为 null 表示静态成员。</summary>
    public static bool TryGetValue(MemberInfo? member, object? target, out object? value)
    {
        value = null;
        try
        {
            switch (member)
            {
                case PropertyInfo p when p.CanRead:
                    value = p.GetValue(target, null);
                    return true;
                case FieldInfo f:
                    value = f.GetValue(target);
                    return true;
            }
        }
        catch { /* 反射调用异常按不可用处理 */ }
        return false;
    }

    /// <summary>对象是否实现了指定完整名的接口（按名称比较，接口类型被删除时返回 false）。</summary>
    public static Type? GetImplementedInterface(object? obj, string interfaceFullName)
    {
        if (obj == null) return null;
        try
        {
            return obj.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == interfaceFullName);
        }
        catch { return null; }
    }

    /// <summary>清空缓存（仅单测使用）。</summary>
    internal static void ResetForTests()
    {
        Types.Clear();
        Members.Clear();
        ResolveCount = 0;
    }
}
