// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// Reads meadow.json into a <see cref="MeadowProfile"/>. Fail-soft in the same way as
/// <see cref="SaveMetadataExtractor"/>: a missing, empty or malformed file comes back as a
/// profile with <see cref="MeadowProfile.ParseError"/> set rather than an exception, because
/// this runs while listing a folder the user did not curate and a mod update can change the
/// file's shape at any time.
/// </summary>
internal static class MeadowProfileReader
{
    private const int MaxErrorLength = 200;

    /// <summary>
    /// Unknown members are skipped rather than rejected, because Rain Meadow adds fields to
    /// ProgressionData between versions and a new one must not turn the whole file unreadable.
    /// Skip is the default for this option and is set here so that stays deliberate.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Never throws. On failure returns a profile with ParseError set.</summary>
    public static MeadowProfile Read(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Failed("no file path");
            }

            if (!File.Exists(filePath))
            {
                return Failed("file not found");
            }

            // ReadAllText strips a byte order mark. JsonSerializer rejects one inside a string,
            // and a file written by a text editor rather than by the mod can carry it.
            return Parse(File.ReadAllText(filePath));
        }
        catch (Exception ex)
        {
            return Failed(Shorten(ex.Message) ?? ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reads <see cref="MeadowProfile.FileName"/> out of a save folder, which is either the live
    /// folder or a snapshot. Never throws.
    /// </summary>
    public static MeadowProfile ReadFromFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Failed("no folder path");
        }

        try
        {
            return Read(Path.Combine(folderPath, MeadowProfile.FileName));
        }
        catch (Exception ex)
        {
            return Failed(Shorten(ex.Message) ?? ex.GetType().Name);
        }
    }

    /// <summary>Reads the json text itself. Never throws.</summary>
    public static MeadowProfile Parse(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Failed("the file is empty");
            }

            ProfileDto? dto = JsonSerializer.Deserialize<ProfileDto>(json, Options);
            if (dto is null)
            {
                // The literal "null" parses fine and deserialises to nothing.
                return Failed("the file holds no profile");
            }

            // Well-formed json that carries no characterProgress key at all, "{}" being the
            // shortest example, is not a Rain Meadow profile. Every other key defaults to a
            // plausible value, so without this check such a file renders as an empty panel with
            // nothing to say why.
            if (dto.CharacterProgress is null)
            {
                return Failed("the file holds no character progression");
            }

            return new MeadowProfile
            {
                CollisionOn = dto.CollisionOn,
                DisplayNames = dto.DisplayNames,
                CharacterUnlockProgress = dto.CharacterUnlockProgress,
                CurrentlySelectedCharacter = Clean(dto.CurrentlySelectedCharacter),
                Characters = BuildCharacters(dto.CharacterProgress),
                ParseError = null,
            };
        }
        catch (JsonException ex)
        {
            return Failed(Shorten(ex.Message) ?? "the file is not valid json");
        }
        catch (Exception ex)
        {
            return Failed(Shorten(ex.Message) ?? ex.GetType().Name);
        }
    }

    /// <summary>
    /// Turns the "characterProgress" object into a list. The key is the character name, which
    /// the entry itself does not carry, so it is copied onto each one. Sorted by name because a
    /// json object has no order the UI can rely on.
    /// </summary>
    private static IReadOnlyList<MeadowCharacterProgress> BuildCharacters(
        Dictionary<string, CharacterDto?>? progress)
    {
        if (progress is null || progress.Count == 0)
        {
            return Array.Empty<MeadowCharacterProgress>();
        }

        var characters = new List<MeadowCharacterProgress>(progress.Count);
        foreach (KeyValuePair<string, CharacterDto?> entry in progress)
        {
            CharacterDto dto = entry.Value ?? new CharacterDto();
            characters.Add(new MeadowCharacterProgress
            {
                Name = Clean(entry.Key),
                PlayTimeMilliseconds = dto.TimePlayed,
                EmoteUnlockProgress = dto.EmoteUnlockProgress,
                SkinUnlockProgress = dto.SkinUnlockProgress,
                UnlockedEmotes = CleanList(dto.UnlockedEmotes),
                UnlockedSkins = CleanList(dto.UnlockedSkins),
                SaveLocation = Clean(dto.SaveLocation),
                EverSeenInMenu = dto.EverSeenInMenu,
                SelectedSkin = Clean(dto.SelectedSkin),
                TintAmount = dto.TintAmount,
                TintColor = Clean(dto.TintColor),
                EmoteHotbar = CleanList(dto.EmoteHotbar),
            });
        }

        characters.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return characters;
    }

    private static string Clean(string? value) => value ?? "";

    /// <summary>
    /// Drops nulls and blanks from a name list. Newtonsoft writes an ExtEnum as its string value,
    /// and an entry the mod could not resolve is written as null.
    /// </summary>
    private static IReadOnlyList<string> CleanList(List<string?>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Array.Empty<string>();
        }

        var cleaned = new List<string>(values.Count);
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                cleaned.Add(value);
            }
        }

        return cleaned.Count == 0 ? Array.Empty<string>() : cleaned;
    }

    private static MeadowProfile Failed(string reason) => new() { ParseError = reason };

    /// <summary>Collapses a message to one capped line so it fits a list row.</summary>
    private static string? Shorten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var flattened = new StringBuilder(message.Length);
        bool lastWasSpace = false;
        foreach (char c in message)
        {
            bool isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    flattened.Append(' ');
                }
            }
            else
            {
                flattened.Append(c);
            }

            lastWasSpace = isSpace;
        }

        // The reason renders inside parentheses in a list row, where a sentence stop reads oddly.
        string text = flattened.ToString().Trim().TrimEnd('.');
        if (text.Length <= MaxErrorLength)
        {
            return text;
        }

        return text.Substring(0, MaxErrorLength - 3) + "...";
    }

    /// <summary>
    /// The wire shape of meadow.json. The names are the field and property names of
    /// MeadowProgression.ProgressionData, which is what Newtonsoft writes with default settings.
    /// </summary>
    private sealed class ProfileDto
    {
        [JsonPropertyName("collisionOn")]
        public bool CollisionOn { get; set; }

        [JsonPropertyName("displayNames")]
        public bool DisplayNames { get; set; }

        [JsonPropertyName("characterUnlockProgress")]
        public int CharacterUnlockProgress { get; set; }

        [JsonPropertyName("currentlySelectedCharacter")]
        public string? CurrentlySelectedCharacter { get; set; }

        [JsonPropertyName("characterProgress")]
        public Dictionary<string, CharacterDto?>? CharacterProgress { get; set; }
    }

    /// <summary>
    /// The wire shape of one characterProgress entry, from
    /// MeadowProgression.ProgressionData.CharacterProgressionData.
    /// </summary>
    private sealed class CharacterDto
    {
        [JsonPropertyName("timePlayed")]
        public long TimePlayed { get; set; }

        [JsonPropertyName("emoteUnlockProgress")]
        public int EmoteUnlockProgress { get; set; }

        [JsonPropertyName("skinUnlockProgress")]
        public int SkinUnlockProgress { get; set; }

        [JsonPropertyName("unlockedEmotes")]
        public List<string?>? UnlockedEmotes { get; set; }

        [JsonPropertyName("unlockedSkins")]
        public List<string?>? UnlockedSkins { get; set; }

        [JsonPropertyName("saveLocation")]
        public string? SaveLocation { get; set; }

        [JsonPropertyName("everSeenInMenu")]
        public bool EverSeenInMenu { get; set; }

        [JsonPropertyName("selectedSkin")]
        public string? SelectedSkin { get; set; }

        [JsonPropertyName("tintAmount")]
        public float TintAmount { get; set; }

        [JsonPropertyName("tintColor")]
        public string? TintColor { get; set; }

        [JsonPropertyName("emoteHotbar")]
        public List<string?>? EmoteHotbar { get; set; }
    }
}
