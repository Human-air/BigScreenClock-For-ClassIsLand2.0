using System.Reflection;

namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 从别的程序集里挑一个公开静态方法：按名字 + 参数个数 + 参数类型，**不按名字直接取**。
///
/// 为什么要挑：反编译来的那些计算器后来给同名方法加了重载，直接 <c>GetMethod(名字)</c>
/// 一碰上重载就抛 <see cref="AmbiguousMatchException"/>。这个异常在调用链上被 catch 吞掉后，
/// 表现是「功能静默失效」——地震提醒就是这么从来没在大屏上弹出来过（2026-10-30 定位）。
/// </summary>
public static class StaticOverloadFinder
{
    /// <summary>
    /// 找「名字、参数个数、返回值类型都对、且每个参数类型都能接收 <paramref name="argumentType"/>」的那个。
    /// 同时有多个符合条件的（比如 double 与 double? 各一个）就取先找到的；找不到返回 null。
    /// </summary>
    public static MethodInfo? Find(Type? type, string name, int arity, Type returnType, Type argumentType)
    {
        if (type is null) return null;

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != name) continue;
            if (method.ReturnType != returnType) continue;

            var parameters = method.GetParameters();
            if (parameters.Length != arity) continue;

            var fits = true;
            foreach (var p in parameters)
            {
                if (p.ParameterType == argumentType) continue;

                // 可空值类型 / 装箱后的 object 也能接住 double 实参
                var underlying = Nullable.GetUnderlyingType(p.ParameterType);
                if (underlying == argumentType || p.ParameterType == typeof(object)) continue;

                fits = false;
                break;
            }

            if (fits) return method;
        }

        return null;
    }
}
