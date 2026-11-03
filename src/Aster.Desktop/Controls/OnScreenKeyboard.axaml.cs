using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;

namespace Aster.Desktop.Controls;

/// <summary>
/// A keyboard on the screen, for searching with a controller from the couch
/// (gamescope, Big Picture), where there's no keyboard to type on: rows of
/// letters and digits, then Space, Delete and Clear, as the Apple TV's
/// search has. The arrows move key by key; Up and Down go to the nearest key
/// in the row above or below. A key a keyboard types (with one plugged in)
/// goes in too.
/// </summary>
public partial class OnScreenKeyboard : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<OnScreenKeyboard, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    private static readonly string[] Characters = ["abcdefghijklm", "nopqrstuvwxyz", "1234567890"];

    private readonly List<List<Button>> rows = [];

    public OnScreenKeyboard()
    {
        InitializeComponent();
        foreach (var characters in Characters)
            AddRow(characters.Select(ch => Key(ch.ToString(), () => Type(ch.ToString()), name: ch.ToString())));
        AddRow([
            Key("space", () => Type(" "), wide: true, name: "Space"),
            Key("", Delete, name: "Delete", icon: true),
            Key("Clear", () => Text = "", wide: true, name: "Clear"),
        ]);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The first key, where the search page starts.</summary>
    public Button First => rows[0][0];

    /// <summary>Onto the bottom row's key nearest <paramref name="from"/> (coming up from the results).</summary>
    public void FocusBottom(Visual from) => KeyFocus.Move(Nearest(rows[^1], from));

    private void AddRow(IEnumerable<Button> keys)
    {
        var row = keys.ToList();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var key in row) panel.Children.Add(key);
        Rows.Children.Add(panel);
        rows.Add(row);
    }

    private static Button Key(string label, Action press, string name, bool wide = false, bool icon = false)
    {
        var key = new Button { Content = label, Focusable = true };
        if (Application.Current?.FindResource("PillButton") is ControlTheme pill) key.Theme = pill;
        key.Classes.Add("key");
        if (wide) key.Classes.Add("wide");
        if (icon && Application.Current?.FindResource("IconFont") is Avalonia.Media.FontFamily font) key.FontFamily = font;
        Avalonia.Automation.AutomationProperties.SetName(key, name);
        key.Click += (_, _) => press();
        return key;
    }

    private void Type(string characters) => Text += characters;

    private void Delete()
    {
        if (Text.Length > 0) Text = Text[..^1];
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Avalonia.Input.Key.Back)
        {
            Delete();
            e.Handled = true;
            return;
        }
        // A typed space comes as text (below); a key mustn't take it as a press too.
        if (e.Key == Avalonia.Input.Key.Space)
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not Button key || Locate(key) is not var (r, c)) return;
        Button? next = e.Key switch
        {
            Avalonia.Input.Key.Left when c > 0 => rows[r][c - 1],
            Avalonia.Input.Key.Right => c < rows[r].Count - 1 ? rows[r][c + 1] : key,
            Avalonia.Input.Key.Up when r > 0 => Nearest(rows[r - 1], key),
            Avalonia.Input.Key.Down when r < rows.Count - 1 => Nearest(rows[r + 1], key),
            // Left from the first key opens the sidebar, Up from the top row is the top bar's,
            // Down from the bottom row goes on to the results: the page's to decide.
            _ => null,
        };
        if (next is null) return;
        KeyFocus.Move(next);
        e.Handled = true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        Type(e.Text);
        e.Handled = true;
    }

    private (int Row, int Column)? Locate(Button key)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            var c = rows[r].IndexOf(key);
            if (c >= 0) return (r, c);
        }
        return null;
    }

    /// <summary>The key in <paramref name="row"/> whose middle is nearest across to <paramref name="from"/>'s.</summary>
    private Button Nearest(List<Button> row, Visual from)
    {
        double Middle(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width / 2, 0), this)?.X ?? 0;
        var x = Middle(from);
        return row.MinBy(k => Math.Abs(Middle(k) - x))!;
    }
}
