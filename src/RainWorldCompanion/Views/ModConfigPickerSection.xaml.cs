using System.Windows.Controls;

namespace RainWorldCompanion.Views;

/// <summary>
/// Its DataContext is a <see cref="ViewModels.ModConfigPickerViewModel"/>, and it hides itself when
/// the save carries no mod settings.
/// </summary>
public partial class ModConfigPickerSection : UserControl
{
    public ModConfigPickerSection()
    {
        InitializeComponent();
    }
}
