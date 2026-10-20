using Avalonia.Controls;

namespace Aster.Desktop.Views;

/// <summary>
/// The header takes most of the window's first screen, its details along the
/// bottom over the backdrop, as on the Apple app's Mac pages (72% of the window).
/// </summary>
public partial class DetailView : UserControl
{
    public DetailView()
    {
        InitializeComponent();
        Page.SizeChanged += (_, e) => HeaderHost.MinHeight = e.NewSize.Height * 0.72;
    }
}
