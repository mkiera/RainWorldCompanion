using System.Collections;
using System.Reflection;

namespace RWCompanion.Mod;

internal static class GameAccess
{
    private static readonly Dictionary<string, MemberInfo?> Members = new();
    internal static Type? FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);

    internal static object? Get(object? target, string name)
    {
        if (target == null) return null;
        var type = target as Type ?? target.GetType();
        string key = type.AssemblyQualifiedName + ":" + name;
        if (!Members.TryGetValue(key, out var member))
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            member = (MemberInfo?)type.GetField(name, flags) ?? type.GetProperty(name, flags);
            Members[key] = member;
        }
        object? instance = target is Type ? null : target;
        return member is FieldInfo field ? field.GetValue(instance) : (member as PropertyInfo)?.GetValue(instance, null);
    }

    internal static string Text(object? target, string name) => Convert.ToString(Get(target, name)) ?? "";
    internal static void Set(object target, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var type = target as Type ?? target.GetType();
        var instance = target is Type ? null : target;
        if (type.GetField(name, flags) is { } field) field.SetValue(instance, value);
        else if (type.GetProperty(name, flags) is { } property) property.SetValue(instance, value, null);
        else throw new MissingMemberException(type.FullName, name);
    }

    internal static object? Call(object target, string name, params object?[] arguments)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var type = target as Type ?? target.GetType();
        var method = type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length
            && m.GetParameters().Select((p, i) => arguments[i] == null ? !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null
                : (Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType).IsInstanceOfType(arguments[i])).All(match => match))
            ?? throw new MissingMethodException(type.FullName, name);
        return method.Invoke(target is Type ? null : target, arguments);
    }
    internal static string EnumValue(object? value) => Text(value, "value");
    internal static IEnumerable<object> Items(object? value) => value is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
}
