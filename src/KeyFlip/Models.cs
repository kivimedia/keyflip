using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;

namespace KeyFlip;

public sealed record ModelAnswer(Lang? Lang, double Confidence, string Raw, long LatencyMs, double Usd, string? Error = null, double OppositeProb = 1)
{
    /// <summary>Jev's doubt is usually between a language and "unclear", not between the two languages
    /// (26-Oct: an English-named Google Doc came back english 0.67, hebrew 0.01). So a clear lead over the
    /// opposite language is accepted a little below the main threshold.</summary>
    public bool Accept(double threshold) =>
        Lang != null && (Confidence >= threshold || (Confidence >= 0.55 && OppositeProb <= 0.10));
}

/// <summary>Jev (TypeSafe System One): one Choice question per focus. Pinned version on purpose,
/// because the jev-latest alias moves with no notice and thresholds belong to one version.</summary>
public sealed class JevClient
{
    public const string ModelPin = "jev-1.13.0";
    private const string Endpoint = "https://api.typesafe.ai/v1/systemone";
    private const double UsdPerInputToken = 42.0 / 1_000_000_000; // output tokens are free

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly string? _key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")
                                   ?? Environment.GetEnvironmentVariable("TYPESAFE_API_KEY", EnvironmentVariableTarget.User);

    public bool Available => !string.IsNullOrEmpty(_key);

    public async Task<ModelAnswer> AskAsync(object state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var body = new
            {
                model = ModelPin,
                state,
                questions = new
                {
                    lang = new
                    {
                        type = "choice",
                        instructions = "The user (an Israeli who writes both Hebrew and English every day) just put the cursor in this text field. " +
                                       "Which language will they most likely type here? Judge from the app, the site, the page title, the field label and any text already in the field.",
                        criteria = new Dictionary<string, string>
                        {
                            ["hebrew"] = "They will type in Hebrew",
                            ["english"] = "They will type in English",
                            ["unclear"] = "Nothing in the context points to either language",
                        },
                    },
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            using var res = await Http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return new ModelAnswer(null, 0, "", sw.ElapsedMilliseconds, 0, $"http {(int)res.StatusCode}: {Trim(text)}");

            var json = JsonNode.Parse(text)!;
            var ans = json["answers"]!["lang"]!;
            var choice = ans["choice"]!.GetValue<string>();
            double prob = ans["probabilities"]?[choice]?.GetValue<double>() ?? 0;
            long tokens = json["usage"]?["input_tokens"]?.GetValue<long>() ?? 0;
            Lang? lang = choice switch { "hebrew" => Lang.He, "english" => Lang.En, _ => null };
            var opposite = choice == "hebrew" ? "english" : "hebrew";
            double oppositeProb = ans["probabilities"]?[opposite]?.GetValue<double>() ?? 1;
            return new ModelAnswer(lang, prob, choice, sw.ElapsedMilliseconds, tokens * UsdPerInputToken, OppositeProb: oppositeProb);
        }
        catch (Exception e)
        {
            return new ModelAnswer(null, 0, "", sw.ElapsedMilliseconds, 0, Trim(e.Message));
        }
    }

    internal static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}

/// <summary>Claude Haiku 5.5 as the second opinion, only when Jev was unsure and the context
/// has real text. Thinking off and low effort: this is a one-word decision.</summary>
public sealed class HaikuClient
{
    private const double UsdIn = 0.10 / 1_000_000, UsdOut = 0.50 / 1_000_000;
    private readonly AnthropicClient? _client;

    public HaikuClient(string? apiKey)
    {
        var key = string.IsNullOrWhiteSpace(apiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : apiKey;
        if (!string.IsNullOrWhiteSpace(key)) _client = new AnthropicClient { ApiKey = key };
    }

    public bool Available => _client != null;

    public async Task<ModelAnswer> AskAsync(object state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _client!.Messages.Create(new MessageCreateParams
            {
                Model = "claude-haiku-5-5",
                MaxTokens = 16,
                Thinking = new ThinkingConfigDisabled(),
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                System = "You pick a keyboard layout. The user is Israeli and writes both Hebrew and English. " +
                         "Given the context of the text field they just focused, answer with exactly one word: HEBREW, ENGLISH or UNSURE.",
                Messages = [new UserMessageParam { Content = JsonSerializer.Serialize(state) }],
            }, ct);
            var text = string.Concat(resp.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim().ToUpperInvariant();
            double usd = resp.Usage.InputTokens * UsdIn + resp.Usage.OutputTokens * UsdOut;
            Lang? lang = text.StartsWith("HEBREW") ? Lang.He : text.StartsWith("ENGLISH") ? Lang.En : null;
            return new ModelAnswer(lang, lang == null ? 0 : 1, text, sw.ElapsedMilliseconds, usd);
        }
        catch (Exception e)
        {
            return new ModelAnswer(null, 0, "", sw.ElapsedMilliseconds, 0, JevClient.Trim(e.Message));
        }
    }

    public const string LinePrompt =
        "This is a thin strip of the user's screen around where they just clicked to type. A red dot marks the click. " +
        "Look at the line of text at the same height as the dot (the dot may sit in the blank margin of that line). " +
        "Answer HEBREW or ENGLISH for the language of that line, or UNSURE if the dot's line is " +
        "not body text (a toolbar, menu, ruler, image) or is empty. Reply with that one word only, no explanation.";

    /// <summary>The answer word wherever it appears (Haiku sometimes explains first).</summary>
    internal static Lang? ParseLang(string text)
    {
        int he = text.IndexOf("HEBREW", StringComparison.Ordinal), en = text.IndexOf("ENGLISH", StringComparison.Ordinal);
        if (text.Contains("UNSURE") || (he >= 0 && en >= 0)) return null;
        return he >= 0 ? Lang.He : en >= 0 ? Lang.En : null;
    }

    /// <summary>Which language is the line the user clicked on (Google Docs draws text on a canvas).</summary>
    public async Task<ModelAnswer> AskLineAsync(byte[] png, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await _client!.Messages.Create(new MessageCreateParams
            {
                Model = "claude-haiku-5-5",
                MaxTokens = 16,
                Thinking = new ThinkingConfigDisabled(),
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                Messages =
                [
                    new UserMessageParam
                    {
                        Content = new List<ContentBlockParam>
                        {
                            new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng } },
                            new TextBlockParam { Text = LinePrompt },
                        },
                    },
                ],
            }, ct);
            var text = string.Concat(resp.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim().ToUpperInvariant();
            double usd = resp.Usage.InputTokens * UsdIn + resp.Usage.OutputTokens * UsdOut;
            Lang? lang = ParseLang(text);
            return new ModelAnswer(lang, lang == null ? 0 : 1, text, sw.ElapsedMilliseconds, usd);
        }
        catch (Exception e)
        {
            return new ModelAnswer(null, 0, "", sw.ElapsedMilliseconds, 0, JevClient.Trim(e.Message));
        }
    }
}
