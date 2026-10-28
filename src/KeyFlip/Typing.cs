namespace KeyFlip;

/// <summary>
/// The word being typed, as the keyboard hook sees it, and whether it may be fixed. No Win32 here: the hook
/// feeds it the characters each key gives under the current and the other layout, so the tests can replay
/// real typing key by key.
/// </summary>
public sealed class WordTracker
{
    private readonly WordChecker _words;
    private readonly TypingActivity _activity;
    private readonly List<(string Typed, string Meant)> _keys = new();
    private Lang _lang;

    // The hook only knows the keys typed since the last word end. When it did not see where the text before
    // the caret ends (Backspace past that point, an arrow, a click, the field announced again mid-word, a
    // digit just before), the next letters may be the tail of a word it never saw: "wiht" + 3 Backspaces +
    // "th" left it judging "th" alone and flipping it to אי (bug 28-Oct). Such a word is never fixed.
    private bool _startSeen = true;

    public WordTracker(WordChecker words, TypingActivity activity)
    {
        _words = words;
        _activity = activity;
    }

    public int Count => _keys.Count;

    /// <summary>What a finished word was, and what to do with it.</summary>
    /// <param name="Fixed">The corrected word, or null to leave it.</param>
    /// <param name="Blocked">Why a word the plain dictionary rule would have fixed was left alone, or null.</param>
    /// <param name="RealWord">A real word in the current layout, 3+ letters (teaches the field its language).</param>
    public readonly record struct Ended(string Typed, string Meant, int Keys, string? Fixed, string? Blocked, bool RealWord);

    /// <summary>Does this key type a letter, in either layout (cc = under the current one, oc = the other)?</summary>
    public static bool IsWordKey(char cc, char oc, Lang current) => current == Lang.En
        ? char.IsLetter(cc) || WordChecker.IsHebrewLetter(oc)
        : WordChecker.IsHebrewLetter(cc) || WordChecker.IsLatinLetter(oc);

    public void Letter(string typed, string meant, Lang current)
    {
        if (_keys.Count > 0 && _lang != current) _keys.Clear();
        _lang = current;
        _keys.Add((typed, meant));
    }

    public void Backspace()
    {
        if (_keys.Count > 0) _keys.RemoveAt(_keys.Count - 1);
        else _startSeen = false; // now deleting into text typed before the buffer
    }

    /// <summary>The caret moved or the text changed without typing: arrows, Home/End, shortcuts.</summary>
    public void CaretMoved()
    {
        _keys.Clear();
        _startSeen = false;
    }

    /// <summary>A left click: the caret may now be anywhere.</summary>
    public void Click(DateTime at)
    {
        _activity.Click(at);
        CaretMoved();
    }

    /// <summary>A new field took the focus (or the app announced the same field again). Between words the
    /// next word starts fresh; mid-word, the letters that follow continue a word whose start is gone.</summary>
    public void NewField()
    {
        _startSeen = _keys.Count == 0;
        _keys.Clear();
    }

    /// <summary>Whitespace and prose punctuation start a word; a digit or a symbol glues to it ("28th", "a/b").</summary>
    private static bool StartsWord(string endText) =>
        endText.Length == 1 && (char.IsWhiteSpace(endText[0]) || "([{<\"'!?,.;:".Contains(endText[0]));

    /// <summary>A key that is not a letter ended the word (space, Enter, punctuation, a digit).</summary>
    public Ended? End(string endText, Lang current, IntPtr window, DateTime now, ISet<string>? neverFlip)
    {
        bool whole = _startSeen;
        _startSeen = StartsWord(endText);
        if (_keys.Count == 0) return null;
        var typed = string.Concat(_keys.Select(k => k.Typed));
        var meant = string.Concat(_keys.Select(k => k.Meant));
        int n = _keys.Count;
        _keys.Clear();

        var fixedWord = _words.Fix(typed, meant, current, neverFlip);
        string? blocked = null;
        if (fixedWord != null && !whole) blocked = "word start not seen";
        else if (fixedWord != null && current == Lang.En && _activity.TypingEnglish(window, now)
                 && _words.Fix(typed, meant, current, neverFlip, typingEnglish: true) == null)
            blocked = "typing English";
        if (blocked != null) fixedWord = null;

        var core = typed.Trim(',', '.', ';', '\'');
        bool real = current == Lang.En ? _words.EnglishRank(core) != 0
                                       : !WordChecker.LooksBrokenHebrew(typed) && _words.HebrewRank(typed) != 0;
        if (fixedWord != null) _activity.Word(window, now, Layouts.OtherOf(current));
        else if (current == Lang.En && _words.IsEnglishWord(core)) _activity.Word(window, now, Lang.En);
        else if (current == Lang.He && real) _activity.Word(window, now, Lang.He);
        return new Ended(typed, meant, n, fixedWord, blocked, real && typed.Length >= 3);
    }
}

/// <summary>
/// What the user is typing right now, per foreground window, shared by the focus decider and the word fixer.
/// The clock is passed in, so the tests can replay real timings.
/// </summary>
public sealed class TypingActivity
{
    /// <summary>A real English word typed in this window this recently = the user is typing English here.</summary>
    public static readonly TimeSpan EnglishWindow = TimeSpan.FromSeconds(30);

    /// <summary>A key typed this recently in this window = the user's fingers are still on the keys.</summary>
    public static readonly TimeSpan FlowingWindow = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private IntPtr _keyWindow;
    private DateTime _keyAt = DateTime.MinValue;
    private bool _keyMovesFocus;
    private DateTime _clickAt = DateTime.MinValue;
    private IntPtr _englishWindow;
    private DateTime _englishAt = DateTime.MinValue;

    /// <summary>A real keystroke. Tab, Enter, Escape and shortcuts move the focus on purpose.</summary>
    public void Key(IntPtr window, DateTime at, bool movesFocus)
    {
        lock (_lock) { _keyWindow = window; _keyAt = at; _keyMovesFocus = movesFocus; }
    }

    public void Click(DateTime at)
    {
        lock (_lock) _clickAt = at;
    }

    /// <summary>A finished real word (or a fixed one) in this window, in this language.</summary>
    public void Word(IntPtr window, DateTime at, Lang lang)
    {
        lock (_lock)
        {
            if (lang == Lang.En) { _englishWindow = window; _englishAt = at; }
            else if (window == _englishWindow) _englishAt = DateTime.MinValue;
        }
    }

    public bool TypingEnglish(IntPtr window, DateTime now)
    {
        lock (_lock) return window == _englishWindow && now - _englishAt < EnglishWindow;
    }

    /// <summary>
    /// The user is in the middle of typing in this window: a focus event now is the app announcing the same
    /// field again (VS Code and Chrome do this while you type), not the user going somewhere new.
    /// </summary>
    public bool KeysFlowing(IntPtr window, DateTime now)
    {
        lock (_lock) return window == _keyWindow && !_keyMovesFocus && _clickAt < _keyAt && now - _keyAt < FlowingWindow;
    }
}
