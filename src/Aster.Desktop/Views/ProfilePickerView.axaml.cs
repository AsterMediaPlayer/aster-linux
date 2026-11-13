using System;
using System.ComponentModel;
using System.Linq;
using Aster.Desktop.Controls;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// The picker's keys and focus: the first tile has it as the picker opens (the
/// controller picks at once), the PIN pad while one is asked (again after a
/// wrong one); Esc or a controller's B leaves the PIN first, then the picker.
/// </summary>
public partial class ProfilePickerView : UserControl
{
    private ProfilePickerViewModel? model;

    public ProfilePickerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Dispatcher.UIThread.Post(FocusFirstTile, DispatcherPriority.Background);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (model is not null)
        {
            model.PropertyChanged -= Changed;
            model.PinRefused -= FocusPin;
        }
        model = DataContext as ProfilePickerViewModel;
        if (model is not null)
        {
            model.PropertyChanged += Changed;
            model.PinRefused += FocusPin;
        }
    }

    /// <summary>The first tile, once the tiles are there, unless something of the picker's has the focus already.</summary>
    private void FocusFirstTile()
    {
        if (model is not { HasTiles: true, IsPicking: true }) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused) && focused != this) return;
        if (TileRow.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled) is not { } first) return;
        KeyFocus.Move(first);
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilePickerViewModel.IsEnteringPin))
        {
            if (model?.IsEnteringPin == true) FocusPin();
            // Back from the PIN: the tiles again (the pad that had the focus is hidden).
            else Dispatcher.UIThread.Post(() => { if (TileRow.GetVisualDescendants().OfType<Button>().FirstOrDefault() is { } first) KeyFocus.Move(first); }, DispatcherPriority.Background);
        }
        // The tiles came (or changed): onto the first, once they're laid out.
        else if (e.PropertyName == nameof(ProfilePickerViewModel.Tiles)) Dispatcher.UIThread.Post(FocusFirstTile, DispatcherPriority.Background);
    }

    /// <summary>To the pad's 1 once it's on screen.</summary>
    private void FocusPin() => Dispatcher.UIThread.Post(() => KeyFocus.Move(Pad.First), DispatcherPriority.Loaded);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.BrowserBack)
        {
            model?.GoBack();
            e.Handled = true;
        }
    }
}
