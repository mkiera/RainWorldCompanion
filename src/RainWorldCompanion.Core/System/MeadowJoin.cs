// Usings sit above the namespace: RainWorldCompanion.Core.System would otherwise shadow System.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using RainWorldCompanion.Core.Mods;

namespace RainWorldCompanion.Core.System;

public sealed record MeadowJoin
{
    public const string SteamLobbyToken = "+connect_lobby";

    public const string LanLobbyToken = "+connect_lan_lobby";

    public const string PasswordToken = "+lobby_password";

    public const string InviteLinkPrefix = "steam://joinlobby/";

    public const string GameExecutableName = "RainWorld.exe";

    private const int MaxPasswordLength = 200;

    public string SteamLobbyId { get; init; } = "";

    // Rain Meadow writes and reads the obsolete IPAddress.Address packing, not dotted text.
    public string LanAddress { get; init; } = "";

    public string LanPort { get; init; } = "";

    public string Password { get; init; } = "";

    public bool IsLan => LanAddress.Length > 0;

    public bool HasPassword => Password.Length > 0;

    public string JoinCode
    {
        get
        {
            var code = new StringBuilder();
            if (IsLan)
            {
                code.Append(LanLobbyToken).Append(' ').Append(LanAddress).Append(' ').Append(LanPort);
            }
            else
            {
                code.Append(SteamLobbyToken).Append(' ').Append(SteamLobbyId);
            }

            if (HasPassword)
            {
                code.Append(' ').Append(PasswordToken).Append(' ').Append(Password);
            }

            return code.ToString();
        }
    }

    public string Describe => IsLan
        ? "Lobby on " + DottedLanAddress + " port " + LanPort
        : "Lobby " + SteamLobbyId;

    public string DottedLanAddress
    {
        get
        {
            if (!long.TryParse(LanAddress, NumberStyles.Integer, CultureInfo.InvariantCulture, out long packed))
            {
                return LanAddress;
            }

            try
            {
                return new IPAddress(packed).ToString();
            }
            catch (ArgumentException)
            {
                return LanAddress;
            }
        }
    }

    // Takes a bare lobby id, a join code the mod prints, or a Steam invite link.
    public static MeadowJoin? Read(string? text, out string? problem)
    {
        problem = null;

        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            problem = "Paste a lobby id, a join code, or a Steam invite link.";
            return null;
        }

        if (trimmed.StartsWith(InviteLinkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ReadInviteLink(trimmed, out problem);
        }

        string[] words = trimmed.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);

        string password = ReadPassword(words, out problem);
        if (problem is not null)
        {
            return null;
        }

        int steamAt = IndexOf(words, SteamLobbyToken);
        if (steamAt >= 0)
        {
            return ReadSteamCode(words, steamAt, password, out problem);
        }

        int lanAt = IndexOf(words, LanLobbyToken);
        if (lanAt >= 0)
        {
            return ReadLanCode(words, lanAt, password, out problem);
        }

        if (words.Length == 1 && IsLobbyId(words[0]))
        {
            return new MeadowJoin { SteamLobbyId = words[0], Password = password };
        }

        problem = "That is not a lobby id, a join code, or a Steam invite link.";
        return null;
    }

    public MeadowJoin WithPassword(string? password)
    {
        string cleaned = (password ?? "").Trim();
        return cleaned.Length == 0 ? this : this with { Password = cleaned };
    }

    // Steam's invite link carries no password and no local address, so those start the game itself.
    public MeadowStart Start(string? gameInstallPath)
    {
        if (!IsLan && !HasPassword)
        {
            return MeadowStart.ForSteam(
                InviteLinkPrefix + CurrentModsReader.SteamAppId + "/" + SteamLobbyId,
                Describe);
        }

        if (WhyPasswordCannotTravel() is { } refusal)
        {
            return MeadowStart.Refused(refusal);
        }

        if (string.IsNullOrWhiteSpace(gameInstallPath))
        {
            return MeadowStart.Refused(
                "The game folder is not set, so Rain World cannot be started with a password or a "
                + "local address. Set it in Settings, or join a lobby that has no password.");
        }

        string executable;
        try
        {
            executable = Path.Combine(gameInstallPath.Trim(), GameExecutableName);
        }
        catch (ArgumentException)
        {
            return MeadowStart.Refused("The game folder in Settings is not a usable path.");
        }

        if (!FileExists(executable))
        {
            return MeadowStart.Refused(
                $"'{gameInstallPath}' holds no {GameExecutableName}, so the game cannot be started from here.");
        }

        var arguments = new List<string>();
        if (IsLan)
        {
            arguments.Add(LanLobbyToken);
            arguments.Add(LanAddress);
            arguments.Add(LanPort);
        }
        else
        {
            arguments.Add(SteamLobbyToken);
            arguments.Add(SteamLobbyId);
        }

        if (HasPassword)
        {
            arguments.Add(PasswordToken);
            arguments.Add(Password);
        }

        string because = IsLan
            ? "Steam cannot join a lobby on the local network."
            : "Steam cannot carry a password.";
        return MeadowStart.ForGame(executable, arguments, Describe, because);
    }

    private string? WhyPasswordCannotTravel()
    {
        if (!HasPassword)
        {
            return null;
        }

        // JoinLobbyUsingCode splits the whole command line on single spaces, so a password holding
        // one never reaches the mod whole.
        foreach (char character in Password)
        {
            if (char.IsWhiteSpace(character))
            {
                return "Rain Meadow reads a password up to the first space, so one with a space in "
                    + "it cannot be passed when the game starts.";
            }
        }

        return Password.Length > MaxPasswordLength
            ? "That password is too long to pass when the game starts."
            : null;
    }

    private static MeadowJoin? ReadInviteLink(string link, out string? problem)
    {
        problem = null;

        string[] parts = link.Substring(InviteLinkPrefix.Length)
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2 || !IsLobbyId(parts[1]))
        {
            problem = "That invite link carries no lobby id.";
            return null;
        }

        if (!string.Equals(parts[0], CurrentModsReader.SteamAppId, StringComparison.Ordinal))
        {
            problem = "That invite link is for another game, not Rain World.";
            return null;
        }

        return new MeadowJoin { SteamLobbyId = parts[1] };
    }

    private static MeadowJoin? ReadSteamCode(string[] words, int at, string password, out string? problem)
    {
        problem = null;

        if (at + 1 >= words.Length || !IsLobbyId(words[at + 1]))
        {
            problem = "That join code names no lobby id after " + SteamLobbyToken + ".";
            return null;
        }

        return new MeadowJoin { SteamLobbyId = words[at + 1], Password = password };
    }

    private static MeadowJoin? ReadLanCode(string[] words, int at, string password, out string? problem)
    {
        problem = null;

        if (at + 2 >= words.Length)
        {
            problem = "That join code names no address and port after " + LanLobbyToken + ".";
            return null;
        }

        string? address = PackedAddress(words[at + 1]);
        if (address is null)
        {
            problem = $"'{words[at + 1]}' is not an address Rain Meadow can read.";
            return null;
        }

        if (!int.TryParse(words[at + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            problem = $"'{words[at + 2]}' is not a port number.";
            return null;
        }

        return new MeadowJoin
        {
            LanAddress = address,
            LanPort = port.ToString(CultureInfo.InvariantCulture),
            Password = password,
        };
    }

    // Dotted text is packed the way the mod writes it, so a typed address works as well as a code.
    private static string? PackedAddress(string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long packed)
            && packed is >= 0 and <= uint.MaxValue)
        {
            return packed.ToString(CultureInfo.InvariantCulture);
        }

        if (!IPAddress.TryParse(text, out IPAddress? parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        byte[] bytes = parsed.GetAddressBytes();
        long value = bytes[0] | ((long)bytes[1] << 8) | ((long)bytes[2] << 16) | ((long)bytes[3] << 24);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string ReadPassword(string[] words, out string? problem)
    {
        problem = null;

        int at = IndexOf(words, PasswordToken);
        if (at < 0)
        {
            return "";
        }

        if (at + 1 >= words.Length)
        {
            problem = "That join code names no password after " + PasswordToken + ".";
            return "";
        }

        return words[at + 1];
    }

    private static int IndexOf(string[] words, string token)
    {
        for (int index = 0; index < words.Length; index++)
        {
            if (string.Equals(words[index], token, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    // A CSteamID is 64 bits and the mod parses it with ulong.TryParse.
    private static bool IsLobbyId(string text)
    {
        if (text.Length is < 6 or > 20)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id) && id != 0;
    }

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record MeadowStart
{
    private MeadowStart()
    {
    }

    public string SteamUrl { get; private init; } = "";

    public string Executable { get; private init; } = "";

    public IReadOnlyList<string> Arguments { get; private init; } = Array.Empty<string>();

    public string? Problem { get; private init; }

    public string Headline { get; private init; } = "";

    public bool CanRun => Problem is null;

    public bool ThroughSteam => SteamUrl.Length > 0;

    internal static MeadowStart ForSteam(string url, string headline) => new()
    {
        SteamUrl = url,
        Headline = headline + ", through Steam.",
    };

    internal static MeadowStart ForGame(
        string executable,
        IReadOnlyList<string> arguments,
        string headline,
        string because) => new()
    {
        Executable = executable,
        Arguments = arguments,
        Headline = headline + ", starting Rain World here. " + because,
    };

    internal static MeadowStart Refused(string problem) => new() { Problem = problem };
}
