using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace Aster.Desktop.Views;

public partial class TrackingLinkView : UserControl
{
    public TrackingLinkView() => InitializeComponent();

    /// <summary>Gone (Back, Esc): it stops asking the server whether it's done.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as TrackingLinkViewModel)?.Leave();
        base.OnDetachedFromVisualTree(e);
    }
}
