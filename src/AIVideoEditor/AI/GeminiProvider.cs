using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIVideoEditor.Core;

namespace AIVideoEditor.AI;

/// <summary>
/// Google Gemini through the Generative Language REST API (function calling).
/// Model turns are appended exactly as returned so thought signatures round-trip.
/// </summary>
public sealed class GeminiProvider(string apiKey, string model) : IAiProvider
{
    string model = model;
    const string Base = "https://generativelanguage.googleapis.com/v1beta/";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly JsonArray contents = [];

    public string Display => "Gemini";
    public void Reset() => contents.Clear();

    public void AddUser(string text) =>
        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) });

    public void AddToolResults(List<ToolOutput> results)
    {
        var parts = new JsonArray();
        foreach (var r in results)
        {
            JsonNode? payload;
            try { payload = r.IsError ? null : JsonNode.Parse(r.Text); } catch { payload = null; }
            var fr = new JsonObject
            {
                ["name"] = r.Name,
                ["response"] = r.IsError ? new JsonObject { ["error"] = r.Text } : new JsonObject { ["result"] = payload ?? r.Text },
            };
            if (!r.CallId.StartsWith("gem-")) fr["id"] = r.CallId;
            parts.Add(new JsonObject { ["functionResponse"] = fr });
        }
        foreach (var r in results)
            foreach (var (label, jpg) in r.Images)
            {
                parts.Add(new JsonObject { ["text"] = label });
                parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpg) } });
            }
        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
    }

    public async Task<AiTurn> NextAsync(string system, List<ToolDef> tools, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model))
            model = (await ListModels(apiKey)).FirstOrDefault() ?? throw new AiException("Gemini: no usable model found for this key", 404);
        var decls = new JsonArray();
        foreach (var t in tools)
            decls.Add(new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description,
                ["parametersJsonSchema"] = new JsonObject
                {
                    ["type"] = "object", ["properties"] = t.Properties.DeepClone(),
                    ["required"] = new JsonArray(t.Required.Select(r => (JsonNode)r).ToArray()),
                },
            });
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = contents.DeepClone(),
            ["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = decls }),
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } },
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 16000 },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}models/{model}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", apiKey);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text;
            throw new AiException($"Gemini error {(int)resp.StatusCode}: {msg}", (int)resp.StatusCode);
        }
        var root = JsonNode.Parse(text)!;
        var cand = root["candidates"]?[0];
        if (cand == null)
        {
            var reason = root["promptFeedback"]?["blockReason"]?.ToString();
            return new AiTurn([], [], reason != null ? "refusal" : "end_turn");
        }
        var content = cand["content"]?.DeepClone() as JsonObject ?? new JsonObject { ["role"] = "model", ["parts"] = new JsonArray() };
        content["role"] = "model";
        if (content["parts"] is JsonArray ps && ps.Count > 0) contents.Add(content);
        var texts = new List<string>();
        var calls = new List<ToolCall>();
        int n = 0;
        foreach (var p in content["parts"]?.AsArray() ?? [])
        {
            if (p?["thought"]?.GetValue<bool>() == true) continue;
            if (p?["text"]?.ToString() is { Length: > 0 } t) texts.Add(t);
            if (p?["functionCall"] is JsonObject fc)
            {
                var id = fc["id"]?.ToString() ?? $"gem-{n++}";
                calls.Add(new ToolCall(id, fc["name"]!.ToString(), fc["args"]?.DeepClone() as JsonObject ?? []));
            }
        }
        var finish = cand["finishReason"]?.ToString() ?? "";
        var stop = finish switch { "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" => "refusal", "MAX_TOKENS" => "max_tokens", _ => "end_turn" };
        return new AiTurn(texts, calls, stop);
    }

    /// <summary>Gemini models that support generateContent, best first.</summary>
    public static async Task<List<string>> ListModels(string apiKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}models?pageSize=200");
        req.Headers.Add("x-goog-api-key", apiKey);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new AiException("Gemini: " + (JsonNode.Parse(text)?["error"]?["message"]?.ToString() ?? text), (int)resp.StatusCode);
        var names = (JsonNode.Parse(text)?["models"]?.AsArray() ?? [])
            .Where(m => m?["supportedGenerationMethods"]?.AsArray().Any(x => x?.ToString() == "generateContent") == true)
            .Select(m => m!["name"]!.ToString().Replace("models/", ""))
            .Where(n => n.StartsWith("gemini") && !Regex.IsMatch(n, "tts|image|embedding|audio|live|robotics|computer"))
            .ToList();
        static (double ver, int tier, int stable) Rank(string n)
        {
            var m = Regex.Match(n, @"gemini-(\d+(?:\.\d+)?)");
            double v = m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            // Flash first: on the free tier Pro models get no requests
            int tier = n.Contains("flash") && !n.Contains("lite") ? 3 : n.Contains("pro") ? 2 : 1;
            return (v, tier, n.Contains("preview") || n.Contains("exp") ? 0 : 1);
        }
        return names.OrderByDescending(n => Rank(n).tier).ThenByDescending(n => Rank(n).stable).ThenByDescending(n => Rank(n).ver).ToList();
    }
}

public sealed class AiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}
