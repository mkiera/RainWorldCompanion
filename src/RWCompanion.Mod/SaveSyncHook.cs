using System.Reflection;
using HarmonyLib;

namespace RWCompanion.Mod;

// Rain Meadow reloads the save through PlayerProgression.LoadProgression on lobby join and leave.
internal static class SaveSyncHook
{
    // Longer than Companion's own 4 second budget, so its answer arrives first.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(6);

    private static LiveTransport? _transport;
    private static Func<bool>? _enabled;
    private static Action<string>? _log;
    private static Harmony? _harmony;

    internal static void Install(LiveTransport transport, Func<bool> enabled, Action<string> log)
    {
        _transport = transport;
        _enabled = enabled;
        _log = log;
        if (_harmony != null) return;
        var method = GameAccess.FindType("PlayerProgression")?.GetMethod("LoadProgression", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        if (method == null)
        {
            log("Autosync before load is unavailable: PlayerProgression.LoadProgression was not found.");
            return;
        }
        try
        {
            var harmony = new Harmony("rwcompanion.autosync");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(SaveSyncHook).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
            _harmony = harmony;
        }
        catch (Exception exception)
        {
            log("Autosync before load is unavailable: " + exception.GetBaseException().Message);
        }
    }

    internal static void Uninstall()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        _transport = null;
        _enabled = null;
        _log = null;
    }

    private static void Prefix(object __instance)
    {
        try
        {
            if (_transport == null || _enabled?.Invoke() != true) return;
            var options = GameAccess.Get(GameAccess.Get(__instance, "rainWorld"), "options");
            if (GameAccess.Get(options, "saveSlot") is not int saveSlot || saveSlot < 0) return;
            int slot = saveSlot + 1;
            if (_transport.SyncSave(slot, Wait) is { } message)
                _log?.Invoke("Autosync slot " + slot + " before load: " + message);
        }
        catch (Exception exception)
        {
            _log?.Invoke("Autosync before load failed: " + exception.GetBaseException().Message);
        }
    }
}
