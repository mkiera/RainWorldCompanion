namespace RainWorldSaveManager.Core.Updates;

/// <summary>
/// A Semantic Versioning 2.0.0 version, with the spec's precedence rules.
///
/// Release tags are the authority for what a build is, and they are semver, so ordering them is
/// what decides whether a release is an update, a downgrade, or the copy already running. The
/// full rule is implemented rather than a numeric-only approximation because the approximation
/// has a specific failure: collapsing every pre-release to a single "is this stable" flag makes
/// 1.2.0-rc.1 and 1.2.0-rc.2 compare equal, so nobody on rc.1 is ever offered rc.2.
///
/// Build metadata (everything after "+") is parsed and then ignored by every comparison, which
/// is what the spec requires. It matters here because the .NET SDK appends the commit sha to
/// InformationalVersion, so the running version arrives carrying one.
/// </summary>
public readonly record struct SemVer : IComparable<SemVer>
{
    private SemVer(int major, int minor, int patch, string preRelease, string buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The text between "-" and "+", or empty when the version is a release.</summary>
    public string PreRelease { get; }

    /// <summary>The text after "+", or empty. Takes no part in ordering or equality.</summary>
    public string BuildMetadata { get; }

    /// <summary>True for 1.0.0-beta.1, false for 1.0.0.</summary>
    public bool IsPreRelease => PreRelease.Length != 0;

    /// <summary>
    /// Reads a version, tolerating one leading "v" so a caller holding a git tag gets the same
    /// answer as one holding the number out of it.
    ///
    /// Returns false rather than throwing, because the caller is walking a list of tags that
    /// anyone can create and an unorderable one has to be a skip. Strict about the three-part
    /// core: "1.2" cannot be placed against "1.2.0" without guessing, and guessing about which
    /// build someone is offered is worse than passing over the tag.
    /// </summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.StartsWith('v'))
        {
            span = span[1..];
        }

        var build = "";
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            build = span[(plus + 1)..];
            span = span[..plus];
            if (!IsDotSeparatedIdentifiers(build, numericIdentifiersAreStrict: false))
            {
                return false;
            }
        }

        var preRelease = "";
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = span[(dash + 1)..];
            span = span[..dash];
            if (!IsDotSeparatedIdentifiers(preRelease, numericIdentifiersAreStrict: true))
            {
                return false;
            }
        }

        var parts = span.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch, preRelease, build);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Equality follows precedence, so two versions differing only in build metadata are equal.
    /// The record's generated comparison would include the metadata and disagree with
    /// <see cref="CompareTo"/>, which is how a version ends up neither newer, older, nor the
    /// same as itself.
    /// </summary>
    public bool Equals(SemVer other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    /// <summary>The version as it would be written, without a leading "v".</summary>
    public override string ToString()
    {
        var text = Major + "." + Minor + "." + Patch;
        if (PreRelease.Length != 0)
        {
            text += "-" + PreRelease;
        }

        if (BuildMetadata.Length != 0)
        {
            text += "+" + BuildMetadata;
        }

        return text;
    }

    /// <summary>
    /// The spec's rule, in order: a release outranks any pre-release of the same core, then the
    /// dot-separated identifiers are compared left to right, then a longer run of identifiers
    /// wins. Numeric identifiers compare as numbers and rank below alphanumeric ones, which is
    /// the clause that puts beta.2 below beta.11 where a plain string comparison puts it above.
    /// </summary>
    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var shared = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < shared; i++)
        {
            var order = CompareIdentifier(leftParts[i], rightParts[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumber = IsNumeric(left);
        var rightIsNumber = IsNumeric(right);

        if (leftIsNumber && rightIsNumber)
        {
            // Both are digits with no leading zero, so the longer string is the larger number and
            // equal lengths order the same way the characters do. Parsing would cap the width at
            // whatever integer type was chosen; the spec puts no cap on it.
            return left.Length != right.Length
                ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
        }

        if (leftIsNumber != rightIsNumber)
        {
            return leftIsNumber ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    /// <summary>
    /// A version core number: digits only, and no leading zero, because the spec forbids one and
    /// allowing it would make 1.01.0 and 1.1.0 two spellings of the same version. int.TryParse
    /// also rejects anything wider than an int, which is the right answer for a tag holding a
    /// number nobody meant.
    /// </summary>
    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return IsNumeric(text) && int.TryParse(text, out value);
    }

    private static bool IsNumeric(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return text.Length == 1 || text[0] != '0';
    }

    /// <summary>
    /// Validates a pre-release or build-metadata tail. Both are dot-separated runs of
    /// [0-9A-Za-z-] with no empty identifier. They differ in one clause: a numeric pre-release
    /// identifier may not carry a leading zero, because it is compared as a number, while build
    /// metadata is never compared and so may hold anything.
    /// </summary>
    private static bool IsDotSeparatedIdentifiers(string text, bool numericIdentifiersAreStrict)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var identifier in text.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            var allDigits = true;
            foreach (var c in identifier)
            {
                if (c is >= '0' and <= '9')
                {
                    continue;
                }

                if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-')
                {
                    allDigits = false;
                    continue;
                }

                return false;
            }

            if (numericIdentifiersAreStrict && allDigits && !IsNumeric(identifier))
            {
                return false;
            }
        }

        return true;
    }
}
