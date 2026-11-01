using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SDL;
using static SDL.SDL3;

namespace Aster.Desktop.Input;

/// <summary>
/// Game controllers, read through SDL3 (Xbox, PlayStation, Switch and most
/// others, plugged in or out while the app runs), as the keys the app
/// already knows, so every page, guide and player works with one:
///
/// <list type="bullet">
/// <item>the D-pad or left stick: the arrows, repeating while held;</item>
/// <item>A (the bottom button): Enter, to choose;</item>
/// <item>B (the right button): Back, which goes back, or opens the sidebar;</item>
/// <item>X or Start: play/pause;</item>
/// <item>the shoulder buttons: previous and next (the song, the book's chapter, the channel).</item>
/// </list>
///
/// It only acts while the app's window is in front, so a controller used in
/// another app leaves this one be. Without SDL (a Linux machine missing it,
/// say), the app goes on without controllers.
/// </summary>
public sealed unsafe class Gamepads : IDisposable
{
    /// <summary>How far a stick goes to count as pushed, and how far back to count as let go: the gap stops it chattering.</summary>
    private const short StickPress = 16000;
    private const short StickRelease = 9000;

    /// <summary>A held direction repeats as a held arrow key does: a pause, then quick steps.</summary>
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

    private readonly Window window;
    private readonly Thread thread;
    private volatile bool stopping;

    private readonly Dictionary<SDL_JoystickID, IntPtr> open = [];
    // Per direction source, so the D-pad and the stick don't fight over one arrow.
    private Key? dpad;
    private Key? stick;
    private short stickX, stickY;
    private Key? held;
    private DateTime nextRepeat;

    private Gamepads(Window window)
    {
        this.window = window;
        thread = new Thread(Run) { IsBackground = true, Name = "Gamepads" };
        thread.Start();
    }

    /// <summary>Starts listening for controllers for <paramref name="window"/>.</summary>
    public static Gamepads Start(Window window) => new(window);

    public void Dispose()
    {
        stopping = true;
        thread.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        try
        {
            // No window of SDL's own: it hears controllers in the background, on its own thread,
            // and leaves Ctrl+C to the app.
            SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1"u8);
            SDL_SetHint(SDL_HINT_JOYSTICK_THREAD, "1"u8);
            SDL_SetHint(SDL_HINT_NO_SIGNAL_HANDLERS, "1"u8);
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD))
            {
                Trace.WriteLine($"Gamepads: SDL didn't start: {SDL_GetError()}");
                return;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Trace.WriteLine($"Gamepads: no SDL3 ({error.Message}); going on without controllers");
            return;
        }

#if DEBUG
        StartScript();
#endif
        try
        {
            SDL_Event e;
            while (!stopping)
            {
                // Awake often enough to repeat a held direction, and to notice stopping.
                if (SDL_WaitEventTimeout(&e, 30)) Handle(ref e);
                while (!stopping && SDL_PollEvent(&e)) Handle(ref e);
                Repeat();
#if DEBUG
                Script();
#endif
            }
        }
        finally
        {
            foreach (var gamepad in open.Values) SDL_CloseGamepad((SDL_Gamepad*)gamepad);
            open.Clear();
            SDL_Quit();
        }
    }

    private void Handle(ref SDL_Event e)
    {
        switch ((SDL_EventType)e.type)
        {
            // Those plugged in when it starts come as added too.
            case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                var id = e.gdevice.which;
                if (!open.ContainsKey(id) && SDL_OpenGamepad(id) is var gamepad && gamepad != null)
                {
                    open[id] = (IntPtr)gamepad;
                    Trace.WriteLine($"Gamepads: {SDL_GetGamepadName(gamepad)} connected");
                }
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                if (open.Remove(e.gdevice.which, out var removed))
                {
                    SDL_CloseGamepad((SDL_Gamepad*)removed);
                    Trace.WriteLine("Gamepads: a controller disconnected");
                }
                dpad = stick = null;
                Held();
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
                Button((SDL_GamepadButton)e.gbutton.button, down: true);
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                Button((SDL_GamepadButton)e.gbutton.button, down: false);
                break;
            case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                Axis((SDL_GamepadAxis)e.gaxis.axis, e.gaxis.value);
                break;
        }
    }

    private void Button(SDL_GamepadButton button, bool down)
    {
        Key? direction = button switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => Key.Up,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => Key.Down,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => Key.Left,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => Key.Right,
            _ => null,
        };
        if (direction is { } arrow)
        {
            if (down) dpad = arrow;
            else if (dpad == arrow) dpad = null;
            Held();
            return;
        }
        if (!down) return;
        Key? key = button switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => Key.Enter,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => Key.BrowserBack,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST or SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => Key.MediaPlayPause,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => Key.MediaPreviousTrack,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => Key.MediaNextTrack,
            _ => null,
        };
        if (key is { } pressed) Send(pressed);
    }

    /// <summary>The left stick, as the D-pad: whichever way it's pushed furthest.</summary>
    private void Axis(SDL_GamepadAxis axis, short value)
    {
        if (axis == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX) stickX = value;
        else if (axis == SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY) stickY = value;
        else return;
        var x = Math.Abs((int)stickX);
        var y = Math.Abs((int)stickY);
        var limit = stick is null ? StickPress : StickRelease;
        Key? now = Math.Max(x, y) < limit ? null
            : x > y ? (stickX < 0 ? Key.Left : Key.Right)
            : (stickY < 0 ? Key.Up : Key.Down);
        if (now == stick) return;
        stick = now;
        Held();
    }

    /// <summary>The direction held changed: step once now, then repeat while it stays held.</summary>
    private void Held()
    {
        var now = dpad ?? stick;
        if (now == held) return;
        held = now;
        if (now is not { } key) return;
        Send(key);
        nextRepeat = DateTime.UtcNow + RepeatDelay;
    }

    private void Repeat()
    {
        if (held is not { } key || DateTime.UtcNow < nextRepeat) return;
        Send(key);
        nextRepeat = DateTime.UtcNow + RepeatInterval;
    }

#if DEBUG
    // ASTER_GAMEPAD_SCRIPT, for trying the app without a controller: a
    // pretend one, pressed at times after it starts, as "seconds:button"
    // pairs, e.g. "10:down,12:a,14:b" (a, b, x, y, start, lb, rb, up, down,
    // left, right; "hold-" before one holds it a second and a half).
    private readonly Queue<(DateTime At, int Button, bool Down)> script = new();
    private SDL_Joystick* scripted;

    private void StartScript()
    {
        if (Environment.GetEnvironmentVariable("ASTER_GAMEPAD_SCRIPT") is not { Length: > 0 } text) return;
        SDL_INIT_INTERFACE(out SDL_VirtualJoystickDesc desc);
        desc.type = (ushort)SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD;
        desc.naxes = (ushort)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_COUNT;
        desc.nbuttons = (ushort)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC1;
        var id = SDL_AttachVirtualJoystick(&desc);
        scripted = SDL_OpenJoystick(id);
        var names = new Dictionary<string, SDL_GamepadButton>
        {
            ["a"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, ["b"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST,
            ["x"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, ["y"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH,
            ["start"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START,
            ["lb"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, ["rb"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER,
            ["up"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, ["down"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN,
            ["left"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, ["right"] = SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT,
        };
        var start = DateTime.UtcNow;
        foreach (var step in text.Split(','))
        {
            var (at, name) = (step.Split(':')[0], step.Split(':')[1]);
            var hold = name.StartsWith("hold-");
            var button = (int)names[hold ? name[5..] : name];
            var when = start + TimeSpan.FromSeconds(double.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
            script.Enqueue((when, button, true));
            script.Enqueue((when + TimeSpan.FromMilliseconds(hold ? 1500 : 80), button, false));
        }
    }

    private void Script()
    {
        while (scripted != null && script.TryPeek(out var next) && next.At <= DateTime.UtcNow)
        {
            script.Dequeue();
            SDL_SetJoystickVirtualButton(scripted, next.Button, next.Down);
        }
    }
#endif

    /// <summary>
    /// The key, as if typed: down and up at the control with the keyboard
    /// (or the window, with nothing focused), so the window's own handlers,
    /// the arrows' moving between controls and buttons' Enter all hear it.
    /// </summary>
    private void Send(Key key) => Dispatcher.UIThread.Post(() =>
    {
        // A couch session's one window is the screen, so it always hears the
        // controller, whether or not gamescope says it's focused.
        if (!window.IsActive && !Aster.Desktop.Controls.FullScreen.Pinned) return;
        var target = window.FocusManager?.GetFocusedElement() as Interactive ?? window;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyDeviceType = KeyDeviceType.Gamepad, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyDeviceType = KeyDeviceType.Gamepad, Source = target });
    });
}
