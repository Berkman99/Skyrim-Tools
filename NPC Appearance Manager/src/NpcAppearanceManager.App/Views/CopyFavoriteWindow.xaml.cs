using System.Windows;
using NpcAppearanceManager.App.ViewModels;

namespace NpcAppearanceManager.App.Views;

/// <summary>A click on a favorite: "Copy this face to (NPC)?", with the face, whose it is and where it comes from.</summary>
public partial class CopyFavoriteWindow : Window
{
    public CopyFavoriteWindow(FavoriteViewModel favorite, NpcRowViewModel target)
    {
        InitializeComponent();
        Question.Text = $"Copy this face to {target.DisplayName}?";
        Face.Source = favorite.Tile.Image;
        FaceName.Text = favorite.Name;
        FaceSource.Text = "from " + favorite.Source;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private void Yes(object sender, RoutedEventArgs e) => DialogResult = true;
}
