using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Aster.Desktop.Controls;

/// <summary>
/// A PIN pad on the screen, for a profile's PIN with a controller from the
/// couch: the digits so far as dots, then 1 to 9, and Delete, 0 and OK, as a
/// TV's PIN screens have. The arrows move key by key (Down from the bottom
/// row is the page's); a keyboard's digits, Backspace and Enter work too.
/// Its keys look like the search keyboard's (<see cref="OnScreenKeyboard"/>).
/// </summary>
public sealed class PinPad : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<PinPad, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The most digits it takes (Bonfire's PINs are 4 to 8).</summary>
    public static readonly StyledProperty<int> MaxLengthProperty = AvaloniaProperty.Register<PinPad, int>(nameof(MaxLength), 8);

    /// <summary>OK, and Enter: the PIN as it is.</summary>
    public static readonly StyledProperty<ICommand?> SubmitCommandProperty = AvaloniaProperty.Register<PinPad, ICommand?>(nameof(SubmitCommand));

    private readonly List<List<Button>> rows = [];
    private readonly TextBlock dots = new()
    {
        FontSize = 26, LetterSpacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Height = 36, Foreground = Brushes.White,
    };

    public PinPad()
    {
        var grid = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        grid.Children.Add(dots);
        foreach (var digits in new[] { "123", "456", "789" })
            AddRow(grid, digits.Select(d => MakeKey(d.ToString(), () => Type(d), d.ToString())));
        AddRow(grid, [
            MakeKey("", Delete, "Delete", icon: true),
            MakeKey("0", () => Type('0'), "0"),
            MakeKey("OK", Submit, "OK", prominent: true),
        ]);
        Content = grid;
        Show();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int MaxLength
    {
        get => GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public ICommand? SubmitCommand
    {
        get => GetValue(SubmitCommandProperty);
        set => SetValue(SubmitCommandProperty, value);
    }

    /// <summary>The 1, where the pad starts.</summary>
    public Button First => rows[0][0];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) Show();
    }

    /// <summary>A dot for each digit typed; a faint line for none yet.</summary>
    private void Show()
    {
        var length = Text?.Length ?? 0;
        dots.Text = length == 0 ? "– – – –" : new string('●', length);
        dots.Opacity = length == 0 ? 0.35 : 1;
    }

    private void AddRow(StackPanel grid, IEnumerable<Button> keys)
    {
        var row = keys.ToList();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var key in row) panel.Children.Add(key);
        grid.Children.Add(panel);
        rows.Add(row);
    }

    private static Button MakeKey(string label, Action press, string name, bool icon = false, bool prominent = false)
    {
        var key = new Button
        {
            Content = label, Width = 72, Height = 50, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(12),
            FontSize = 22, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (Application.Current?.FindResource("PillButton") is ControlTheme pill) key.Theme = pill;
        if (prominent) key.Classes.Add("prominent");
        if (icon && Application.Current?.FindResource("IconFont") is FontFamily font)
        {
            key.FontFamily = font;
            key.FontSize = 20;
        }
        Avalonia.Automation.AutomationProperties.SetName(key, name);
        key.Click += (_, _) => press();
        return key;
    }

    private void Type(char digit)
    {
        if ((Text ?? "").Length < MaxLength) Text = (Text ?? "") + digit;
    }

    private void Delete()
    {
        if (Text is { Length: > 0 } text) Text = text[..^1];
    }

    private void Submit()
    {
        if (SubmitCommand is { } submit && submit.CanExecute(null)) submit.Execute(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Back:
                Delete();
                e.Handled = true;
                return;
            // A keyboard's Enter: the PIN as typed (a controller's A presses the key it's on instead).
            case Key.Enter or Key.Return when e.KeyDeviceType != KeyDeviceType.Gamepad:
                Submit();
                e.Handled = true;
                return;
        }
        if (e.Source is not Button key || Locate(key) is not var (r, c)) return;
        Button? next = e.Key switch
        {
            Key.Left when c > 0 => rows[r][c - 1],
            Key.Right when c < rows[r].Count - 1 => rows[r][c + 1],
            Key.Up when r > 0 => rows[r - 1][c],
            Key.Down when r < rows.Count - 1 => rows[r + 1][c],
            // Left, Right or Up off the pad stays on it; Down from the bottom row is the page's (Cancel).
            Key.Left or Key.Right or Key.Up => key,
            _ => null,
        };
        if (next is null) return;
        KeyFocus.Move(next);
        e.Handled = true;
    }

    /// <summary>A keyboard's digits (the number row, or the keypad's).</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        foreach (var ch in e.Text.Where(char.IsAsciiDigit)) Type(ch);
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
}
