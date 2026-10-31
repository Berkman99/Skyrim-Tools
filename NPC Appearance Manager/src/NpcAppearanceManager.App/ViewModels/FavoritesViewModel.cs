using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcAppearanceManager.App.Views;
using NpcAppearanceManager.Core.Scanning;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.App.ViewModels;

/// <summary>One saved face: an NPC's face (its tile, for the picture) and its name.</summary>
public sealed class FavoriteViewModel(NpcRowViewModel row, string face, LookTileViewModel tile)
{
    public NpcRowViewModel Row { get; } = row;

    /// <summary>The face's pick key ("" the original, a mod's name, or "ff:id").</summary>
    public string Face { get; } = face;

    public LookTileViewModel Tile { get; } = tile;
    public string Name => Row.DisplayName;

    /// <summary>Where the face comes from: a mod, "Skyrim Special Edition", or a FaceFinder mod you don't have.</summary>
    public string Source => Tile.IsOnline ? $"{Tile.ModName} (not installed)" : Tile.ModName;
}

/// <summary>What a click on a favorite does for the NPC you have selected.</summary>
public enum FavoriteClick
{
    /// <summary>No NPC selected, or it already uses that face.</summary>
    Nothing,

    /// <summary>The favorite is the selected NPC's own face (from another mod): picked for it in Pick faces.</summary>
    Pick,

    /// <summary>Another NPC's face: you're asked whether to copy it onto the selected NPC.</summary>
    AskToCopy,
}

/// <summary>
/// Faces you saved (right-click a face, "Add to favorites"), per profile, newest first, in a panel shared by Pick faces
/// and Copy faces. A click gives the face to the NPC selected there.
/// </summary>
public partial class FavoritesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly NpcChooserViewModel _chooser;
    private CancellationTokenSource? _pictures;

    public ObservableCollection<FavoriteViewModel> Items { get; } = [];

    public FavoritesViewModel(MainViewModel main, NpcChooserViewModel chooser)
    {
        _main = main;
        _chooser = chooser;
        Reload();
    }

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The favorites as saved, with their pictures (again after pictures were looked up anew).</summary>
    public void Reload()
    {
        _pictures?.Cancel();
        Items.Clear();
        var byKey = _chooser.Rows.ToDictionary(r => r.Entry.FormKey.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var saved in _main.Selections.Favorites)
            if (byKey.TryGetValue(saved.From, out var row) && Make(row, _chooser.InstalledFaceOrSame(row, PickKeys.Load(saved.Face, row.Entry.Looks))) is { } favorite)
                Items.Add(favorite);
        LoadPictures();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private FavoriteViewModel? Make(NpcRowViewModel row, string face) =>
        _chooser.FaceTile(row, face) is { IsUnavailable: false } tile ? new FavoriteViewModel(row, face, tile) : null;

    private void LoadPictures()
    {
        _pictures = new CancellationTokenSource();
        _chooser.LoadTiles(Items.Select(f => f.Tile).ToList(), _pictures.Token);
    }

    public bool Contains(NpcRowViewModel row, string face) => Items.Any(f => f.Row == row && f.Face == face);

    /// <summary>Right-click on a face: adds it (first in the list), or removes it when it's a favorite already.</summary>
    public void Toggle(LookTileViewModel tile)
    {
        if (tile.Row is not { } row || tile.IsUnavailable) return;
        if (Items.FirstOrDefault(f => f.Row == row && f.Face == tile.Key) is { } existing) Remove(existing);
        else if (Make(row, tile.Key) is { } favorite)
        {
            Items.Insert(0, favorite);
            _pictures ??= new CancellationTokenSource();
            _chooser.LoadTiles([favorite.Tile], _pictures.Token);
            Save();
        }
    }

    [RelayCommand]
    private void Remove(FavoriteViewModel favorite)
    {
        Items.Remove(favorite);
        Save();
    }

    private void Save()
    {
        _main.Selections.Favorites = Items.Select(f => new FaceCopy { From = f.Row.Entry.FormKey.ToString(), Face = PickKeys.Save(f.Face, f.Row.Entry.Looks) }).ToList();
        _main.SaveSettings();
        _chooser.RefreshFavoriteMenus();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>What clicking a favorite does with <paramref name="selected"/> selected (see <see cref="FavoriteClick"/>).</summary>
    public static FavoriteClick Decide(NpcRowViewModel? selected, NpcRowViewModel favorite, string face)
    {
        if (selected == null) return FavoriteClick.Nothing;
        var (usesRow, usesFace) = selected.Copy is { } c ? (c.From, c.Face) : (selected, selected.ChosenMod);
        if (usesRow == favorite && usesFace == face) return FavoriteClick.Nothing;
        return favorite == selected ? FavoriteClick.Pick : FavoriteClick.AskToCopy;
    }

    [RelayCommand]
    private void Open(FavoriteViewModel favorite)
    {
        var selected = _main.CurrentStep == Page.Copy ? _main.CopyFaces?.Target : _chooser.SelectedNpc;
        switch (Decide(selected, favorite.Row, favorite.Face))
        {
            case FavoriteClick.Pick:
                _main.ShowPage(Page.Npcs);
                _chooser.PickFace(selected!, favorite.Face);
                _main.IsFavoritesOpen = false;
                break;

            case FavoriteClick.AskToCopy:
                if (!favorite.Tile.IsOnline && favorite.Row.CopyableLook(favorite.Face) == null)
                {
                    MessageBox.Show(Application.Current.MainWindow!, "In the game this NPC gets a random face each time it appears, so there's no single face to copy.", AppSettings.AppName);
                    return;
                }
                var ask = new CopyFavoriteWindow(favorite, selected!) { Owner = Application.Current.MainWindow };
                if (ask.ShowDialog() != true) return;
                _chooser.SetCopy(selected!, (favorite.Row, favorite.Face));
                _main.ShowPage(Page.Copy);
                _main.CopyFaces?.ShowCopy(selected!);
                _main.IsFavoritesOpen = false;
                break;
        }
    }
}
