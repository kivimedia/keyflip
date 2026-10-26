# KeyFlip

**Stop typing English on the Hebrew keyboard (and the other way round).**

KeyFlip is a small Windows tray app for people who type in both Hebrew and English. Windows keeps a
separate keyboard layout per window, so switching to another app or clicking into a text box often
leaves you on the wrong one, and you get `רןאןמע דםצקאיןמע` instead of "writing something".

KeyFlip picks the right layout for each text field before you type, and fixes a word if you still
typed it on the wrong layout.

## What it does

**When you focus a text field** (switch window, click into a box), it decides in this order, stopping
at the first answer:

1. What it already learned for this field (your manual switches teach it).
2. Text already in the field, if it is clearly in one script.
3. The page title, when it was clearly written by a person (a Hebrew title anywhere, or an English
   document name on Google Docs, Notion and the like).
4. [Jev](https://typesafe.ai) (TypeSafe System One), a fast classifier, from the app, site, page title
   and field label.
5. The language you last typed in on that app or site.
6. Claude Haiku as a second opinion, only when there is text to read.
7. Otherwise it leaves your layout alone.

It never switches after you have started typing in that field, or after you switched by hand.

**When you click inside a document** (Google Docs, Notion, Word), documents mix languages paragraph by
paragraph, and Google Docs draws its text on a canvas that Windows cannot read. So KeyFlip captures a
thin strip of the screen around the click and asks Claude Haiku which language that line is in.

**While you type**, when a word ends it checks it against offline English and Hebrew word lists. If
the word is gibberish on the current layout but a real word on the other one, it retypes it and
switches. Press Backspace right after a fix to undo it; that word is then never fixed again.

The tray icon shows **HE** or **EN**. Left-click pauses. Right-click shows today's and this month's
cost, toggles the word fixer and "start with Windows", and opens the log.

## Requirements

- Windows 10 or 11 with both a Hebrew and an English keyboard layout installed.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build.
- Optional API keys (KeyFlip works without them, using only the free local steps):
  - `TYPESAFE_API_KEY` environment variable for Jev.
  - An Anthropic API key, for Haiku: set `ANTHROPIC_API_KEY`, or put it in `config.json` (below).

## Install

```
git clone https://github.com/kivimedia/keyflip
cd keyflip
dotnet run --project tests/KeyFlip.Tests
dotnet publish src/KeyFlip -c Release -o "%LOCALAPPDATA%\Programs\KeyFlip"
"%LOCALAPPDATA%\Programs\KeyFlip\KeyFlip.exe"
```

Then right-click the tray icon and tick **Start with Windows**.

## Settings and data

Everything lives in `%LOCALAPPDATA%\KeyFlip`:

- `config.json`: `AnthropicApiKey`, `JevThreshold` (default 0.70), `FixerEnabled`, `Paused`,
  `ExcludedApps` and `FixerExcludedApps` (process names, terminals excluded from the fixer by default).
- `learned.json`: what it learned per field and per site.
- `log.jsonl`: one line per decision, with which step decided, the latency and the cost.

## Privacy: what leaves your PC

- **Keystrokes never leave your PC.** The word fixer works offline.
- **Jev and Haiku (text)** receive the app name, site, page title, field label and at most 300
  characters already in the field. Password fields are skipped entirely.
- **Haiku (image)** receives a strip of the screen about 96 pixels tall around your click, only for
  clicks inside a document.
- Leave both keys unset to keep everything local.

## Cost

Measured in daily use: about **$0.30 a month**. Around 4 in 5 decisions are free (learned, local text
or page title). A Jev call costs about $0.00002 and answers in about 0.2 s. A Haiku call costs about
$0.00002 to $0.00004 and takes about 1 s.

## Tests

```
dotnet run --project tests/KeyFlip.Tests            # offline: layouts, word fixer, field identity
dotnet run --project tests/KeyFlip.Tests -- probe   # also calls Jev and Haiku (fractions of a cent)
```

On 5,000 common words per language, no correctly typed word is flipped, and about 95% of words typed
on the wrong layout are caught. For the clicked-line test, put your own screenshots in
`tests/KeyFlip.Tests/shots/` with a `cases.txt` (`file.png x y HEBREW|ENGLISH|UNSURE` per line); that
folder is gitignored.

## Credits

Word lists: [FrequencyWords](https://github.com/hermitdave/FrequencyWords) by Hermit Dave, built from
OpenSubtitles, licensed [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). See
`src/KeyFlip/dict/NOTICE.md`.

## License

MIT, see [LICENSE](LICENSE). The word lists keep their own CC BY-SA 4.0 license.
