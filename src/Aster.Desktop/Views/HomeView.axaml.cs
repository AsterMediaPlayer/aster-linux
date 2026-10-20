using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace Aster.Desktop.Views;

/// <summary>
/// Sizes the banner to the window, as the Apple app does, and pages it with
/// the arrow keys while it has the keyboard.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, e) => Hero.Height = e.NewSize.Height;
        Hero.KeyDown += (_, e) =>
        {
            if (DataContext is not HomeViewModel home) return;
            if (e.Key == Key.Right) home.NextHeroCommand.Execute(null);
            else if (e.Key == Key.Left) home.PreviousHeroCommand.Execute(null);
            else return;
            e.Handled = true;
        };
    }
}
