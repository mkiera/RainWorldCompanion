namespace RainWorldSaveManager.Tests;

/// <summary>
/// Compares two parsed models property by property instead of field by named field.
///
/// An edit test wants to say "this one value moved and nothing else did". Listing the other values
/// by hand states the model twice and goes stale the day one is added, and record equality does not
/// answer it either: both <c>CampaignSummary</c> and <c>DeathPersistentData</c> hold lists, which
/// compare by reference, so two identical parses of the same blob are never equal.
/// </summary>
internal static class PropertyComparison
{
    public static void AssertSameExcept<T>(T before, T after, params string[] ignored)
    {
        foreach (var property in typeof(T).GetProperties())
        {
            if (ignored.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var left = property.GetValue(before);
            var right = property.GetValue(after);

            if (left is System.Collections.IEnumerable leftItems and not string
                && right is System.Collections.IEnumerable rightItems and not string)
            {
                Assert.Equal(leftItems.Cast<object>(), rightItems.Cast<object>());
                continue;
            }

            // Naming the property is the difference between "expected False, actual True" and
            // knowing which of forty values moved.
            Assert.True(Equals(left, right), $"{property.Name} changed from '{left}' to '{right}'.");
        }
    }
}
