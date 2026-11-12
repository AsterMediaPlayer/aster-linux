using System.ComponentModel;
using System.Linq;
using Aster.Desktop.Controls;
using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// The form's keys: the name box ready for typing as it opens; a PIN's pad
/// takes the focus as it opens, and gives it back to its row as it shuts;
/// Esc or a controller's B shuts an open pad, else leaves the form (not while saving).
/// </summary>
public partial class ProfileCreateView : UserControl
{
    private ProfileCreateViewModel? form;
    private string? lastPin;

    public ProfileCreateView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Dispatcher.UIThread.Post(() => NameBox.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (form is not null) form.PropertyChanged -= Changed;
        form = DataContext as ProfileCreateViewModel;
        if (form is not null) form.PropertyChanged += Changed;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileCreateViewModel.EditingPin) || form is null) return;
        var opened = form.EditingPin;
        var shut = lastPin;
        lastPin = opened;
        Dispatcher.UIThread.Post(() =>
        {
            // Opened: onto its 1. Shut: back to its row.
            if (opened is not null && Find<PinPad>(opened) is { } pad) KeyFocus.Move(pad.First);
            else if (shut is not null && Find<Button>(shut) is { } row) KeyFocus.Move(row);
        }, DispatcherPriority.Loaded);
    }

    private T? Find<T>(string field) where T : Control =>
        this.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Tag as string == field && (c is not Button button || button.Classes.Contains("pinRow")));

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.BrowserBack) || form is null) return;
        if (form.EditingPin is not null) form.ClosePinCommand.Execute(null);
        else if (form.CancelCommand.CanExecute(null)) form.CancelCommand.Execute(null);
        e.Handled = true;
    }
}
