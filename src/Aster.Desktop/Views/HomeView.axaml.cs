using Avalonia.Controls;

namespace Aster.Desktop.Views;

/// <summary>
/// Sizes the banner to the window, as the Apple app does.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, e) => Hero.Height = e.NewSize.Height;
    }
}
