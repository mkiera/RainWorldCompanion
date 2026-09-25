using System.Reflection;

namespace RWCompanion.Mod;

internal static class RecoveryState
{
    internal static void Revive(object state)
    {
        for (var type = state.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty("alive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property?.GetSetMethod(true) is not { } setter) continue;
            setter.Invoke(state, new object[] { true });
            return;
        }
        throw new MissingMemberException(state.GetType().FullName, "alive setter");
    }
}
