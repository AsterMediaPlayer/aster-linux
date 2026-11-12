using Aster.Desktop.Controls;
using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

public partial class ServerPasswordView : UserControl
{
    public ServerPasswordView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>Ready for typing as it shows: the password box, or the PIN pad's 1.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ServerPasswordViewModel { IsPin: true }) KeyFocus.Move(Pad.First);
        else PasswordBox.Focus();
    }

    /// <summary>A controller's B: Cancel (or Use Another Server), as Esc is.</summary>
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.BrowserBack || DataContext is not ServerPasswordViewModel prompt) return;
        prompt.OtherCommand.Execute(null);
        e.Handled = true;
    }
}
