// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;
using RainWorldSaveManager.Core.Saves.Models;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// Pulls the campaign summary out of a save container. Every entry point is fail-soft: a
/// missing, truncated, empty or garbage file comes back as a <see cref="SlotMetadata"/> with
/// <see cref="SlotMetadata.ParseError"/> set rather than an exception, because this runs while
/// listing a directory the user did not curate.
/// </summary>
public static class SaveMetadataExtractor
{
    /// <summary>Hashtable key holding the live progression payload.</summary>
    private const string SaveKey = "save";

    /// <summary>Record header that carries a campaign.</summary>
    private const string SaveStateHeader = "SAVE STATE";

    private const string SlugcatField = "SAV STATE NUMBER";
    private const string CycleField = "CYCLENUM";
    private const string FoodField = "FOOD";
    private const string DenPosField = "DENPOS";
    private const string SeedField = "SEED";
    private const string GlowField = "HASTHEGLOW";
    private const string DevourmentField = "DEVOURMENTSTATE";
    private const string TimelineField = "TIMELINE";
    private const string LastDenPosField = "LASTVDENPOS";
    private const string TotalFoodField = "TOTFOOD";
    private const string TotalTimeField = "TOTTIME";
    private const string CurrentVersionCyclesField = "CURRVERCYCLES";
    private const string RoboField = "HASROBO";
    private const string JustBeatGameField = "JUSTBEATGAME";

    /// <summary>
    /// The SAVE STATE half of the extra cycles flag. DEATHPERSISTENTSAVEDATA carries one under the
    /// same name, and SaveState.RedExtraCycles is true when either is set.
    /// </summary>
    private const string RedExtraCyclesField = "REDEXTRACYCLES";

    private const string DeathPersistentField = "DEATHPERSISTENTSAVEDATA";
    private const string KillsField = "KILLS";
    private const string SwallowedItemsField = "SWALLOWEDITEMS";
    private const string HeldItemsField = "UNRECOGNIZEDPLAYERGRASPS";

    /// <summary>The game's list of creatures it keeps with the player between cycles.</summary>
    private const string FriendsField = "FRIENDS";

    /// <summary>Separates the entries of the KILLS value.</summary>
    private const string KillSeparator = "<svC>";

    /// <summary>Separates a creature id from its count inside one KILLS entry.</summary>
    private const string KillCountSeparator = "<svD>";

    private const int MaxErrorLength = 200;

    /// <summary>
    /// Never throws. On failure returns metadata with ParseError set. The realm is taken from the
    /// file name, so a file read out of the save folder under its real name lands on the right
    /// side without the caller saying so.
    /// </summary>
    public static SlotMetadata Extract(string filePath, int slotNumber)
        => Extract(filePath, slotNumber, SaveSlotRef.RealmForFileName(SafeFileName(filePath)));

    /// <summary>
    /// Never throws. Same as <see cref="Extract(string, int)"/> with the realm stated outright,
    /// for a file whose name does not say which set it came from.
    /// </summary>
    public static SlotMetadata Extract(string filePath, int slotNumber, SaveRealm realm)
    {
        string fileName = SafeFileName(filePath);

        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return Failed(slotNumber, fileName, realm, "no file path");
            }

            if (!File.Exists(filePath))
            {
                return Failed(slotNumber, fileName, realm, "file not found");
            }

            if (!SaveContainer.TryRead(filePath, out SaveContainer? container, out string? error)
                || container is null)
            {
                return Failed(slotNumber, fileName, realm, Shorten(Localise(error, filePath, fileName)) ?? "unreadable save container");
            }

            // A hashtable whose Keys and Values disagree has lost entries. Reporting that as a
            // parse error is the only thing that separates a damaged slot from an unused one.
            if (container.StructureProblem is { } structureProblem)
            {
                return Failed(slotNumber, fileName, realm, Shorten(structureProblem) ?? "the save file is damaged");
            }

            // No "save" key is a real state, not a failure: exp1 has no keys at all and
            // expCore1 only has "core".
            if (!container.Entries.TryGetValue(SaveKey, out string? rawValue))
            {
                return new SlotMetadata
                {
                    Slot = slotNumber,
                    FileName = fileName,
                    Realm = realm,
                    ChecksumValid = null,
                    Campaigns = Array.Empty<CampaignSummary>(),
                    RecordCount = 0,
                    ParseError = null,
                };
            }

            // The return value says whether a digest was there at all. A value with no digest is
            // a raw payload, which is how the format stores several keys, and reporting it as a
            // failed checksum tells the player their save is corrupt when the game reads it fine.
            bool hasDigest = SaveChecksum.TryUnwrap(rawValue, out string payload, out bool checksumValid);

            // An empty payload is an untouched slot, which is what online_sav3 is on a fresh
            // install: the value is the digest and nothing after it. That reads as no campaigns
            // and no parse error, so Describe says "empty" rather than reporting a failure.
            return Walk(payload, fileName, slotNumber, realm, hasDigest ? checksumValid : null);
        }
        catch (Exception ex)
        {
            return Failed(slotNumber, fileName, realm, Shorten(ex.Message) ?? ex.GetType().Name);
        }
    }

    /// <summary>
    /// The same read as <see cref="Extract(string, int)"/>, over a payload already in hand rather
    /// than a file on disk.
    ///
    /// A campaign stored on its own is a payload with no container around it, so there is no digest
    /// to check and <see cref="SlotMetadata.ChecksumValid"/> comes back null, which is the state
    /// this format already uses for a value that carries none.
    /// </summary>
    public static SlotMetadata FromPayload(string? payload, string fileName, int slotNumber, SaveRealm realm)
    {
        try
        {
            return Walk(payload ?? "", fileName, slotNumber, realm, checksumValid: null);
        }
        catch (Exception ex)
        {
            return Failed(slotNumber, fileName, realm, Shorten(ex.Message) ?? ex.GetType().Name);
        }
    }

    private static SlotMetadata Walk(
        string payload,
        string fileName,
        int slotNumber,
        SaveRealm realm,
        bool? checksumValid)
    {
        // Every record is counted, not only the campaigns. A Rain Meadow online_sav commonly holds
        // MAP, MAPUPDATE and MISCPROG records and no SAVE STATE at all, and without the total the
        // app has no way to tell that file from a slot that has never been played.
        var campaigns = new List<CampaignSummary>();
        int records = 0;

        foreach (RecordSpan record in SavePayloadReader.EnumerateRecords(payload))
        {
            records++;

            // Compare the header before touching the body. MAP records run to hundreds of
            // kilobytes and copying one out to look at its header is wasted work.
            if (!record.HeaderIs(SaveStateHeader))
            {
                continue;
            }

            campaigns.Add(BuildCampaign(record.Body()));
        }

        return new SlotMetadata
        {
            Slot = slotNumber,
            FileName = fileName,
            Realm = realm,
            ChecksumValid = checksumValid,
            Campaigns = campaigns,
            RecordCount = records,
            ParseError = null,
        };
    }

    /// <summary>
    /// "sav" to 1, "sav2" to 2, "sav3" to 3, and the Rain Meadow "online_sav", "online_sav2",
    /// "online_sav3" to the same 1, 2, 3. Anything else is null.
    /// </summary>
    public static int? SlotNumberForFileName(string fileName) => SlotForFileName(fileName)?.Slot;

    /// <summary>
    /// The slot a container file name stands for, realm and number together. Null when the name is
    /// not one of the six.
    ///
    /// Both realms are numbered by the same Options.saveSlot. Options.GetSaveFileName_SavOrExp
    /// returns "sav" for saveSlot 0 and "sav" + (saveSlot + 1) above it, and Rain Meadow's hook
    /// RainMeadow.RainMeadow.Options_GetSaveFileName_SavOrExp returns "online_sav" and
    /// "online_sav" + (saveSlot + 1) from the same field once a lobby is joined. So online_sav2
    /// is the online half of the same UI slot 2 that sav2 is the local half of.
    /// </summary>
    public static SaveSlotRef? SlotForFileName(string fileName) => SaveSlotRef.ForFileName(fileName);

    private static CampaignSummary BuildCampaign(string body)
    {
        string slugcat = CampaignSummary.UnknownSlugcat;
        int? cycle = null;
        int? food = null;
        string? denPos = null;
        string? seed = null;
        int devourmentCount = 0;
        bool hasGlow = false;
        bool hasRobo = false;
        bool justBeatGame = false;
        bool redExtraCycles = false;
        string? timeline = null;
        string? lastDenPos = null;
        int? totalFood = null;
        int? currentVersionCycles = null;
        TimeSpan? playTime = null;
        DeathPersistentData death = DeathPersistentData.Empty;
        List<KillRecord>? kills = null;
        List<DevourmentRelationship>? devourmentStates = null;
        List<string>? swallowedItems = null;
        List<string>? heldItems = null;
        List<string>? friendIds = null;

        // REGIONSTATE and COMMUNITIES are skipped by omission. REGIONSTATE alone appears about a
        // hundred times per campaign and each value runs to kilobytes, and nothing here reads them.
        foreach (KeyValuePair<string, string?> field in SavePayloadReader.SplitFields(body))
        {
            switch (field.Key)
            {
                case SlugcatField:
                    if (!string.IsNullOrEmpty(field.Value))
                    {
                        slugcat = field.Value;
                    }

                    break;

                case CycleField:
                    if (int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCycle))
                    {
                        cycle = parsedCycle;
                    }

                    break;

                case FoodField:
                    if (int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFood))
                    {
                        food = parsedFood;
                    }

                    break;

                case DenPosField:
                    denPos = field.Value;
                    break;

                case SeedField:
                    seed = field.Value;
                    break;

                case GlowField:
                    hasGlow = true;
                    break;

                case DevourmentField:
                    // The count is of fields, not of parsed relationships. A field a newer mod
                    // version writes in a shape this app does not know still happened, and the
                    // count is what the UI reports as "carried".
                    devourmentCount++;
                    if (DevourmentReader.TryRead(field.Value, out DevourmentRelationship? relationship)
                        && relationship is not null)
                    {
                        (devourmentStates ??= new List<DevourmentRelationship>()).Add(relationship);
                    }

                    break;

                case RoboField:
                    hasRobo = true;
                    break;

                case JustBeatGameField:
                    justBeatGame = true;
                    break;

                case RedExtraCyclesField:
                    redExtraCycles = true;
                    break;

                case TimelineField:
                    timeline = field.Value;
                    break;

                case LastDenPosField:
                    lastDenPos = field.Value;
                    break;

                case TotalFoodField:
                    if (int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTotalFood))
                    {
                        totalFood = parsedTotalFood;
                    }

                    break;

                case CurrentVersionCyclesField:
                    if (int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVersionCycles))
                    {
                        currentVersionCycles = parsedVersionCycles;
                    }

                    break;

                case TotalTimeField:
                    if (int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeconds)
                        && parsedSeconds >= 0)
                    {
                        playTime = TimeSpan.FromSeconds(parsedSeconds);
                    }

                    break;

                case DeathPersistentField:
                    death = DeathPersistentReader.Read(field.Value);
                    break;

                case KillsField:
                    AppendKills(field.Value, ref kills);
                    break;

                case SwallowedItemsField:
                    // One field per item, holding the game's own serialized item.
                    if (DevourmentReader.ItemTypeOf(field.Value) is { } swallowed)
                    {
                        (swallowedItems ??= new List<string>()).Add(swallowed);
                    }

                    break;

                case HeldItemsField:
                    AppendHeldItems(field.Value, ref heldItems);
                    break;

                case FriendsField:
                    foreach (string friendId in EntityBlobReader.ReadFriendIds(field.Value))
                    {
                        (friendIds ??= new List<string>()).Add(friendId);
                    }

                    break;
            }
        }

        return new CampaignSummary
        {
            SlugcatId = slugcat,
            CycleNum = cycle,
            Food = food,
            DenPos = denPos,
            Seed = seed,
            DevourmentStateCount = devourmentCount,
            HasGlow = hasGlow,
            Karma = death.Karma,
            KarmaCap = death.KarmaCap,
            ReinforcedKarma = death.ReinforcedKarma,
            HasTheMark = death.HasTheMark,
            Ascended = death.Ascended,
            HasRobo = hasRobo,
            JustBeatGame = justBeatGame,
            RedsDeathStored = death.RedsDeathStored,
            RedExtraCycles = redExtraCycles || death.RedExtraCycles,
            Deaths = death.Deaths,
            Survives = death.Survives,
            Quits = death.Quits,
            TotalFoodEaten = totalFood,
            PlayTime = playTime,
            CyclesThisVersion = currentVersionCycles,
            Timeline = timeline,
            LastDenPos = lastDenPos,
            Echoes = death.Echoes,
            UnlockedGates = death.UnlockedGates,
            Passages = death.Passages,
            Kills = kills is null ? Array.Empty<KillRecord>() : kills,
            DevourmentStates = devourmentStates is null
                ? Array.Empty<DevourmentRelationship>()
                : devourmentStates,
            SwallowedItems = swallowedItems is null ? Array.Empty<string>() : swallowedItems,
            HeldItems = heldItems is null ? Array.Empty<string>() : heldItems,
            FriendIds = friendIds is null ? Array.Empty<string>() : friendIds,
        };
    }

    /// <summary>
    /// Reads one KILLS value: entries separated by &lt;svC&gt;, each "CreatureId&lt;svD&gt;Count".
    /// An entry with no count, or a count that will not parse, is dropped.
    /// </summary>
    private static void AppendKills(string? value, ref List<KillRecord>? kills)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (string entry in value.Split(KillSeparator, StringSplitOptions.None))
        {
            int split = entry.IndexOf(KillCountSeparator, StringComparison.Ordinal);
            if (split <= 0)
            {
                continue;
            }

            string creatureId = entry.Substring(0, split).Trim();
            if (creatureId.Length == 0)
            {
                continue;
            }

            string countText = entry.Substring(split + KillCountSeparator.Length);
            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                continue;
            }

            // Ids carry the game's template bookkeeping after the name, as in "Fly-Creature-0".
            int hyphen = creatureId.IndexOf('-');
            string displayName = hyphen > 0 ? creatureId.Substring(0, hyphen) : creatureId;

            (kills ??= new List<KillRecord>()).Add(new KillRecord(creatureId, displayName, count));
        }
    }

    /// <summary>
    /// Reads UNRECOGNIZEDPLAYERGRASPS, which lists one item name per hand separated by
    /// &lt;svB&gt;. The first &lt;svB&gt; was already consumed as the key boundary.
    /// </summary>
    private static void AppendHeldItems(string? value, ref List<string>? heldItems)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (string entry in value.Split(SavePayloadReader.ValueSeparator, StringSplitOptions.None))
        {
            string item = entry.Trim();
            if (item.Length != 0)
            {
                (heldItems ??= new List<string>()).Add(item);
            }
        }
    }

    private static SlotMetadata Failed(int slotNumber, string fileName, SaveRealm realm, string reason) =>
        new()
        {
            Slot = slotNumber,
            FileName = fileName,
            Realm = realm,
            ChecksumValid = null,
            Campaigns = Array.Empty<CampaignSummary>(),
            ParseError = reason,
        };

    private static string SafeFileName(string filePath)
    {
        try
        {
            return string.IsNullOrEmpty(filePath) ? "" : (Path.GetFileName(filePath) ?? "");
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    /// <summary>
    /// Swaps the full path in a container message for the bare file name. The exception names
    /// the path because a caller may only have the message, but a list row has no space for it.
    /// </summary>
    private static string? Localise(string? message, string filePath, string fileName)
    {
        if (message is null || fileName.Length == 0 || filePath.Length == 0)
        {
            return message;
        }

        return message.Replace(filePath, fileName, StringComparison.OrdinalIgnoreCase);
    }

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
}
