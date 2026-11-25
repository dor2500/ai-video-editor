using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIVideoEditor.AI;

/// <summary>A service that speaks the OpenAI chat-completions API with tool calling.</summary>
public sealed record OpenAiService(
    string Id, string Display, string BaseUrl, string KeyUrl, string FreeNote, string[] PreferredModels, bool OnlyFreeModels = false);

public static class Services
{
    /// <summary>Services with a free tier (no credit card), all OpenAI-compatible.</summary>
    public static readonly OpenAiService[] Free =
    [
        new("groq", "Groq", "https://api.groq.com/openai/v1", "https://console.groq.com/keys",
            "Free tier, very fast. Roughly 30 requests/minute and 1,000/day per model.",
            ["openai/gpt-oss-120b", "moonshotai/kimi-k2", "qwen/qwen3-32b", "llama-3.3-70b-versatile", "openai/gpt-oss-20b"]),
        new("openrouter", "OpenRouter (free models)", "https://openrouter.ai/api/v1", "https://openrouter.ai/settings/keys",
            "Only ':free' models are listed. About 50 requests/day (1,000/day after a one-time $10 top-up). Free models change often.",
            ["openai/gpt-oss-120b:free", "qwen/qwen3-coder:free", "deepseek/deepseek-chat-v3.1:free", "z-ai/glm-4.5-air:free",
             "meta-llama/llama-3.3-70b-instruct:free"], OnlyFreeModels: true),
        new("cerebras", "Cerebras", "https://api.cerebras.ai/v1", "https://cloud.cerebras.ai/",
            "Free tier with a daily token allowance; very fast.",
            ["gpt-oss-120b", "qwen-3-235b-a22b-instruct-2507", "zai-glm-4.6", "llama-3.3-70b"]),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "https://console.mistral.ai/api-keys",
            "Free 'Experiment' plan (phone verification); low rate limits. Prompts may be used for training on the free plan.",
            ["mistral-large-latest", "mistral-medium-latest", "mistral-small-latest"]),
    ];

    public static OpenAiService? Get(string id) => Free.FirstOrDefault(s => s.Id == id);
}

/// <summary>Tool-calling chat through any OpenAI-compatible endpoint (Groq, OpenRouter, Cerebras, Mistral, ...).</summary>
public sealed class OpenAiCompatProvider(OpenAiService service, string apiKey, string model, string? baseUrlOverride = null) : IAiProvider
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly JsonArray messages = [];
    string model = model;
    string Base => (baseUrlOverride ?? service.BaseUrl).TrimEnd('/');

    public string Display => service.Display;
    public void Reset() => messages.Clear();
    public void AddUser(string text) => messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

    public void AddToolResults(List<ToolOutput> results)
    {
        foreach (var r in results)
        {
            var text = r.Images.Count > 0
                ? r.Text + " (Note: this model cannot see images; choose photos by file name/date instead.)"
                : r.Text;
            var m = new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = text };
            if (service.Id == "mistral") m["name"] = r.Name;
            messages.Add(m);
        }
    }

    public async Task<AiTurn> NextAsync(string system, List<ToolDef> tools, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model))
            model = (await ListModels(service, apiKey, baseUrlOverride)).FirstOrDefault()
                    ?? throw new AiException($"{service.Display}: no tool-capable model found for this key", 404);
        var toolArr = new JsonArray();
        foreach (var t in tools)
            toolArr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object", ["properties"] = t.Properties.DeepClone(),
                        ["required"] = new JsonArray(t.Required.Select(x => (JsonNode)x).ToArray()),
                    },
                },
            });
        var all = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages) all.Add(m!.DeepClone());
        var body = new JsonObject
        {
            ["model"] = model, ["messages"] = all, ["tools"] = toolArr, ["max_tokens"] = 8000, ["temperature"] = 0.3,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Authorization", "Bearer " + apiKey);
        if (service.Id == "openrouter")
        {
            req.Headers.Add("HTTP-Referer", "https://github.com/dor2500/ai-video-editor");
            req.Headers.Add("X-Title", "AI Video Editor");
        }
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new AiException($"{service.Display} error {(int)resp.StatusCode}: {ErrorText(text)}", (int)resp.StatusCode);
        var root = JsonNode.Parse(text)!;
        if (root["error"] is JsonNode err) throw new AiException($"{service.Display}: {err["message"] ?? err}", 500);
        var choice = root["choices"]?[0] ?? throw new AiException($"{service.Display}: empty response", 500);
        var msg = choice["message"]!;
        var content = msg["content"]?.ToString() ?? "";
        var calls = new List<ToolCall>();
        var echoCalls = new JsonArray();
        int n = 0;
        foreach (var tc in msg["tool_calls"]?.AsArray() ?? [])
        {
            var fn = tc!["function"]!;
            var name = fn["name"]!.ToString();
            var id = tc["id"]?.ToString() is { Length: > 0 } tid ? tid : $"call{n++}{Guid.NewGuid().ToString("N")[..4]}";
            var argsText = fn["arguments"]?.ToString() ?? "{}";
            JsonObject args;
            try { args = (argsText.Trim() == "" ? new JsonObject() : JsonNode.Parse(argsText) as JsonObject) ?? []; }
            catch { args = new JsonObject { ["__invalid_json"] = argsText }; }
            calls.Add(new ToolCall(id, name, args));
            echoCalls.Add(new JsonObject
            {
                ["id"] = id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argsText },
            });
        }
        // echo only the standard fields (some services reject their own extra fields, e.g. "reasoning")
        var echo = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (echoCalls.Count > 0) echo["tool_calls"] = echoCalls;
        messages.Add(echo);
        var finish = choice["finish_reason"]?.ToString() ?? "";
        var stop = finish switch { "length" => "max_tokens", "content_filter" => "refusal", _ => "end_turn" };
        var texts = string.IsNullOrWhiteSpace(content) ? new List<string>() : [StripThink(content)];
        return new AiTurn(texts, calls, stop);
    }

    /// <summary>Some open models put their reasoning in &lt;think&gt; tags inside the answer.</summary>
    static string StripThink(string s) => Regex.Replace(s, @"<think>.*?</think>", "", RegexOptions.Singleline).Trim();

    static string ErrorText(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?["message"]?.ToString() ?? n?["message"]?.ToString() ?? n?["detail"]?.ToString() ?? body;
        }
        catch { return body.Length > 300 ? body[..300] : body; }
    }

    /// <summary>Models that support chat + tools, best first.</summary>
    public static async Task<List<string>> ListModels(OpenAiService s, string apiKey, string? baseUrlOverride = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, (baseUrlOverride ?? s.BaseUrl).TrimEnd('/') + "/models");
        req.Headers.Add("Authorization", "Bearer " + apiKey);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new AiException($"{s.Display}: {ErrorText(text)}", (int)resp.StatusCode);
        var data = JsonNode.Parse(text)?["data"]?.AsArray() ?? [];
        var ids = new List<string>();
        foreach (var m in data)
        {
            var id = m?["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            if (Regex.IsMatch(id, "whisper|tts|guard|embed|moderation|playai|orpheus|ocr|transcri|audio|image|vision-preview", RegexOptions.IgnoreCase)) continue;
            if (s.OnlyFreeModels && !id.EndsWith(":free")) continue;
            if (m?["supported_parameters"] is JsonArray sp && !sp.Any(x => x?.ToString() == "tools")) continue;
            if (m?["capabilities"]?["function_calling"]?.GetValue<bool>() == false) continue; // Mistral
            if (m?["active"] is JsonValue av && av.TryGetValue<bool>(out var act) && !act) continue; // Groq
            ids.Add(id);
        }
        return Rank(s, ids.Distinct().ToList());
    }

    /// <summary>Known-good models first, then bigger models (tool use needs a capable model), small/"nano" ones last.</summary>
    public static List<string> Rank(OpenAiService s, List<string> ids)
    {
        int Pref(string id)
        {
            for (int i = 0; i < s.PreferredModels.Length; i++)
                if (id.Equals(s.PreferredModels[i], StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
        static double Size(string id)
        {
            var sizes = Regex.Matches(id, @"(\d+(?:\.\d+)?)b(?![a-z])", RegexOptions.IgnoreCase)
                .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (sizes.Count > 0) return sizes.Max();
            if (Regex.IsMatch(id, "large|ultra|kimi|deepseek|glm|qwen3-coder", RegexOptions.IgnoreCase)) return 200;
            if (Regex.IsMatch(id, "nano|tiny|mini|lightning|small|lite", RegexOptions.IgnoreCase)) return 5;
            return 30; // unknown size: middle of the pack
        }
        return ids.OrderBy(Pref).ThenByDescending(Size).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
