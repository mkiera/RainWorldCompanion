// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using Microsoft.Win32;

namespace RainWorldCompanion.Core.System;

public static class SteamPersonaName
{
    public static string? Find()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var root = steam?.GetValue("SteamPath") as string;
            if (string.IsNullOrWhiteSpace(root)) return null;
            using var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            uint? account = active?.GetValue("ActiveUser") is int value && value != 0 ? unchecked((uint)value) : null;
            return Read(Path.Combine(root, "config", "loginusers.vdf"), account);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? Read(string path, uint? activeAccountId)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null;
        }

        var tokens = Tokenize(text);
        var accounts = new List<(ulong Id, string Name, bool MostRecent)>();
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (!ulong.TryParse(tokens[index], out var id) || tokens[index + 1] != "{") continue;
            index += 2;
            var depth = 1;
            string? name = null;
            var mostRecent = false;
            while (index < tokens.Count && depth > 0)
            {
                if (tokens[index] == "{") { depth++; index++; continue; }
                if (tokens[index] == "}") { depth--; index++; continue; }
                if (depth == 1 && index + 1 < tokens.Count && tokens[index + 1] is not "{" and not "}")
                {
                    if (tokens[index].Equals("PersonaName", StringComparison.OrdinalIgnoreCase)) name = tokens[index + 1];
                    if (tokens[index].Equals("MostRecent", StringComparison.OrdinalIgnoreCase)) mostRecent = tokens[index + 1] == "1";
                    index += 2;
                    continue;
                }
                index++;
            }
            if (!string.IsNullOrWhiteSpace(name)) accounts.Add((id, name.Trim(), mostRecent));
            index--;
        }

        var selected = activeAccountId is { } active
            ? accounts.FirstOrDefault(account => (uint)(account.Id & uint.MaxValue) == active)
            : default;
        if (selected.Name is null) selected = accounts.FirstOrDefault(account => account.MostRecent);
        if (selected.Name is null && accounts.Count == 1) selected = accounts[0];
        return selected.Name;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        for (var index = 0; index < text.Length;)
        {
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            if (text[index] is '{' or '}') { tokens.Add(text[index++].ToString()); continue; }
            if (text[index] != '"') { index++; continue; }
            index++;
            var value = new global::System.Text.StringBuilder();
            while (index < text.Length && text[index] != '"')
            {
                if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is '\\' or '"') index++;
                value.Append(text[index++]);
            }
            if (index < text.Length) index++;
            tokens.Add(value.ToString());
        }
        return tokens;
    }
}
