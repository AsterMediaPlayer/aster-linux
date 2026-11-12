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
/// controller picks at once), the PIN box while one is asked (again after a
/// wrong one); Esc or a controller's B leaves the PIN first, then the picker.
/// </summary>
public partial class ProfilePickerView : UserControl
{
    private ProfilePickerViewModel? model;

    public ProfilePickerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        TileRow.LayoutUpdated += (_, _) => FocusFirstTile();
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

    private bool focusedTile;

    /// <summary>The first tile, once the tiles are there and nothing else has the focus.</summary>
    private void FocusFirstTile()
    {
        if (focusedTile || model is not { HasTiles: true, IsPicking: true }) return;
        if (TileRow.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled) is not { } first) return;
        focusedTile = true;
        KeyFocus.Move(first);
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfilePickerViewModel.IsEnteringPin))
        {
            if (model?.IsEnteringPin == true) FocusPin();
            else
            {
                focusedTile = false;
                Dispatcher.UIThread.Post(FocusFirstTile, DispatcherPriority.Loaded);
            }
        }
    }

    /// <summary>To the PIN box once it's on screen.</summary>
    private void FocusPin() => Dispatcher.UIThread.Post(() => PinBox.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.BrowserBack)
        {
            model?.GoBack();
            e.Handled = true;
        }
    }
}
