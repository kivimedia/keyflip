// KeyFlip tests. `dotnet run` = offline checks (exit 1 on any failure).
// `dotnet run -- probe` = also asks Jev and Haiku about sample fields (costs a fraction of a cent).
using System.IO;
using System.Text;
using KeyFlip;

Console.OutputEncoding = Encoding.UTF8;
int failures = 0;
void Check(bool ok, string what)
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
}

Layouts.Init();
Check(Layouts.Ready, $"Hebrew and English layouts found (he={Layouts.He:X}, en={Layouts.En:X})");
if (!Layouts.Ready) return 1;

var dictDir = Path.Combine(AppContext.BaseDirectory, "dict");
var words = WordChecker.Load(dictDir);
string HeKeys(string english) => Layouts.Transliterate(english, Layouts.En, Layouts.He);   // English keys on the Hebrew layout
string EnKeys(string hebrew) => Layouts.Transliterate(hebrew, Layouts.He, Layouts.En);     // Hebrew keys on the English layout

// Fix one sentence word by word, the way the hook sees it (typed under current, meant under other).
string FixSentence(string typedSentence, Lang current)
{
    var from = Layouts.Hkl(current);
    var to = Layouts.Hkl(Layouts.OtherOf(current));
    return string.Join(" ", typedSentence.Split(' ').Select(w =>
        words.Fix(w, Layouts.Transliterate(w, from, to), current) ?? w));
}

// 1. A real mistake as reported (the leading w was lost in the paste), and other English typed on the Hebrew layout.
var reportedLine = "רןאןמע דםצקאיןמע ןמ יקנרק";
Check(FixSentence(reportedLine, Lang.He).Contains("something in"),
      $"reported line \"{reportedLine}\" -> \"{FixSentence(reportedLine, Lang.He)}\"");
foreach (var s in new[] { "writing something in hebrew", "please send me the report today", "what time is the meeting tomorrow" })
{
    var typed = HeKeys(s);
    var got = FixSentence(typed, Lang.He);
    Check(got == s, $"EN on HE layout \"{typed}\" -> \"{got}\"");
}

// 2. Hebrew typed on the English layout.
foreach (var s in new[] { "שלום מה שלומך היום", "תודה רבה על העזרה", "אני כותב משהו בעברית", "נדבר מחר בבוקר" })
{
    var typed = EnKeys(s);
    var got = FixSentence(typed, Lang.En);
    Check(got == s, $"HE on EN layout \"{typed}\" -> \"{got}\"");
}

// 3. Never touch correct text, names or code.
foreach (var s in new[] { "the quick brown fox jumps over the lazy dog", "hello world meeting tomorrow at noon",
                          "const useState async await npm git push origin main", "Dana Gmail Acme Media VS Code ok" })
{
    var got = FixSentence(s, Lang.En);
    Check(got == s, $"English stays English: \"{s}\"{(got == s ? "" : $" became \"{got}\"")}");
}
foreach (var s in new[] { "שלום לכולם", "מה קורה היום בערב", "אני אשלח לך את הדוח מחר" })
{
    var got = FixSentence(s, Lang.He);
    Check(got == s, $"Hebrew stays Hebrew: \"{s}\"{(got == s ? "" : $" became \"{got}\"")}");
}

// 4. Rates over the word lists: false flips on correct words must be rare.
List<string> Top(string file, int n, Func<string, bool> keep) => File.ReadLines(Path.Combine(dictDir, file), Encoding.UTF8)
    .Select(l => l.Split(' ')[0].ToLowerInvariant()).Where(keep).Take(n).ToList();
var enTop = Top("en_50k.txt", 5000, w => w.Length >= 2 && WordChecker.IsLatinWord(w) && !w.Contains('\''));
var heTop = Top("he_50k.txt", 5000, w => w.Length >= 2 && w.All(WordChecker.IsHebrewLetter));

int enFalse = enTop.Count(w => words.Fix(w, HeKeys(w), Lang.En) != null);
int heFalse = heTop.Count(w => words.Fix(w, EnKeys(w), Lang.He) != null);
int enFixed = enTop.Count(w => words.Fix(HeKeys(w), w, Lang.He) == w);
int heFixed = heTop.Count(w => words.Fix(EnKeys(w), w, Lang.En) == w);
Console.WriteLine($"      false flips: English {enFalse}/{enTop.Count}, Hebrew {heFalse}/{heTop.Count}");
Console.WriteLine($"      caught: English-on-Hebrew {enFixed}/{enTop.Count}, Hebrew-on-English {heFixed}/{heTop.Count}");
Check(enFalse <= enTop.Count / 100, $"under 1% of correct English words get flipped ({enFalse})");
Check(heFalse <= heTop.Count / 100, $"under 1% of correct Hebrew words get flipped ({heFalse})");
Check(enFixed >= enTop.Count * 0.8, $"at least 80% of English-on-Hebrew words caught ({enFixed})");
Check(heFixed >= heTop.Count * 0.6, $"at least 60% of Hebrew-on-English words caught ({heFixed})");
if (enFalse > 0) Console.WriteLine("      e.g. " + string.Join(", ", enTop.Where(w => words.Fix(w, HeKeys(w), Lang.En) != null).Take(12).Select(w => $"{w}->{HeKeys(w)}")));
if (heFalse > 0) Console.WriteLine("      e.g. " + string.Join(", ", heTop.Where(w => words.Fix(w, EnKeys(w), Lang.He) != null).Take(12).Select(w => $"{w}->{EnKeys(w)}")));

// 5. Text already in the field.
Check(Decider.ScriptOf("שלום, מה נשמע?") == Lang.He, "existing Hebrew text -> HE");
Check(Decider.ScriptOf("Hi Dana, thanks") == Lang.En, "existing English text -> EN");
Check(Decider.ScriptOf("ok") == null, "too little text -> no decision");

// 6. Field identity (bug 26-Oct: every Google Doc shared one key, and Chrome's repeated focus
//    events on the same field reset the typing guards and the word buffer).
FieldContext Doc(string title, string label = "Document content") => new()
{
    App = "chrome", Site = "docs.google.com", Title = title + " - Google Chrome", Kind = "Edit", Label = label, IsText = true,
};
var englishDoc = Doc("Client map - Acme Events - Google Docs");
var hebrewDoc = Doc("סיכום פגישה עם הלקוח - Google Docs");
Check(englishDoc.FieldKey != hebrewDoc.FieldKey, "two different Google Docs do not share one learned key");
Check(Doc("Client map - Acme Events - Google Docs").FieldKey == englishDoc.FieldKey, "the same doc keeps the same key");
var inbox3 = new FieldContext { App = "chrome", Site = "mail.google.com", Title = "Inbox (3) - someone@example.com - Gmail - Google Chrome", Kind = "Edit", Label = "Message Body" };
var inbox4 = new FieldContext { App = "chrome", Site = "mail.google.com", Title = "Inbox (4) - someone@example.com - Gmail - Google Chrome", Kind = "Edit", Label = "Message Body" };
Check(inbox3.FieldKey == inbox4.FieldKey, "an unread counter in the title does not make a new field");
var vs1 = new FieldContext { App = "Code", Title = "a.cs - keyflip - Visual Studio Code", Kind = "Edit", Label = "Message input" };
var vs2 = new FieldContext { App = "Code", Title = "b.cs - keyflip - Visual Studio Code", Kind = "Edit", Label = "Message input" };
Check(vs1.FieldKey == vs2.FieldKey, "desktop apps keep one key per field across window titles");

FieldContext Page(string site, string title) => new() { App = "chrome", Site = site, Title = title + " - Google Chrome", Kind = "Edit", Label = "x" };
Check(englishDoc.TitleHint() == Lang.En, "Google Doc with an English name -> EN from its name");
Check(hebrewDoc.TitleHint() == Lang.He, "Google Doc with a Hebrew name -> HE");
Check(Doc("Untitled document - Google Docs").TitleHint() == null, "Untitled document -> no hint");
Check(Page("web.whatsapp.com", "WhatsApp").TitleHint() == null, "WhatsApp title -> no hint");
Check(Page("mail.google.com", "Inbox (3) - someone@example.com - Gmail").TitleHint() == null, "Gmail inbox -> no hint");
Check(Page("mail.google.com", "Re: הצעת מחיר לאירוע - someone@example.com - Gmail").TitleHint() == Lang.He, "Gmail thread with a Hebrew subject -> HE");
Check(Page("github.com", "someone/keyflip: KeyFlip - GitHub").TitleHint() == null, "English title on a non-document site -> no hint");

Check(new ModelAnswer(Lang.En, 0.67, "english", 0, 0, OppositeProb: 0.01).Accept(0.70), "english 0.67 vs hebrew 0.01 is accepted");
Check(!new ModelAnswer(Lang.En, 0.60, "english", 0, 0, OppositeProb: 0.35).Accept(0.70), "english 0.60 vs hebrew 0.35 is not");
Check(!new ModelAnswer(null, 0.90, "unclear", 0, 0, OppositeProb: 0.0).Accept(0.70), "unclear is never accepted");

// 7. Optional live probe of Jev and Haiku.
if (args.Contains("probe"))
{
    var store = new Store();
    var jev = new JevClient();
    var haiku = new HaikuClient(store.Config.AnthropicApiKey);
    var samples = new (string name, object state)[]
    {
        ("ynet search", new { app = "chrome", site = "ynet.co.il", window_title = "ynet - חדשות, כלכלה, ספורט ובריאות", field_kind = "Edit", field_label = "חיפוש", field_hint = "", text_already_in_field = "" }),
        ("github search", new { app = "chrome", site = "github.com", window_title = "GitHub", field_kind = "ComboBox", field_label = "Search or jump to...", field_hint = "", text_already_in_field = "" }),
        ("gmail reply hebrew", new { app = "chrome", site = "mail.google.com", window_title = "Re: הצעת מחיר לאירוע - someone@example.com - Gmail", field_kind = "Edit", field_label = "Message Body", field_hint = "", text_already_in_field = "" }),
        ("vs code", new { app = "Code", site = "", window_title = "Decider.cs - keyflip - Visual Studio Code", field_kind = "Edit", field_label = "The editor is not accessible at this time.", field_hint = "", text_already_in_field = "" }),
        ("whatsapp chat", new { app = "chrome", site = "web.whatsapp.com", window_title = "WhatsApp", field_kind = "Edit", field_label = "Type a message to דני", field_hint = "", text_already_in_field = "" }),
        ("blank notepad", new { app = "Notepad", site = "", window_title = "Untitled - Notepad", field_kind = "Document", field_label = "Text editor", field_hint = "", text_already_in_field = "" }),
        ("doc english name", englishDoc.ToState()),
        ("doc hebrew name", hebrewDoc.ToState()),
        ("whatsapp hebrew chat", new FieldContext { App = "chrome", Site = "web.whatsapp.com", Title = "WhatsApp - Google Chrome", Kind = "Edit", Label = "Type a message to דני" }.ToState()),
    };
    double total = 0;
    foreach (var (name, state) in samples)
    {
        var j = jev.Available ? await jev.AskAsync(state, CancellationToken.None) : null;
        var h = haiku.Available ? await haiku.AskAsync(state, CancellationToken.None) : null;
        total += (j?.Usd ?? 0) + (h?.Usd ?? 0);
        Console.WriteLine($"PROBE {name,-20} jev={j?.Raw}/{j?.Confidence:0.00} vs {j?.OppositeProb:0.00} accept={j?.Accept(0.70)} {j?.LatencyMs}ms {j?.Error}  |  haiku={h?.Raw} {h?.LatencyMs}ms {h?.Error}");
    }
    Console.WriteLine($"PROBE total cost ${total:0.000000}");

    // Clicked-line reading on real screenshots. They hold private content, so they and their cases live
    // only on your machine: shots/cases.txt, one "file.png x y HEBREW|ENGLISH|UNSURE" per line.
    var shots = Path.Combine(AppContext.BaseDirectory, "shots");
    var casesFile = Path.Combine(shots, "cases.txt");
    var lines = !File.Exists(casesFile) ? [] : File.ReadAllLines(casesFile)
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length >= 4 && !p[0].StartsWith('#'))
        .Select(p => (file: p[0], x: int.Parse(p[1]), y: int.Parse(p[2]), expect: p[3])).ToList();
    if (lines.Count == 0) Console.WriteLine("SKIP  clicked-line cases: no shots/cases.txt");
    foreach (var (file, x, y, expect) in lines)
    {
        var path = Path.Combine(shots, file);
        if (!File.Exists(path)) { Console.WriteLine($"SKIP  {file} missing"); continue; }
        using var shot = new System.Drawing.Bitmap(path);
        var png = LineReader.StripFromImage(shot, new System.Drawing.Point(x, y))!;
        var a = await haiku.AskLineAsync(png, CancellationToken.None);
        Check(a.Raw.StartsWith(expect), $"line {file} ({x},{y}) -> {a.Raw} (want {expect}) {a.LatencyMs}ms ${a.Usd:0.000000} {a.Error}");
    }
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
