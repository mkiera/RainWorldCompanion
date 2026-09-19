using System.Text;
using System.Text.Json;
using RainWorldCompanion.Core.LogStreaming;
using RainWorldCompanion.Core.LogStreaming.Analysis;
using RainWorldCompanion.Tests;

namespace RainWorldCompanion.Core.Tests;

public sealed class LogCaptureAnalysisTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Raw_chunks_ending_mid_line_reparse_only_the_tail_and_preserve_complete_errors()
    {
        using var files = new TempDirectory("capture-partial-raw-tail");
        WriteCapture(files, incomplete: true);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string relative = Path.Combine(alice, "generation-001/BepInEx/LogOutput.log");
        string prefix = string.Concat(Enumerable.Range(0, 20000)
            .Select(index => $"[Info : Rain Meadow] Entity {index} moved normally.\n"));
        files.WriteText(relative, "[Error : Rain Meadow] Earlier distinct failure\n" + prefix
            + "[Error : Rain Meadow] Missing entity with");
        var session = new LogCaptureAnalysisSession(files.Path);
        await session.RefreshAsync();

        File.AppendAllText(files.Resolve(relative), "out state\n[Warning : Rain Meadow] Separate warning\n");
        var snapshot = await session.RefreshAsync();

        Assert.InRange(session.LastRefreshParsedBytes, 1, 70 * 1024);
        Assert.Contains(snapshot.Moments, moment => moment.Details.Contains("Earlier distinct failure", StringComparison.Ordinal));
        Assert.Single(snapshot.Moments, moment => moment.Details.Contains("Missing entity without state", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Moments, moment => moment.Details.EndsWith("Missing entity with", StringComparison.Ordinal));
        Assert.Contains(snapshot.Moments, moment => moment.Details.Contains("Separate warning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_structured_record_resumes_without_reparsing_prior_records_or_duplicating_events()
    {
        using var files = new TempDirectory("capture-partial-structured-tail");
        WriteCapture(files, incomplete: true);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string relative = Path.Combine(alice, "generation-001/Companion/events.jsonl");
        string earlier = string.Concat(Enumerable.Range(0, 1000).Select(index =>
            Diagnostic("marker", Start.AddSeconds(index), new { label = "Previous " + index }) + "\n"));
        string next = Diagnostic("marker", Start.AddSeconds(1001), new { label = "Final marker" });
        int split = next.Length / 2;
        files.WriteText(relative, earlier + next[..split]);
        var session = new LogCaptureAnalysisSession(files.Path);
        var initial = await session.RefreshAsync();

        File.AppendAllText(files.Resolve(relative), next[split..] + "\n");
        var completed = await session.RefreshAsync();

        Assert.InRange(session.LastRefreshParsedBytes, 1, 2048);
        Assert.Equal(initial.Moments.Count + 1, completed.Moments.Count);
        Assert.Single(completed.Moments, moment => moment.Timestamp == Start.AddSeconds(1001));
        File.AppendAllText(files.Resolve(relative), Diagnostic("marker", Start.AddSeconds(1002), new { label = "Following marker" }) + "\n");
        var following = await session.RefreshAsync();
        Assert.Equal(initial.Moments.Count + 2, following.Moments.Count);
    }

    [Fact]
    public async Task Capture_writer_output_loads_without_schema_translation()
    {
        using var files = new TempDirectory("capture-analysis-writer");
        var writer = new LogStreamCaptureWriter(
            files.CreateSubdirectory("captures"),
            "capture-real",
            new LogStreamCaptureOptions
            {
                ReservedFreeSpaceBytes = 0,
                AvailableFreeSpace = _ => long.MaxValue,
                TimeProvider = new FixedClock(Start),
            });
        const ulong steamId = 76561198000000001;
        var sender = new LogStreamPeerIdentity(steamId, "Alice", true);
        string diagnostic = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            timestampUtc = Start,
            kind = "session-start",
            sessionId = "source-real",
            gameplayId = "gameplay-real",
            details = new
            {
                appVersion = "1.4.0-beta.3",
                gameVersion = "v1.11.8",
                gameHookVersion = "1.0.12",
                campaign = "White",
                timeline = "White",
                state = "gameplay",
                process = "SU",
                isOnline = true,
                isHost = true,
                enabledExpansions = new[] { "moreslugcats" },
                activeMods = new[]
                {
                    new
                    {
                        id = "henpemaz_rainmeadow",
                        displayName = "Rain Meadow",
                        version = "0.4.2",
                        codeFingerprint = new string('a', 64),
                        fingerprintStatus = "complete",
                    },
                },
            },
        }) + "\n";
        var chunk = new LogStreamChunk(
            "capture-real",
            "source-real",
            steamId,
            1,
            "Companion/events.jsonl",
            1,
            0,
            Encoding.UTF8.GetBytes(diagnostic));

        LogStreamWriteResult written = writer.Write(sender, chunk);
        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(writer.CaptureDirectory).RefreshAsync();

        Assert.Equal(LogStreamWriteStatus.Written, written.Status);
        CaptureParticipant participant = Assert.Single(snapshot.Participants);
        Assert.Equal("Alice", participant.DisplayName);
        Assert.True(participant.HasSharedLogs);
        CaptureTimelineMoment sessionStart = Assert.Single(snapshot.Moments, moment => moment.Kind == "session-start");
        Assert.Equal(CaptureTimingConfidence.Aligned, sessionStart.TimingConfidence);
        CaptureModSnapshot mods = Assert.Single(snapshot.ModSnapshots);
        CaptureModEntry meadow = Assert.Single(mods.Mods);
        Assert.Equal("Rain Meadow", meadow.DisplayName);
        Assert.Equal(new string('a', 64), meadow.CodeFingerprint);
        Assert.Contains(snapshot.MapContexts, context => context.Timeline == "White" && context.DownpourEnabled);
    }

    [Fact]
    public async Task Multi_sender_errors_are_aligned_by_receiver_arrival_and_rank_named_mods()
    {
        using var files = new TempDirectory("capture-analysis");
        WriteCapture(files, incomplete: false);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string bob = WriteSession(files, "Bob", "2", false, "bob-session");
        string aliceEvent = Diagnostic("session-start", Start.AddMinutes(-3), new
        {
            appVersion = "1.4.0-beta.3", gameVersion = "v1.11.8", gameHookVersion = "1.0.12",
            campaign = "White", timeline = "White", state = "gameplay", process = "SU",
            isOnline = true, isHost = true, enabledExpansions = new[] { "moreslugcats" },
            activeMods = new[] { new { id = "henpemaz_rainmeadow", displayName = "Rain Meadow", version = "0.4.2", codeFingerprint = "aaa", fingerprintStatus = "complete" } }
        });
        string bobEvent = Diagnostic("session-start", Start.AddMinutes(4), new
        {
            appVersion = "1.4.0-beta.3", gameVersion = "v1.11.8", gameHookVersion = "1.0.12",
            campaign = "White", timeline = "White", state = "gameplay", process = "SU",
            isOnline = true, isHost = false, enabledExpansions = new[] { "moreslugcats" },
            activeMods = new[] { new { id = "henpemaz_rainmeadow", displayName = "Rain Meadow", version = "0.4.2", codeFingerprint = "bbb", fingerprintStatus = "complete" } }
        });
        files.WriteText(Path.Combine(alice, "generation-001/Companion/events.jsonl"), aliceEvent + "\n");
        files.WriteText(Path.Combine(bob, "generation-001/Companion/events.jsonl"), bobEvent + "\n");
        const string priorLog = "[Info : BepInEx] capture started\n";
        const string error = "[Error : Unity Log] RainMeadow.WorldSessionException: exploded\n  at RainMeadow.WorldSession.Update()";
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), priorLog + error + "\n");
        files.WriteText(Path.Combine(bob, "generation-001/BepInEx/LogOutput.log"), priorLog + error + "\n");
        int errorOffset = Encoding.UTF8.GetByteCount(priorLog);
        WriteJournal(files,
            Chunk("1", "alice-session", "Companion/events.jsonl", 0, Encoding.UTF8.GetByteCount(aliceEvent + "\n"), Start),
            Chunk("2", "bob-session", "Companion/events.jsonl", 0, Encoding.UTF8.GetByteCount(bobEvent + "\n"), Start),
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", errorOffset, Encoding.UTF8.GetByteCount(error + "\n"), Start.AddSeconds(10)),
            Chunk("2", "bob-session", "BepInEx/LogOutput.log", errorOffset, Encoding.UTF8.GetByteCount(error + "\n"), Start.AddSeconds(10.4)));

        var snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        CaptureIncident incident = Assert.Single(snapshot.Incidents, item => item.IsCrossPlayer);
        Assert.Equal(2, incident.ParticipantIds.Count);
        Assert.InRange((incident.Ended - incident.Started).TotalSeconds, 0, 1);
        CaptureCauseCandidate cause = Assert.Single(incident.PossibleCauses, item => item.ModId == "henpemaz_rainmeadow");
        Assert.True(cause.Likelihood >= 0.75);
        Assert.Contains(cause.Evidence, item => item.Contains("names this mod", StringComparison.Ordinal));
        Assert.Contains(cause.Evidence, item => item.Contains("fingerprints", StringComparison.Ordinal));
        Assert.Contains(snapshot.MapContexts, context => context.Timeline == "White" && context.DownpourEnabled);
    }

    [Fact]
    public async Task Native_only_players_are_observed_without_being_reported_as_error_free()
    {
        using var files = new TempDirectory("capture-analysis-native");
        WriteCapture(files, incomplete: true);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string native = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            timestampUtc = Start,
            kind = "peer-present",
            sessionId = "alice-session",
            source = "rain-meadow-native",
            details = new { subjectId = "peer-hidden", displayName = "Charlie", isHost = false }
        });
        files.WriteText(Path.Combine(alice, "generation-001/Companion/meadow-native.jsonl"), native + "\n");
        WriteJournal(files, Chunk("1", "alice-session", "Companion/meadow-native.jsonl", 0,
            Encoding.UTF8.GetByteCount(native + "\n"), Start.AddMilliseconds(20)));

        var snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        CaptureParticipant charlie = Assert.Single(snapshot.Participants, item => item.DisplayName == "Charlie");
        Assert.False(charlie.HasSharedLogs);
        Assert.True(charlie.HasNativeObservations);
        Assert.Contains("Logs unavailable", charlie.CoverageText, StringComparison.Ordinal);
        Assert.True(snapshot.IsIncomplete);
    }

    [Fact]
    public async Task Participant_lane_keeps_the_role_from_the_start_of_the_capture()
    {
        using var files = new TempDirectory("capture-analysis-role-change");
        var writer = new LogStreamCaptureWriter(
            files.CreateSubdirectory("captures"),
            "capture-role-change",
            new LogStreamCaptureOptions
            {
                ReservedFreeSpaceBytes = 0,
                AvailableFreeSpace = _ => long.MaxValue,
                TimeProvider = new FixedClock(Start),
            });
        var originalHost = new LogStreamPeerIdentity(76561198000000001, "Alice", true);
        var originalClient = new LogStreamPeerIdentity(76561198000000002, "Bob", false);
        Assert.Equal(LogStreamWriteStatus.Written, writer.Write(originalHost, new(
            "capture-role-change", "host-session", originalHost.SteamId, 1,
            "consoleLog.txt", 1, 0, "host\n"u8)).Status);
        Assert.Equal(LogStreamWriteStatus.Written, writer.Write(originalClient, new(
            "capture-role-change", "client-session", originalClient.SteamId, 1,
            "consoleLog.txt", 1, 0, "client\n"u8)).Status);
        writer.ObservePeer(originalClient with { IsHost = true });

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(writer.CaptureDirectory).RefreshAsync();

        Assert.True(snapshot.Participants.Single(item => item.DisplayName == "Alice").IsHost);
        Assert.False(snapshot.Participants.Single(item => item.DisplayName == "Bob").IsHost);
    }

    [Fact]
    public async Task Partial_fingerprint_differences_do_not_be_used_as_exact_cause_evidence()
    {
        using var files = new TempDirectory("capture-analysis-partial-fingerprints");
        WriteCapture(files, incomplete: false);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string bob = WriteSession(files, "Bob", "2", false, "bob-session");
        string aliceEvent = Diagnostic("session-start", Start, new
        {
            activeMods = new[] { new { id = "henpemaz_rainmeadow", displayName = "Rain Meadow", version = "0.4.2", codeFingerprint = "aaa", fingerprintStatus = "partial" } }
        });
        string bobEvent = Diagnostic("session-start", Start, new
        {
            activeMods = new[] { new { id = "henpemaz_rainmeadow", displayName = "Rain Meadow", version = "0.4.2", codeFingerprint = "bbb", fingerprintStatus = "partial" } }
        });
        files.WriteText(Path.Combine(alice, "generation-001/Companion/events.jsonl"), aliceEvent + "\n");
        files.WriteText(Path.Combine(bob, "generation-001/Companion/events.jsonl"), bobEvent + "\n");
        const string error = "[Error : Unity Log] Generic lobby failure";
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), error + "\n");
        files.WriteText(Path.Combine(bob, "generation-001/BepInEx/LogOutput.log"), error + "\n");
        WriteJournal(files,
            Chunk("1", "alice-session", "Companion/events.jsonl", 0, Encoding.UTF8.GetByteCount(aliceEvent + "\n"), Start),
            Chunk("2", "bob-session", "Companion/events.jsonl", 0, Encoding.UTF8.GetByteCount(bobEvent + "\n"), Start),
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", 0, Encoding.UTF8.GetByteCount(error + "\n"), Start.AddSeconds(10)),
            Chunk("2", "bob-session", "BepInEx/LogOutput.log", 0, Encoding.UTF8.GetByteCount(error + "\n"), Start.AddSeconds(10.2)));

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        Assert.NotEmpty(snapshot.Incidents);
        Assert.All(snapshot.Incidents, incident =>
            Assert.DoesNotContain(incident.PossibleCauses, cause => cause.ModId == "henpemaz_rainmeadow"));
    }

    [Fact]
    public async Task Bundled_package_fingerprints_are_not_used_as_possible_cause_evidence()
    {
        using var files = new TempDirectory("capture-analysis-bundled-fingerprints");
        WriteCapture(files, incomplete: false);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string bob = WriteSession(files, "Bob", "2", false, "bob-session");
        string aliceEvent = Diagnostic("session-start", Start, new
        {
            activeMods = new[]
            {
                new { id = "rwremix", displayName = "Rain World Remix", version = "1.9", codeFingerprint = new string('a', 64), fingerprintStatus = "complete" },
                new { id = "rainmeadow", displayName = "Rain Meadow", version = "0.4.2", codeFingerprint = new string('a', 64), fingerprintStatus = "complete" },
            }
        });
        string bobEvent = Diagnostic("session-start", Start, new
        {
            activeMods = new[]
            {
                new { id = "rwremix", displayName = "Rain World Remix", version = "1.9", codeFingerprint = new string('b', 64), fingerprintStatus = "complete" },
                new { id = "rainmeadow", displayName = "Rain Meadow", version = "0.4.2", codeFingerprint = new string('b', 64), fingerprintStatus = "complete" },
            }
        });
        files.WriteText(Path.Combine(alice, "generation-001/Companion/events.jsonl"), aliceEvent + "\n");
        files.WriteText(Path.Combine(bob, "generation-001/Companion/events.jsonl"), bobEvent + "\n");
        const string error = "[Error : Unity Log] Generic lobby failure\n";
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), error);
        files.WriteText(Path.Combine(bob, "generation-001/BepInEx/LogOutput.log"), error);
        WriteJournal(files,
            Chunk("1", "alice-session", "Companion/events.jsonl", 0, Encoding.UTF8.GetByteCount(aliceEvent + "\n"), Start),
            Chunk("2", "bob-session", "Companion/events.jsonl", 0, Encoding.UTF8.GetByteCount(bobEvent + "\n"), Start),
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", 0, Encoding.UTF8.GetByteCount(error), Start.AddSeconds(10)),
            Chunk("2", "bob-session", "BepInEx/LogOutput.log", 0, Encoding.UTF8.GetByteCount(error), Start.AddSeconds(10.2)));

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        Assert.Equal(2, snapshot.Incidents.Count);
        Assert.All(snapshot.Incidents, incident =>
        {
            Assert.DoesNotContain(incident.PossibleCauses, cause => cause.ModId == "rwremix");
            CaptureCauseCandidate thirdParty = Assert.Single(incident.PossibleCauses,
                cause => cause.ModId == "rainmeadow");
            Assert.Contains(thirdParty.Evidence,
                evidence => evidence.Contains("fingerprints", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Missing_mod_inventory_is_not_treated_as_an_unaffected_player_without_the_mod()
    {
        using var files = new TempDirectory("capture-analysis-missing-inventory");
        WriteCapture(files, incomplete: false);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        _ = WriteSession(files, "Bob", "2", false, "bob-session");
        string aliceEvent = Diagnostic("session-start", Start, new
        {
            activeMods = new[]
            {
                new
                {
                    id = "henpemaz_rainmeadow",
                    displayName = "Rain Meadow",
                    version = "0.4.2",
                    codeFingerprint = new string('a', 64),
                    fingerprintStatus = "complete",
                },
            },
        });
        const string error = "[Error : Unity Log] RainMeadow.WorldSessionException: exploded\n";
        files.WriteText(Path.Combine(alice, "generation-001/Companion/events.jsonl"), aliceEvent + "\n");
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), error);
        WriteJournal(files,
            Chunk("1", "alice-session", "Companion/events.jsonl", 0,
                Encoding.UTF8.GetByteCount(aliceEvent + "\n"), Start),
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", 0,
                Encoding.UTF8.GetByteCount(error), Start.AddSeconds(10)));

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        CaptureCauseCandidate cause = Assert.Single(Assert.Single(snapshot.Incidents).PossibleCauses,
            item => item.ModId == "henpemaz_rainmeadow");
        Assert.Equal(0.6, cause.Likelihood);
        Assert.DoesNotContain(cause.Evidence,
            item => item.Contains("unaffected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Continuous_errors_do_not_chain_into_one_long_simultaneous_incident()
    {
        using var files = new TempDirectory("capture-analysis-incident-window");
        WriteCapture(files, incomplete: false);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string[] errors =
        [
            "[Error : Unity Log] FirstException: one\n",
            "[Error : Unity Log] SecondException: two\n",
            "[Error : Unity Log] ThirdException: three\n",
        ];
        string combined = string.Concat(errors);
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), combined);
        int firstOffset = 0;
        int secondOffset = Encoding.UTF8.GetByteCount(errors[0]);
        int thirdOffset = secondOffset + Encoding.UTF8.GetByteCount(errors[1]);
        WriteJournal(files,
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", firstOffset,
                Encoding.UTF8.GetByteCount(errors[0]), Start),
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", secondOffset,
                Encoding.UTF8.GetByteCount(errors[1]), Start.AddSeconds(1.5)),
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", thirdOffset,
                Encoding.UTF8.GetByteCount(errors[2]), Start.AddSeconds(3)));

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        Assert.Equal(2, snapshot.Incidents.Count);
        Assert.All(snapshot.Incidents, incident =>
            Assert.InRange((incident.Ended - incident.Started).TotalSeconds, 0, 2));
    }

    [Fact]
    public async Task Continuous_matching_error_storm_is_combined_until_the_stream_goes_quiet()
    {
        using var files = new TempDirectory("capture-analysis-error-storm");
        WriteCapture(files, incomplete: false);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        var lines = Enumerable.Range(0, 8)
            .Select(index => $"[Error : Rain Meadow] Entity not found: #{index}:apo:0002\n")
            .Append("[Error : Other Mod] DifferentException: unrelated\n")
            .ToArray();
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), string.Concat(lines));
        long offset = 0;
        var chunks = new List<object>();
        for (int index = 0; index < lines.Length; index++)
        {
            int bytes = Encoding.UTF8.GetByteCount(lines[index]);
            chunks.Add(Chunk("1", "alice-session", "BepInEx/LogOutput.log", offset, bytes,
                index < 8 ? Start.AddSeconds(index * 0.75) : Start.AddSeconds(3)));
            offset += bytes;
        }
        WriteJournal(files, chunks.ToArray());

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        CaptureTimelineMoment storm = Assert.Single(snapshot.Moments,
            moment => moment.Details.Contains("Entity not found", StringComparison.Ordinal));
        Assert.Equal(8, storm.DuplicateCount);
        Assert.Equal(Start.AddSeconds(5.25), storm.LastOccurrence);
        Assert.Single(snapshot.Moments,
            moment => moment.Details.Contains("DifferentException", StringComparison.Ordinal));
        CaptureIncident incident = Assert.Single(snapshot.Incidents,
            item => item.MomentSequences.Contains(storm.Sequence));
        Assert.Equal(Start.AddSeconds(5.25), incident.Ended);
        Assert.Contains("8 related errors", incident.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_discovers_appended_live_events_without_duplicates()
    {
        using var files = new TempDirectory("capture-analysis-live");
        WriteCapture(files, incomplete: true);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string path = files.ResolveWithParent(Path.Combine(alice, "generation-001/Companion/events.jsonl"));
        string first = Diagnostic("player-present", Start, new
        {
            playerId = "alice", playerName = "Alice", isLocal = true, isHost = true,
            roomId = "SU_A01", region = "SU", dead = false
        });
        File.WriteAllText(path, first + "\n");
        WriteJournal(files, Chunk("1", "alice-session", "Companion/events.jsonl", 0,
            Encoding.UTF8.GetByteCount(first + "\n"), Start.AddMilliseconds(10)));
        var session = new LogCaptureAnalysisSession(files.Path);
        CaptureAnalysisSnapshot before = await session.RefreshAsync();
        string second = Diagnostic("room-changed", Start.AddSeconds(2), new
        {
            playerId = "alice", playerName = "Alice", isLocal = true, isHost = true,
            roomId = "SU_A07", region = "SU", dead = false,
            details = new { previousRoom = "SU_A01", currentRoom = "SU_A07", previousRegion = "SU", currentRegion = "SU" }
        });
        File.AppendAllText(path, second + "\n");
        WriteJournal(files,
            Chunk("1", "alice-session", "Companion/events.jsonl", 0,
                Encoding.UTF8.GetByteCount(first + "\n"), Start.AddMilliseconds(10)),
            Chunk("1", "alice-session", "Companion/events.jsonl", Encoding.UTF8.GetByteCount(first + "\n"),
                Encoding.UTF8.GetByteCount(second + "\n"), Start.AddSeconds(2.01)));

        CaptureAnalysisSnapshot after = await session.RefreshAsync();
        CaptureAnalysisSnapshot unchanged = await session.RefreshAsync();

        Assert.Single(before.Moments, item => item.Kind == "player-present");
        Assert.Single(after.Moments, item => item.Kind == "room-changed");
        Assert.Single(unchanged.Moments, item => item.Kind == "room-changed");
        Assert.Equal("SU_A07", after.PlayerObservations.Last().RoomId);
    }

    [Fact]
    public async Task Initial_log_backfill_is_not_mistaken_for_a_simultaneous_cross_player_failure()
    {
        using var files = new TempDirectory("capture-analysis-backfill");
        WriteCapture(files, incomplete: true);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string bob = WriteSession(files, "Bob", "2", false, "bob-session");
        const string aliceError = "[Error : Old Mod] HistoricalException: Alice failed hours ago\n";
        const string bobError = "[Error : Other Mod] HistoricalException: Bob failed yesterday\n";
        files.WriteText(Path.Combine(alice, "generation-001/BepInEx/LogOutput.log"), aliceError);
        files.WriteText(Path.Combine(bob, "generation-001/BepInEx/LogOutput.log"), bobError);
        WriteJournal(files,
            Chunk("1", "alice-session", "BepInEx/LogOutput.log", 0, Encoding.UTF8.GetByteCount(aliceError), Start),
            Chunk("2", "bob-session", "BepInEx/LogOutput.log", 0, Encoding.UTF8.GetByteCount(bobError), Start.AddMilliseconds(100)));

        CaptureAnalysisSnapshot snapshot = await new LogCaptureAnalysisSession(files.Path).RefreshAsync();

        Assert.Equal(2, snapshot.Moments.Count(moment => moment.Severity >= CaptureEventSeverity.Error));
        Assert.Equal(2, snapshot.Incidents.Count);
        Assert.DoesNotContain(snapshot.Incidents, incident => incident.IsCrossPlayer);
        Assert.All(snapshot.Moments.Where(moment => moment.Severity >= CaptureEventSeverity.Error),
            moment => Assert.Equal(CaptureTimingConfidence.Estimated, moment.TimingConfidence));
    }

    [Fact]
    public async Task Incremental_campaign_change_keeps_the_session_expansion_map_variant()
    {
        using var files = new TempDirectory("capture-analysis-map-context");
        WriteCapture(files, incomplete: true);
        string alice = WriteSession(files, "Alice", "1", true, "alice-session");
        string path = files.ResolveWithParent(Path.Combine(alice, "generation-001/Companion/events.jsonl"));
        string sessionStart = Diagnostic("session-start", Start, new
        {
            campaign = "White", timeline = "White", enabledExpansions = new[] { "moreslugcats" },
            activeMods = Array.Empty<object>()
        });
        File.WriteAllText(path, sessionStart + "\n");
        WriteJournal(files, Chunk("1", "alice-session", "Companion/events.jsonl", 0,
            Encoding.UTF8.GetByteCount(sessionStart + "\n"), Start.AddMilliseconds(10)));
        var session = new LogCaptureAnalysisSession(files.Path);
        _ = await session.RefreshAsync();

        string changed = Diagnostic("campaign-changed", Start.AddSeconds(2), new
        {
            previousCampaign = "White", currentCampaign = "Red",
            previousTimeline = "White", currentTimeline = "Red"
        });
        int offset = Encoding.UTF8.GetByteCount(sessionStart + "\n");
        File.AppendAllText(path, changed + "\n");
        WriteJournal(files,
            Chunk("1", "alice-session", "Companion/events.jsonl", 0, offset, Start.AddMilliseconds(10)),
            Chunk("1", "alice-session", "Companion/events.jsonl", offset,
                Encoding.UTF8.GetByteCount(changed + "\n"), Start.AddSeconds(2.01)));

        CaptureAnalysisSnapshot snapshot = await session.RefreshAsync();

        CaptureMapContext context = snapshot.MapContexts.Last();
        Assert.Equal("Red", context.Timeline);
        Assert.True(context.DownpourKnown);
        Assert.True(context.DownpourEnabled);
    }

    private static void WriteCapture(TempDirectory files, bool incomplete)
        => files.WriteText("capture.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            captureId = "capture-test",
            createdUtc = Start,
            endedUtc = incomplete ? (DateTimeOffset?)null : Start.AddMinutes(10),
            incomplete,
            hasGaps = false,
            bytesWritten = 1234
        }));

    private static string WriteSession(
        TempDirectory files,
        string name,
        string id,
        bool host,
        string sessionId)
    {
        string relative = $"{name} [{(host ? "Host" : "Client")}] {id}/session-001 2026-09-14 06-00-00";
        files.WriteText(Path.Combine(relative, "session.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            captureId = "capture-test",
            senderSteamId = id,
            senderSteamName = name,
            initialRole = host ? "Host" : "Client",
            currentRole = host ? "Host" : "Client",
            sourceSessionId = sessionId,
            startedUtc = Start,
            updatedUtc = Start.AddMinutes(10),
            completedUtc = Start.AddMinutes(10),
            isIncomplete = false,
            hasGaps = false
        }));
        return relative;
    }

    private static string Diagnostic(string kind, DateTimeOffset timestamp, object details)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            timestampUtc = timestamp,
            kind,
            sessionId = "source-session",
            gameplayId = "gameplay",
            details
        });

    private static object Chunk(
        string peerId,
        string sessionId,
        string fileId,
        long offset,
        int bytes,
        DateTimeOffset timestamp) => new
        {
            timestamp,
            kind = "chunkWritten",
            captureId = "capture-test",
            message = "A validated chunk was flushed to disk.",
            peerId,
            fileId,
            generation = 1,
            offset,
            byteCount = bytes,
            sourceSessionId = sessionId
        };

    private static void WriteJournal(TempDirectory files, params object[] entries)
        => files.WriteText("events.jsonl", string.Join("\n", entries.Select(entry => JsonSerializer.Serialize(entry))) + "\n");

}
