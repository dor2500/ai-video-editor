using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVideoEditor.Core;

/// <summary>App settings in %APPDATA%\AIVideoEditor\settings.json. API keys are encrypted with Windows DPAPI (current user).</summary>
public sealed class Settings
{
    public string Provider { get; set; } = "claude"; // claude | gemini
    public string ClaudeKeyEnc { get; set; } = "";
    public string GeminiKeyEnc { get; set; } = "";
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    public string GeminiModel { get; set; } = "";
    public string Editor { get; set; } = "capcut";
    public string LastProject { get; set; } = "";
    public List<string> RecentFiles { get; set; } = [];

    static string FilePath => Path.Combine(Paths.AppData, "settings.json");
    static Settings? current;
    public static Settings Current => current ??= Load();

    static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.AppData);
        Json.SaveAtomic(FilePath, JsonSerializer.Serialize(this, Json.Pretty));
    }

    static string Protect(string s) => s == "" ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string s)
    {
        if (s == "") return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    // never serialized: only the DPAPI-encrypted values above go to disk
    [JsonIgnore]
    public string ClaudeKey
    {
        get => Unprotect(ClaudeKeyEnc) is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        set => ClaudeKeyEnc = Protect(value.Trim());
    }

    [JsonIgnore]
    public string GeminiKey
    {
        get => Unprotect(GeminiKeyEnc) is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        set => GeminiKeyEnc = Protect(value.Trim());
    }

    public bool HasKeyFor(string provider) => provider == "gemini" ? GeminiKey != "" : ClaudeKey != "";

    public void AddRecent(string entry)
    {
        RecentFiles.Remove(entry);
        RecentFiles.Insert(0, entry);
        if (RecentFiles.Count > 50) RecentFiles.RemoveRange(50, RecentFiles.Count - 50);
        Save();
    }
}
