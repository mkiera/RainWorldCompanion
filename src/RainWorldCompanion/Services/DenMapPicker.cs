using System.Windows;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.Services;

public interface IDenMapPicker
{
    DenMapAvailability GetAvailability(string slugcatId);
    string? Pick(string currentRoomId, string fieldName);
}

public sealed class DenMapPicker(
    Func<string?> installPath,
    Func<string?> saveRoot,
    Func<Window?> owner) : IDenMapPicker
{
    public DenMapAvailability GetAvailability(string slugcatId) => DenMapAvailability.Check(
        slugcatId, ExpansionDetector.Detect(installPath()), OptionsFile.Read(saveRoot()));

    public string? Pick(string currentRoomId, string fieldName)
    {
        var dialog = new DenMapDialog(currentRoomId, fieldName) { Owner = owner() };
        return dialog.ShowDialog() == true ? dialog.SelectedRoomId : null;
    }
}
