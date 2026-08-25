using System.Reflection;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// The version the app reports is what the updater compares against every release tag, so it has
/// to be readable as one. These pin the two ends of that: the string the tests hand to view models,
/// and the attribute a real build carries.
/// </summary>
public class BuildVersionTests
{
    [Fact]
    public void The_version_the_panels_are_built_with_is_a_version()
    {
        // Panels.AppVersion used to be decoration. It stands in for the running version now, so a
        // value the picker could not order would make every panel test a build the updater cannot
        // reason about.
        Assert.True(SemVer.TryParse(Panels.AppVersion, out var version));
        Assert.True(version.IsPreRelease);
    }

    /// <summary>
    /// The app assembly must carry an informational version, because that is the only version
    /// attribute wide enough for a "-beta.1" tail. Setting AssemblyVersion or FileVersion by hand
    /// in Directory.Build.props suppresses the SDK's inference and is how a build ends up
    /// reporting a version its tag never had.
    /// </summary>
    [Fact]
    public void The_app_assembly_carries_a_version_that_can_be_ordered()
    {
        var informational = typeof(ViewModels.MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(informational));

        // SourceLink appends "+<commit>" in a git checkout. The app splits it off before parsing,
        // so the same split is what this asserts against.
        var plus = informational!.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;

        Assert.True(SemVer.TryParse(version, out _), informational + " should hold a semver");
    }
}
