using System.Windows;
using NpcAppearanceManager.Core.Environment;

namespace NpcAppearanceManager.App.Views;

/// <summary>"Select Mod Manager", asked once on first start. Closing it keeps the one NAM guessed.</summary>
public partial class ManagerPromptWindow : Window
{
    public ModManager Selected { get; private set; }

    public ManagerPromptWindow(ModManager guess)
    {
        InitializeComponent();
        Selected = guess;
        (guess switch { ModManager.Mo2 => Mo2, ModManager.Vortex => VortexChoice, _ => NoManager }).IsChecked = true;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        Selected = Mo2.IsChecked == true ? ModManager.Mo2 : VortexChoice.IsChecked == true ? ModManager.Vortex : ModManager.None;
        DialogResult = true;
    }
}
