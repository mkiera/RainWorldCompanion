using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// The checksum is what stands between a written save and a save the game refuses to load,
/// so the salt is checked against real payloads rather than against a copy of itself.
/// </summary>
public class SaveChecksumTests
{
    private const string HexPrefix = "^[0-9a-f]{32}$";

    [Fact]
    public void The_salt_is_the_ninety_seven_character_constant_from_the_game()
    {
        Assert.Equal(97, SaveChecksum.Salt.Length);
        Assert.Equal(SyntheticSave.Salt, SaveChecksum.Salt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("SAV STATE NUMBER<svB>White")]
    [InlineData("payload with <progDivA> separators and & ampersands")]
    public void Compute_returns_thirty_two_lowercase_hex_characters(string payload)
    {
        var digest = SaveChecksum.Compute(payload);

        Assert.Equal(32, digest.Length);
        Assert.Matches(HexPrefix, digest);
    }

    [Fact]
    public void Compute_matches_an_independently_calculated_md5_of_payload_plus_salt()
    {
        var payload = SyntheticSave.SavePayload();

        Assert.Equal(SyntheticSave.ComputeChecksum(payload), SaveChecksum.Compute(payload));
    }

    [Fact]
    public void Compute_is_stable_across_calls()
    {
        var payload = SyntheticSave.SavePayload(cycle: 42);

        Assert.Equal(SaveChecksum.Compute(payload), SaveChecksum.Compute(payload));
    }

    [Fact]
    public void Different_payloads_produce_different_digests()
    {
        Assert.NotEqual(SaveChecksum.Compute("CYCLENUM<svB>17"), SaveChecksum.Compute("CYCLENUM<svB>18"));
    }

    [Fact]
    public void Wrap_puts_the_digest_in_front_of_the_untouched_payload()
    {
        var payload = SyntheticSave.SavePayload();

        var wrapped = SaveChecksum.Wrap(payload);

        Assert.Equal(SaveChecksum.Compute(payload), wrapped[..32]);
        Assert.Equal(payload, wrapped[32..]);
    }

    [Fact]
    public void Wrap_then_unwrap_round_trips_with_a_valid_checksum()
    {
        var payload = SyntheticSave.SavePayload(slugcat: "Rivulet", cycle: 3, food: 6, seed: "1234");

        var unwrapped = SaveChecksum.TryUnwrap(SaveChecksum.Wrap(payload), out var recovered, out var checksumValid);

        Assert.True(unwrapped);
        Assert.True(checksumValid);
        Assert.Equal(payload, recovered);
    }

    [Fact]
    public void Flipping_one_character_of_a_payload_makes_the_checksum_invalid()
    {
        var payload = SyntheticSave.SavePayload(cycle: 17);
        var wrapped = SaveChecksum.Wrap(payload);

        // Change a single payload character while leaving the stored digest alone.
        var tamperedIndex = 32 + payload.Length / 2;
        var original = wrapped[tamperedIndex];
        var replacement = original == 'X' ? 'Y' : 'X';
        var tampered = wrapped[..tamperedIndex] + replacement + wrapped[(tamperedIndex + 1)..];

        var unwrapped = SaveChecksum.TryUnwrap(tampered, out var recovered, out var checksumValid);

        Assert.True(unwrapped);
        Assert.False(checksumValid);
        Assert.Equal(tampered[32..], recovered);
    }

    [Fact]
    public void Flipping_one_character_of_the_digest_makes_the_checksum_invalid()
    {
        var payload = SyntheticSave.SavePayload();

        SaveChecksum.TryUnwrap(SyntheticSave.WrapWithBadChecksum(payload), out var recovered, out var checksumValid);

        Assert.False(checksumValid);
        Assert.Equal(payload, recovered);
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("41bc5cf8272fd6f03081ce058ff3f75cpayload follows")]
    public void HasChecksumPrefix_accepts_thirty_two_lowercase_hex_characters(string value)
        => Assert.True(SaveChecksum.HasChecksumPrefix(value));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("41BC5CF8272FD6F03081CE058FF3F75C")]                    // uppercase hex is not the game's form
    [InlineData("41bc5cf8272fd6f03081ce058ff3f75")]                     // thirty one characters
    [InlineData("41bc5cf8272fd6f03081ce058ff3f75z")]                    // z is not hex
    [InlineData("SLOT:0<expC>LEVEL:1<expC>POINTS:0")]                   // a raw expCore payload
    public void HasChecksumPrefix_rejects_anything_else(string value)
        => Assert.False(SaveChecksum.HasChecksumPrefix(value));

    [Fact]
    public void TryUnwrap_treats_the_raw_expCore_value_as_a_payload_rather_than_corruption()
    {
        var raw = FixtureFiles.ReadEntries(FixtureFiles.ExpCore1)["core"];

        var unwrapped = SaveChecksum.TryUnwrap(raw, out var payload, out var checksumValid);

        Assert.False(unwrapped);
        Assert.False(checksumValid);
        Assert.Equal(raw, payload);
        Assert.StartsWith("SLOT:0<expC>", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TryUnwrap_treats_every_raw_options_value_as_a_payload()
    {
        foreach (var entry in FixtureFiles.ReadEntries(FixtureFiles.Options))
        {
            var unwrapped = SaveChecksum.TryUnwrap(entry.Value, out var payload, out var checksumValid);

            Assert.False(unwrapped);
            Assert.False(checksumValid);
            Assert.Equal(entry.Value, payload);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("PLAYERS<svB>1000")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public void TryUnwrap_on_a_value_shorter_than_thirty_two_characters_does_not_throw(string value)
    {
        var unwrapped = SaveChecksum.TryUnwrap(value, out var payload, out var checksumValid);

        Assert.False(unwrapped);
        Assert.False(checksumValid);
        Assert.Equal(value, payload);
    }

    [Fact]
    public void The_short_thepit_Sandbox_value_from_a_real_options_file_unwraps_without_throwing()
    {
        var value = FixtureFiles.ReadEntries(FixtureFiles.Options)["thepit_Sandbox"];
        Assert.True(value.Length < 32, "the fixture value is expected to be shorter than a digest");

        var unwrapped = SaveChecksum.TryUnwrap(value, out var payload, out var checksumValid);

        Assert.False(unwrapped);
        Assert.False(checksumValid);
        Assert.Equal(value, payload);
    }
}
