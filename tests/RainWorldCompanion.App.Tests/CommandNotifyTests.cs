using System.IO;
using System.Text.RegularExpressions;

namespace RainWorldCompanion.App.Tests;

/// <summary>
/// A generated command evaluates its guard once and then only when something raises
/// CanExecuteChanged. A guard that reads IsBusy on a command missing from the field's
/// NotifyCanExecuteChangedFor list is stuck on the answer it gave before the first load, which is
/// a button that never enables. Nothing throws and no other test sees it.
/// </summary>
public class CommandNotifyTests
{
    private static readonly string SourceRoot =
        Path.Combine(AppContext.BaseDirectory, "ViewModelSource");

    private static readonly Regex GuardedCommand = new(
        @"\[RelayCommand\([^\]]*CanExecute\s*=\s*nameof\((?<guard>\w+)\)[^\]]*\)\]"
        + @"(?:\s*\[[^\]]*\])*"
        + @"\s*private\s+(?:static\s+)?(?:async\s+)?[\w<>?\[\],\s]+?\s+(?<method>\w+)\s*\(",
        RegexOptions.Singleline);

    private static readonly Regex BusyField = new(
        @"(?<attributes>(?:\s*\[[^\]]*\])+)\s*private\s+bool\s+isBusy\s*;");

    private static readonly Regex Notified = new(
        @"NotifyCanExecuteChangedFor\(nameof\((?<command>\w+)\)\)");

    [Fact]
    public void Every_command_whose_guard_reads_IsBusy_is_told_when_it_changes()
    {
        var files = Directory.EnumerateFiles(SourceRoot, "*.cs").ToList();
        Assert.NotEmpty(files);

        var unwired = new List<string>();

        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            Match busy = BusyField.Match(source);
            if (!busy.Success)
            {
                continue;
            }

            var told = Notified.Matches(busy.Groups["attributes"].Value)
                .Select(match => match.Groups["command"].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match command in GuardedCommand.Matches(source))
            {
                string guard = command.Groups["guard"].Value;
                if (!ReadsBusy(source, guard))
                {
                    continue;
                }

                string name = CommandName(command.Groups["method"].Value);
                if (!told.Contains(name))
                {
                    unwired.Add($"{Path.GetFileName(file)}: {guard} reads IsBusy but {name} is not "
                        + "in the NotifyCanExecuteChangedFor list on isBusy");
                }
            }
        }

        Assert.True(unwired.Count == 0, string.Join("\n", unwired.Order()));
    }

    // Every guard in these files is expression bodied, so the body is what sits between the
    // signature and the first semicolon.
    private static bool ReadsBusy(string source, string guard)
    {
        Match declaration = Regex.Match(source, @"private\s+bool\s+" + Regex.Escape(guard) + @"\s*\([^)]*\)\s*=>");
        if (!declaration.Success)
        {
            return false;
        }

        int start = declaration.Index + declaration.Length;
        int end = source.IndexOf(';', start);
        return end > start && source[start..end].Contains("IsBusy", StringComparison.Ordinal);
    }

    private static string CommandName(string method) =>
        (method.EndsWith("Async", StringComparison.Ordinal) ? method[..^"Async".Length] : method) + "Command";
}
