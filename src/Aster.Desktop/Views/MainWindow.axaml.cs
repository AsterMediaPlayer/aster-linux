using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

/// <summary>
/// Back, wherever the keyboard is (or isn't, after the page it was on went):
/// the mouse's back button, Alt+← and Backspace go back a page.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && GoBack()) e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var back = e.Key == Key.BrowserBack
            || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)
            || (e.Key == Key.Back && e.Source is not TextBox);
        if (back && GoBack()) e.Handled = true;
    }

    private bool GoBack()
    {
        if (DataContext is not MainViewModel { CurrentPage: ShellViewModel { CanGoBack: true } shell }) return false;
        shell.Back();
        return true;
    }
}
