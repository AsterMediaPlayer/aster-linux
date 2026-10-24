// Navigation state of an open disc menu (CLIENT_GUIDE §4.3–4.7): the screen
// stack, page and focus memory, actions and scene selection. The part of
// the Apple app's DiscMenuController that doesn't draw or play anything;
// the menu's music, sounds and trailer follow it from the app.

namespace Aster.Core.DiscMenu;

/// <summary>Which screen is on the stack: one from the document, or a generated scene-selection screen.</summary>
public sealed record DiscScreenRef(string Key, bool IsChapters = false);

/// <summary>A screen with its inherited settings resolved and its page laid out.</summary>
public sealed record DiscResolvedScreen
{
    public required DiscScreenRef Ref { get; init; }
    public required string Title { get; init; }
    public required DiscTheme Theme { get; init; }
    public DiscBackground? Background { get; init; }
    public required DiscLayout Layout { get; init; }
    public DiscAudio? Audio { get; init; }
    public required IReadOnlyList<DiscMenuEntry> Entries { get; init; }
    public bool Positioned { get; init; }
    public int Page { get; init; }
    public int PageCount { get; init; }

    public string TransitionStyle => Layout.Transition?.Style ?? "none";

    public TimeSpan TransitionDuration => TimeSpan.FromMilliseconds(Math.Clamp(Layout.Transition?.DurationMs ?? 300, 0, 2000));
}

public enum DiscSound
{
    Move,
    Select,
    Back,
}

/// <summary>What a menu button plays: the title (from a chapter), an extra, or extras one after another.</summary>
public sealed record DiscPlayback(IReadOnlyList<string> ItemIds, long StartTicks = 0);

public sealed class DiscMenuNavigator
{
    private readonly Func<int, long?, Uri> chapterImage;
    private List<DiscScreenRef> stack;
    private readonly Dictionary<DiscScreenRef, int> pageMemory = [];
    private readonly Dictionary<DiscScreenRef, int> focusMemory = [];
    private readonly Dictionary<DiscScreenRef, DiscMenuScreenSpec> generated = [];

    /// <summary>The buttons' frames on screen, as the view last measured them, and for which <see cref="Generation"/>.</summary>
    private IReadOnlyList<DiscRect>? frames;
    private int framesGeneration = -1;

    /// <param name="chapterImage">A chapter's picture, by its index and image stamp.</param>
    public DiscMenuNavigator(DiscMenuDocument document, string itemId, Func<int, long?, Uri> chapterImage)
    {
        Document = document;
        ItemId = itemId;
        this.chapterImage = chapterImage;
        var root = new DiscScreenRef(document.Root);
        stack = [root];
        Screen = Resolve(root, 0);
    }

    public string ItemId { get; }
    public DiscMenuDocument Document { get; private set; }
    public DiscResolvedScreen Screen { get; private set; }
    public int FocusIndex { get; private set; }

    /// <summary>Bumped on every screen or page change; the view lays out a new screen for each.</summary>
    public int Generation { get; private set; }

    /// <summary>+1 going forward (submenu, More), -1 going back (Back, Home, Previous).</summary>
    public int Direction { get; private set; } = 1;

    /// <summary>False when the screen must be replaced without animation.</summary>
    public bool AnimateChange { get; private set; }

    /// <summary>The screen or page changed: draw <see cref="Screen"/> afresh.</summary>
    public event Action? ScreenChanged;

    /// <summary>Focus moved to another button on the same screen.</summary>
    public event Action? FocusChanged;

    public event Action<DiscSound>? Sound;
    public event Action<DiscPlayback>? PlaybackRequested;
    public event Action<string>? Alert;

    /// <summary>Back from the root screen, or a Back entry there: the menu closes.</summary>
    public event Action? CloseRequested;

    /// <summary>Whether Back steps to an earlier page or screen rather than closing.</summary>
    public bool CanGoBack => Screen.Page > 0 || stack.Count > 1;

    // ---- Lifecycle ----

    public void Open()
    {
        FocusIndex = 0;
        AnimateChange = true;
        Generation++;
        ScreenChanged?.Invoke();
    }

    /// <summary>Playback ended: takes the fresh document (when there is one) and shows the same screen again.</summary>
    public void Reload(DiscMenuDocument? fresh)
    {
        if (fresh is not null) Document = fresh;
        // Drop screens that no longer exist; generated screens are rebuilt on demand.
        stack = stack.Where(r => r.IsChapters ? generated.ContainsKey(r) : Document.Menus.ContainsKey(r.Key)).ToList();
        if (stack.Count == 0) stack = [new DiscScreenRef(Document.Root)];
        AnimateChange = false;
        Rebuild(keepFocus: true);
    }

    // ---- Geometry from the view ----

    /// <summary>The buttons' frames, in drawing order, as laid out for <paramref name="generation"/>.</summary>
    public void ReportFrames(IReadOnlyList<DiscRect> rects, int generation)
    {
        frames = rects;
        framesGeneration = generation;
    }

    private IReadOnlyList<DiscRect>? CurrentFrames =>
        framesGeneration == Generation && frames is { Count: > 0 } measured && measured.Count >= Screen.Entries.Count ? measured : null;

    // ---- Input ----

    /// <summary>Moves focus one step: -1/1 on each axis, left/up being negative.</summary>
    public void Move(int dx, int dy)
    {
        if (Screen.Entries.Count == 0) return;
        int target;
        if (CurrentFrames is { } rects)
        {
            target = DiscMenuRules.MoveFocus(rects, FocusIndex, dx, dy);
        }
        else
        {
            // Frames not measured yet: step through the drawing order.
            var step = dx + dy > 0 ? 1 : -1;
            target = (FocusIndex + step + Screen.Entries.Count) % Screen.Entries.Count;
        }
        if (target == FocusIndex) return;
        Focus(target);
        Sound?.Invoke(DiscSound.Move);
    }

    /// <summary>The pointer came to rest on a button: it takes focus, without a sound.</summary>
    public void Hover(int index)
    {
        if (index < 0 || index >= Screen.Entries.Count || index == FocusIndex) return;
        Focus(index);
    }

    private void Focus(int index)
    {
        FocusIndex = index;
        focusMemory[Screen.Ref] = index;
        FocusChanged?.Invoke();
    }

    public void Select()
    {
        if (FocusIndex >= 0 && FocusIndex < Screen.Entries.Count) Activate(Screen.Entries[FocusIndex]);
    }

    /// <summary>A click at a point in the frames' space: highlights and activates the button under it, if any.</summary>
    public void Tap(double x, double y)
    {
        if (CurrentFrames is not { } rects) return;
        for (var index = rects.Count - 1; index >= 0; index--)
        {
            if (!rects[index].Contains(x, y)) continue;
            if (index != FocusIndex) Focus(index);
            Activate(Screen.Entries[index]);
            return;
        }
    }

    /// <summary>The remote's Back: a page back, then a screen back, then close.</summary>
    public void HardwareBack()
    {
        Sound?.Invoke(DiscSound.Back);
        if (Screen.Page > 0) ChangePage(-1);
        else if (stack.Count > 1) Pop();
        else CloseRequested?.Invoke();
    }

    /// <summary>The play key plays what the focused button plays, and does nothing on any other.</summary>
    public void PlayPause()
    {
        if (FocusIndex < 0 || FocusIndex >= Screen.Entries.Count) return;
        var entry = Screen.Entries[FocusIndex];
        if (entry.Kind is DiscAction.PlayFeature or DiscAction.PlayExtra or DiscAction.PlaySequence or DiscAction.PlayChapter) Activate(entry);
    }

    // ---- Actions ----

    public void Activate(DiscMenuEntry entry)
    {
        switch (entry.Kind)
        {
            case DiscAction.Submenu:
                if (entry.Menu is not { } key || !Document.Menus.ContainsKey(key)) return;
                Sound?.Invoke(DiscSound.Select);
                Push(new DiscScreenRef(key));
                break;
            case DiscAction.Back:
                Sound?.Invoke(DiscSound.Back);
                if (stack.Count > 1) Pop();
                else CloseRequested?.Invoke();
                break;
            case DiscAction.Home:
                Sound?.Invoke(DiscSound.Back);
                Home();
                break;
            case DiscAction.PagePrev:
                Sound?.Invoke(DiscSound.Back);
                ChangePage(-1);
                break;
            case DiscAction.PageNext:
                Sound?.Invoke(DiscSound.Select);
                ChangePage(1);
                break;
            case DiscAction.Chapters:
                Sound?.Invoke(DiscSound.Select);
                OpenChapters(entry);
                break;
            case DiscAction.PlayFeature:
                Sound?.Invoke(DiscSound.Select);
                long start = 0;
                if (entry.StartChapter is { } chapter && Document.Chapters is { } chapters && chapter >= 1 && chapter <= chapters.Count)
                {
                    start = chapters[chapter - 1].StartTicks;
                }
                PlaybackRequested?.Invoke(new DiscPlayback([ItemId], start));
                break;
            case DiscAction.PlayExtra:
                Sound?.Invoke(DiscSound.Select);
                if (entry.ItemId is not { } extra)
                {
                    Alert?.Invoke($"\"{entry.Label}\" isn't available in this library.");
                    return;
                }
                PlaybackRequested?.Invoke(new DiscPlayback([extra]));
                break;
            case DiscAction.PlaySequence:
                Sound?.Invoke(DiscSound.Select);
                if (entry.ItemIds is not { Count: > 0 } ids)
                {
                    Alert?.Invoke($"None of \"{entry.Label}\" is available in this library.");
                    return;
                }
                PlaybackRequested?.Invoke(new DiscPlayback(ids));
                break;
            case DiscAction.PlayChapter:
                Sound?.Invoke(DiscSound.Select);
                PlaybackRequested?.Invoke(new DiscPlayback([ItemId], entry.StartTicks ?? 0));
                break;
        }
    }

    private void Push(DiscScreenRef screen)
    {
        pageMemory[screen] = 0;
        focusMemory.Remove(screen);
        stack.Add(screen);
        Direction = 1;
        AnimateChange = true;
        Rebuild(keepFocus: false);
    }

    private void Pop()
    {
        if (stack.Count <= 1) return;
        stack.RemoveAt(stack.Count - 1);
        Direction = -1;
        AnimateChange = true;
        Rebuild(keepFocus: true);
    }

    private void Home()
    {
        stack = [new DiscScreenRef(Document.Root)];
        pageMemory.Clear();
        Direction = -1;
        AnimateChange = true;
        Rebuild(keepFocus: true);
    }

    private void ChangePage(int delta)
    {
        var next = Screen.Page + delta;
        if (next < 0 || next >= Screen.PageCount) return;
        pageMemory[Screen.Ref] = next;
        focusMemory[Screen.Ref] = 0;
        Direction = delta > 0 ? 1 : -1;
        AnimateChange = true;
        Rebuild(keepFocus: false);
    }

    // ---- Scene selection ----

    private void OpenChapters(DiscMenuEntry entry)
    {
        if (Document.Chapters is not { Count: > 0 } chapters)
        {
            Alert?.Invoke("This title has no chapter markers.");
            return;
        }
        var screen = new DiscScreenRef($"{entry.Menu ?? ""}|{entry.Label}", IsChapters: true);
        var styling = entry.Menu is { } key && Document.Menus.TryGetValue(key, out var found) ? found : null;
        var perPage = Math.Min(24, Math.Max(1, entry.PerPage ?? 6));

        var entries = chapters.Select((chapter, position) => new DiscMenuEntry
        {
            Action = "playChapter",
            Label = string.IsNullOrEmpty(chapter.Name) ? $"Chapter {position + 1}" : chapter.Name,
            Sub = DiscMenuRules.FormatTicks(chapter.StartTicks),
            ThumbUrl = chapter.HasImage == true ? chapterImage(chapter.Index, chapter.ImageStamp) : null,
            StartTicks = chapter.StartTicks,
        }).ToList();
        if (styling is not null) entries.AddRange(styling.Entries);
        else entries.Add(new DiscMenuEntry { Action = "back", Label = "Back" });

        var layout = styling?.Layout ?? new DiscLayout();
        if (DiscMenuRules.EffectiveLayout(Document.Layout, styling?.Layout).Flow is null)
        {
            layout = layout with { Flow = DiscMenuRules.DefaultChapterFlow(perPage) };
        }

        generated[screen] = new DiscMenuScreenSpec
        {
            Title = styling?.Title ?? entry.Label,
            Entries = entries,
            Background = styling?.Background,
            Theme = styling?.Theme,
            Layout = layout,
            Audio = styling?.Audio,
            MaxPerPage = perPage,
        };
        Push(screen);
    }

    // ---- Resolution ----

    private DiscResolvedScreen Resolve(DiscScreenRef screen, int page)
    {
        var spec = (screen.IsChapters ? generated.GetValueOrDefault(screen) : Document.Menus.GetValueOrDefault(screen.Key))
            ?? new DiscMenuScreenSpec { Title = "" };
        var laid = DiscMenuRules.EntriesForPage(Document, spec, page);
        return new DiscResolvedScreen
        {
            Ref = screen,
            Title = spec.Title,
            Theme = DiscMenuRules.EffectiveTheme(Document.Theme, spec.Theme),
            Background = DiscMenuRules.EffectiveBackground(Document.Background, spec.Background),
            Layout = DiscMenuRules.EffectiveLayout(Document.Layout, spec.Layout),
            Audio = DiscMenuRules.EffectiveAudio(Document.Audio, spec.Audio),
            Entries = laid.Entries,
            Positioned = laid.Positioned,
            Page = laid.Page,
            PageCount = laid.PageCount,
        };
    }

    private void Rebuild(bool keepFocus)
    {
        var screen = stack[^1];
        var resolved = Resolve(screen, pageMemory.GetValueOrDefault(screen));
        var remembered = keepFocus ? focusMemory.GetValueOrDefault(screen) : 0;
        Screen = resolved;
        FocusIndex = resolved.Entries.Count == 0 ? 0 : Math.Min(remembered, resolved.Entries.Count - 1);
        Generation++;
        ScreenChanged?.Invoke();
    }
}
