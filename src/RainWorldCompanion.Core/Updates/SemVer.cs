namespace RainWorldCompanion.Core.Updates;

/// <summary>
/// The full Semantic Versioning 2.0.0 precedence rules, not a numeric-only approximation:
/// collapsing every pre-release to one "is this stable" flag would make 1.2.0-rc.1 and 1.2.0-rc.2
/// compare equal. Build metadata is ignored by every comparison, which matters because the .NET
/// SDK appends the commit sha to InformationalVersion.
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

    public bool IsPreRelease => PreRelease.Length != 0;

    /// <summary>
    /// Tolerates one leading "v". Strict about the three-part core: "1.2" cannot be placed against
    /// "1.2.0" without guessing, so it is a skip rather than a throw.
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
    /// The record's generated comparison would include build metadata and disagree with
    /// <see cref="CompareTo"/>, leaving a version neither newer, older, nor the same as itself.
    /// </summary>
    public bool Equals(SemVer other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;

    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

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
    /// Numeric identifiers compare as numbers and rank below alphanumeric ones, the clause that
    /// puts beta.2 below beta.11 where a plain string comparison puts it above.
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
            // Both are digits with no leading zero, so the longer string is the larger number.
            // Parsing would cap the width at whatever integer type was chosen, and the spec does
            // not cap it.
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
    /// No leading zero: the spec forbids one, and allowing it would make 1.01.0 and 1.1.0 two
    /// spellings of the same version.
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
    /// A numeric pre-release identifier may not carry a leading zero, because it is compared as a
    /// number, while build metadata is never compared and so may hold anything.
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
