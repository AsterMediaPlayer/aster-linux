using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Aster.Desktop.Controls;

/// <summary>
/// Keyboard and remote focus: what a page focuses first, and where the
/// keyboard is. A control marked <c>c:Focus.IsPreferred="True"</c> (a
/// banner's Play, a title's Play or Resume, the search box) is where a page
/// starts; without one, its first focusable control.
/// </summary>
public static class KeyFocus
{
    public static readonly AttachedProperty<bool> IsPreferredProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsPreferred", typeof(KeyFocus));

    public static bool GetIsPreferred(Control control) => control.GetValue(IsPreferredProperty);
    public static void SetIsPreferred(Control control, bool value) => control.SetValue(IsPreferredProperty, value);

    /// <summary>
    /// A page (the guide) that starts at its preferred control each time it
    /// opens, rather than where the keyboard was when it was left: the guide
    /// moves on with the clock, so last visit's programme is stale.
    /// </summary>
    public static readonly AttachedProperty<bool> StartsFreshProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("StartsFresh", typeof(KeyFocus));

    public static bool GetStartsFresh(Control control) => control.GetValue(StartsFreshProperty);
    public static void SetStartsFresh(Control control, bool value) => control.SetValue(StartsFreshProperty, value);

    /// <summary>Whether something in <paramref name="root"/> asks to start fresh.</summary>
    public static bool StartsFresh(Visual root) => root.GetVisualDescendants().OfType<Control>().Any(GetStartsFresh);

    /// <summary>The control a page starts on: one marked preferred, else the first that can take focus.</summary>
    public static InputElement? FirstIn(Visual root) => PreferredIn(root) ?? Focusable(root).FirstOrDefault();

    /// <summary>The first control marked preferred, once it's there (a page's rows can come in after it shows).</summary>
    public static InputElement? PreferredIn(Visual root) => Focusable(root).FirstOrDefault(c => c is Control control && GetIsPreferred(control));

    private static IEnumerable<InputElement> Focusable(Visual root) => root.GetVisualDescendants().OfType<InputElement>().Where(CanFocus);

    /// <summary>Can take focus now: focusable, shown, enabled, and not a scroll area (which the arrows move through, not onto).</summary>
    public static bool CanFocus(InputElement element) =>
        element.Focusable && element.IsEffectivelyVisible && element.IsEffectivelyEnabled && element is not ScrollViewer
        && element is not ScrollContentPresenter;

    /// <summary>Whether <paramref name="element"/> is <paramref name="container"/> or inside it.</summary>
    public static bool IsWithin(Visual? element, Visual container)
    {
        for (var node = element; node is not null; node = node.GetVisualParent())
        {
            if (node == container) return true;
        }
        return false;
    }

    /// <summary>The element with the keyboard in <paramref name="visual"/>'s window.</summary>
    public static IInputElement? Current(Visual visual) => TopLevel.GetTopLevel(visual)?.FocusManager?.GetFocusedElement();

    /// <summary>
    /// Focuses <paramref name="element"/> as the arrows would, so it shows its
    /// focus look, and brings it into view.
    /// </summary>
    public static void Move(InputElement element)
    {
        element.Focus(NavigationMethod.Directional);
        (element as Control)?.BringIntoView();
    }
}
