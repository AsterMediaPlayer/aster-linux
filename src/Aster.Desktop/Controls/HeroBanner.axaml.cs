using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace Aster.Desktop.Controls;

/// <summary>A featured banner; the arrow keys page it while it has the keyboard.</summary>
public partial class HeroBanner : UserControl
{
    public HeroBanner()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not HeroModel hero) return;
        if (e.Key == Key.Right) hero.NextCommand.Execute(null);
        else if (e.Key == Key.Left) hero.PreviousCommand.Execute(null);
        else return;
        e.Handled = true;
    }
}
