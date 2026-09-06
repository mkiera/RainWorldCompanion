using System.Windows;
using RainWorldCompanion.Core.Mods;
using RainWorldCompanion.Core.Saves;
using RainWorldCompanion.Core.System;
using RainWorldCompanion.Views;

namespace RainWorldCompanion.Services;

public sealed record DenMapSelection(string RoomId, string Timeline);

public interface IDenMapPicker
{
    DenMapAvailability GetAvailability(string slugcatId);
    DenWorldCatalog LoadWorld();
    DenMapSelection? Pick(string currentRoomId, string fieldName, string timeline, DenWorldCatalog world);
}

public sealed class DenMapPicker(
    Func<string?> installPath,
    Func<string?> saveRoot,
    Func<Window?> owner) : IDenMapPicker
{
    public DenMapAvailability GetAvailability(string slugcatId) => DenMapAvailability.Check(
        slugcatId, ExpansionDetector.Detect(installPath()), OptionsFile.Read(saveRoot()));

    public DenWorldCatalog LoadWorld() => DenWorldCatalog.Load(installPath());

    public DenMapSelection? Pick(string currentRoomId, string fieldName, string timeline, DenWorldCatalog world)
    {
        var dialog = new DenMapDialog(currentRoomId, fieldName, timeline, world) { Owner = owner() };
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }
}
