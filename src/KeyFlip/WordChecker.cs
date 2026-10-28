namespace KeyFlip;

/// <summary>Offline dictionary check: is a typed word a real word in its language, and would the
/// same keys on the other layout make one. Word lists: hermitdave/FrequencyWords (top 50k).</summary>
public sealed class WordChecker
{
    private readonly Dictionary<string, int> _en;
    private readonly Dictionary<string, int> _he;

    private const string HebrewPrefixes = "ובהלמשכ";
    private const string FinalLetters = "ךםןףץ";

    public WordChecker(Dictionary<string, int> en, Dictionary<string, int> he)
    {
        _en = en;
        _he = he;
    }

    public static WordChecker Load(string dir) =>
        new(ReadRanks(Path.Combine(dir, "en_50k.txt"), IsLatinWord),
            ReadRanks(Path.Combine(dir, "he_50k.txt"), IsHebrewWord));

    private static Dictionary<string, int> ReadRanks(string path, Func<string, bool> keep)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        int rank = 0;
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            var word = line.Split(' ')[0].Trim().ToLowerInvariant();
            if (word.Length == 0 || !keep(word)) continue;
            rank++;
            d.TryAdd(word, rank);
        }
        return d;
    }

    public static bool IsHebrewLetter(char c) => c >= 'א' && c <= 'ת';
    public static bool IsLatinLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    public static bool IsLatinWord(string w) => w.All(c => IsLatinLetter(c) || c == '\'');
    public static bool IsHebrewWord(string w) => w.All(c => IsHebrewLetter(c) || c == '\'' || c == '"');

    /// <summary>Rank of an English word (1 = most common), or 0 if unknown.</summary>
    public int EnglishRank(string w) => _en.TryGetValue(w.ToLowerInvariant(), out var r) ? r : 0;

    /// <summary>While typing English, only a Hebrew word at least this common is worth flipping to.</summary>
    internal const int MidEnglishHebrewRank = 10000;

    /// <summary>English words at least this common count as "one typo away" in <see cref="NearEnglish"/>.</summary>
    internal const int TypoSourceRank = 10000;

    /// <summary>
    /// One typo (a dropped, extra, wrong or swapped letter) away from a common English word: "nto" (not),
    /// "eue" (due), "wiht" (with). Fast typists make these all the time; they are not Hebrew.
    /// </summary>
    public bool NearEnglish(string w)
    {
        w = w.ToLowerInvariant();
        bool Common(string c) => _en.TryGetValue(c, out var r) && r <= TypoSourceRank;
        for (int i = 0; i < w.Length; i++)
        {
            if (Common(w.Remove(i, 1))) return true;
            if (i + 1 < w.Length && Common(w[..i] + w[i + 1] + w[i] + w[(i + 2)..])) return true;
            for (char c = 'a'; c <= 'z'; c++)
                if (c != w[i] && Common(w[..i] + c + w[(i + 1)..])) return true;
        }
        for (int i = 0; i <= w.Length; i++)
            for (char c = 'a'; c <= 'z'; c++)
                if (Common(w.Insert(i, c.ToString()))) return true;
        return false;
    }

    /// <summary>A real English word: any listed word of 3+ letters, or a common short one ("if", "we", "to").</summary>
    public bool IsEnglishWord(string w)
    {
        int r = w.Length > 0 ? EnglishRank(w) : 0;
        return r != 0 && (w.Length > 2 || r <= 2000);
    }

    /// <summary>Rank of a Hebrew word, also trying with up to two prefix letters (ו ה ב ל מ ש כ) removed.</summary>
    public int HebrewRank(string w)
    {
        if (_he.TryGetValue(w, out var r)) return r;
        for (int strip = 1; strip <= 2 && w.Length - strip >= 2; strip++)
        {
            if (!HebrewPrefixes.Contains(w[strip - 1])) break;
            if (_he.TryGetValue(w[strip..], out r)) return r;
        }
        return 0;
    }

    /// <summary>Hebrew never has a final-form letter in the middle of a word, and never has
    /// Latin punctuation inside it. Either one means the keys were meant for English.</summary>
    public static bool LooksBrokenHebrew(string w)
    {
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            if (!IsHebrewLetter(c)) return true;
            if (i < w.Length - 1 && FinalLetters.Contains(c)) return true;
        }
        return false;
    }

    private static string TrimNonLetters(string w)
    {
        int s = 0, e = w.Length;
        while (s < e && !char.IsLetter(w[s])) s++;
        while (e > s && !char.IsLetter(w[e - 1])) e--;
        return w[s..e];
    }

    /// <summary>
    /// Given the same keystrokes read under the current layout (typed) and under the other
    /// layout (other), returns the corrected text if the word was clearly typed on the wrong
    /// layout, or null to leave it alone.
    /// </summary>
    public string? Fix(string typed, string other, Lang current, ISet<string>? neverFlip = null, bool typingEnglish = false)
    {
        if (typed.Length < 2 || typed.Length != other.Length) return null;

        if (current == Lang.He)
        {
            // Typed on Hebrew, maybe meant English.
            var en = TrimNonLetters(other);
            if (en.Length < 2 || !IsLatinWord(en)) return null;
            if (neverFlip != null && neverFlip.Contains(typed)) return null;
            int enRank = EnglishRank(en);
            if (enRank == 0) return null;
            if (en.Length == 2 && enRank > 300) return null;
            bool broken = LooksBrokenHebrew(typed);
            int heRank = broken ? 0 : HebrewRank(typed);
            // A real Hebrew word stays, unless it is rare and the English one is common.
            if (heRank != 0 && !(heRank > 20000 && enRank < 2000)) return null;
            return other;
        }

        if (current == Lang.En)
        {
            // Typed on English, maybe meant Hebrew.
            var core = TrimNonLetters(typed);
            if (neverFlip != null && neverFlip.Contains(typed.ToLowerInvariant())) return null;
            if (!IsHebrewWord(other) || LooksBrokenHebrew(other.Trim('\'', '"'))) return null;
            // Short tokens like "gk" sit deep in the subtitle list; only a common one counts as English.
            int coreRank = core.Length > 0 ? EnglishRank(core) : 0;
            if (coreRank != 0 && (core.Length > 2 || coreRank <= 2000)) return null;
            // Capitalized words in English are usually names; leave them.
            if (core.Length > 0 && char.IsUpper(core[0])) return null;
            int heRank = HebrewRank(other);
            if (heRank == 0) return null;
            if (other.Length == 2 && heRank > 300) return null;
            if (other.Length == 3 && heRank > 20000) return null;
            // While the user is typing English, a flip needs keys that only make sense as Hebrew (bug 28-Oct:
            // "th", "sh", "eue", "nto" were tails and typos of English words, flipped to אי, די, קוק, מאם).
            if (typingEnglish && (core.Length < 3 || heRank > MidEnglishHebrewRank || NearEnglish(core))) return null;
            return other;
        }

        return null;
    }
}
