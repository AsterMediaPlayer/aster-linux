using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aster.Desktop.Views;

/// <summary>The form's keys: the name box ready for typing as it opens; Esc or a controller's B leaves it (not while saving).</summary>
public partial class ProfileCreateView : UserControl
{
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

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.BrowserBack) || DataContext is not ProfileCreateViewModel form) return;
        if (form.CancelCommand.CanExecute(null)) form.CancelCommand.Execute(null);
        e.Handled = true;
    }
}
