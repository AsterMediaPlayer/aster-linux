using System;
using System.ComponentModel;
using System.Linq;
using Aster.Desktop.Controls;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// Sizes the banner to the window, as the Apple app does, and keeps the
/// keyboard on the card it was on when the shelves are remade: last
/// time's Home, shown at once, is replaced by the server's when it comes.
/// </summary>
public partial class HomeView : UserControl
{
    private HomeViewModel? model;

    /// <summary>The card the keyboard was on as the shelves were remade: its shelf, its title and its place in the shelf.</summary>
    private (string Shelf, string ItemId, int Index)? onCard;

    public HomeView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, e) => Hero.Height = e.NewSize.Height;
        DataContextChanged += (_, _) =>
        {
            if (model is not null)
            {
                model.PropertyChanging -= ShelvesChanging;
                model.PropertyChanged -= ShelvesChanged;
            }
            model = DataContext as HomeViewModel;
            if (model is not null)
            {
                model.PropertyChanging += ShelvesChanging;
                model.PropertyChanged += ShelvesChanged;
            }
        };
    }

    private void ShelvesChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName != nameof(HomeViewModel.Shelves)) return;
        onCard = null;
        if (KeyFocus.Current(this) is not Visual focused || !KeyFocus.IsWithin(focused, ShelfList)) return;
        var card = focused.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ItemCard>().FirstOrDefault();
        var shelf = focused.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<Shelf>().FirstOrDefault();
        if (card is null || shelf is null) return;
        onCard = (shelf.Title, card.Item.Id, shelf.Cards.ToList().IndexOf(card));
    }

    private void ShelvesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeViewModel.Shelves) || onCard is not { } was) return;
        onCard = null;
        Dispatcher.UIThread.Post(() =>
        {
            // The same title in the same shelf, or what's now in its place there.
            var row = model?.Shelves.FirstOrDefault(s => s.Title == was.Shelf);
            if (row is null || row.Cards.Count == 0) return;
            var card = row.Cards.FirstOrDefault(c => c.Item.Id == was.ItemId) ?? row.Cards[Math.Clamp(was.Index, 0, row.Cards.Count - 1)];
            var target = ShelfList.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(i => i is Control { DataContext: var context } && ReferenceEquals(context, card) && KeyFocus.CanFocus(i));
            if (target is not null && !ReferenceEquals(KeyFocus.Current(this), target)) KeyFocus.Move(target);
        }, DispatcherPriority.Loaded);
    }
}
