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
    internal static string EnumValue(object? value) => Text(value, "value");
    internal static IEnumerable<object> Items(object? value) => value is IEnumerable items ? items.Cast<object>() : Enumerable.Empty<object>();
}
