using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace AIVideoEditor.AI;

/// <summary>Claude through the official Anthropic C# SDK (Messages API, manual tool loop).</summary>
public sealed class ClaudeProvider(string apiKey, string model) : IAiProvider
{
    readonly AnthropicClient client = new() { ApiKey = apiKey };
    readonly List<MessageParam> messages = [];

    public string Display => "Claude";
    public void Reset() => messages.Clear();
    public void AddUser(string text) => messages.Add(new UserMessageParam { Content = text });

    public void AddToolResults(List<ToolOutput> results)
    {
        List<ContentBlockParam> blocks = [];
        foreach (var r in results)
        {
            if (r.Images.Count == 0)
            {
                blocks.Add(new ToolResultBlockParam { ToolUseID = r.CallId, Content = r.Text, IsError = r.IsError });
                continue;
            }
            List<Block> parts = [new TextBlockParam { Text = r.Text }];
            foreach (var (label, jpg) in r.Images)
            {
                parts.Add(new TextBlockParam { Text = label });
                parts.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(jpg), MediaType = MediaType.ImageJpeg } });
            }
            blocks.Add(new ToolResultBlockParam { ToolUseID = r.CallId, Content = parts });
        }
        messages.Add(new UserMessageParam { Content = blocks });
    }

    public async Task<AiTurn> NextAsync(string system, List<ToolDef> tools, CancellationToken ct)
    {
        List<ToolUnion> toolList = [];
        foreach (var t in tools)
            toolList.Add(new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new()
                {
                    Properties = t.Properties.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                    Required = t.Required.ToList(),
                },
            });
        var resp = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = new List<TextBlockParam> { new() { Text = system, CacheControl = new CacheControlEphemeral() } },
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.Medium },
            Tools = toolList,
            Messages = messages.ToList(),
        }, ct);

        List<ContentBlockParam> echo = [];
        var texts = new List<string>();
        var calls = new List<ToolCall>();
        foreach (var block in resp.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                echo.Add(new TextBlockParam { Text = text.Text });
                texts.Add(text.Text);
            }
            else if (block.TryPickThinking(out ThinkingBlock? th))
                echo.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt))
                echo.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
            {
                echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                var args = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(tu.Input))?.AsObject() ?? [];
                calls.Add(new ToolCall(tu.ID, tu.Name, args));
            }
        }
        if (echo.Count > 0) messages.Add(new AssistantMessageParam { Content = echo });
        var stop = resp.StopReason?.ToString() ?? "";
        if (stop.Contains("refusal")) stop = "refusal";
        else if (stop.Contains("max_tokens")) stop = "max_tokens";
        // a cut-off tool call cannot be answered; drop it so the history stays valid
        if (stop == "max_tokens" && calls.Count > 0)
        {
            messages.RemoveAt(messages.Count - 1);
            calls.Clear();
        }
        return new AiTurn(texts, calls, stop);
    }
}
