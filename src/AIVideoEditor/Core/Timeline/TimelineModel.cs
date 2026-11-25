using System.Text.Json.Serialization;

namespace AIVideoEditor.Core.Timeline;

/// <summary>Editor-neutral timeline. Times are microseconds.</summary>
public sealed class TimelineDoc
{
    public string Name { get; set; } = "Untitled";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public List<TTrack> Tracks { get; set; } = [];

    [JsonIgnore] public long Duration => Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    public long Frames(long us) => (long)Math.Round(us / 1_000_000.0 * Fps);
    public long FromFrames(long frames) => (long)Math.Round(frames / Fps * 1_000_000.0);
    public int IntFps => (int)Math.Round(Fps);
}

public sealed class TTrack
{
    public string Type { get; set; } = "video"; // video | audio | text
    public string Name { get; set; } = "";
    public bool Muted { get; set; }
    public bool Hidden { get; set; }
    public List<TClip> Clips { get; set; } = [];
    /// <summary>Format-specific data kept for round-tripping (e.g. OpenShot layer json).</summary>
    public Dictionary<string, string> Extra { get; set; } = [];
}

public sealed class TClip
{
    public string Id { get; set; } = Ids.Short();
    public string Kind { get; set; } = "video"; // video | photo | audio | text | color
    public string Path { get; set; } = "";
    public long Start { get; set; }
    public long Dur { get; set; }
    /// <summary>Trim point inside the source media.</summary>
    public long In { get; set; }
    /// <summary>Full length of the source media (0 = unknown / still).</summary>
    public long SourceDur { get; set; }
    public int MediaWidth { get; set; }
    public int MediaHeight { get; set; }
    public bool HasAudio { get; set; }

    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    /// <summary>Position in half-canvas units (0,0 = center, y up), as in the AI tools.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }

    public long AudioFadeIn { get; set; }
    public long AudioFadeOut { get; set; }
    public long VideoFadeIn { get; set; }
    public long VideoFadeOut { get; set; }

    /// <summary>Transition into the NEXT clip on the same track.</summary>
    public string? Transition { get; set; }
    public long TransitionDur { get; set; }

    public string? Motion { get; set; }
    public double MotionStrength { get; set; } = 0.15;

    // text
    public string? Text { get; set; }
    public double FontSize { get; set; } = 8;
    public string Color { get; set; } = "#FFFFFF";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string? Font { get; set; }
    public string? StrokeColor { get; set; }
    public string? BackgroundColor { get; set; }
    public string Align { get; set; } = "center";
    public double StrokeWidth { get; set; } = 0.08;
    public double BackgroundAlpha { get; set; } = 0.6;
    public bool Shadow { get; set; }
    public double LetterSpacing { get; set; }
    public double LineSpacing { get; set; } = 0.02;

    // video / canvas / crop / speed
    public bool Reverse { get; set; }
    public bool KeepPitch { get; set; }
    public string BlendMode { get; set; } = "normal";
    public double CanvasBlur { get; set; }
    public string? CanvasColor { get; set; }
    public double CropLeft { get; set; }
    public double CropRight { get; set; }
    public double CropTop { get; set; }
    public double CropBottom { get; set; }
    public double CornerRadius { get; set; }

    // color adjustments (-1..1 normalized or degrees for Hue)
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public double Temperature { get; set; }
    public double Hue { get; set; }
    public double Sharpen { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Vignette { get; set; }
    public double ColorFade { get; set; }

    // audio processing
    public double AudioPan { get; set; }
    public bool NoiseReduction { get; set; }
    public string VocalIsolation { get; set; } = "none"; // none | keep_vocals | remove_vocals

    public List<TKeyframe> Keyframes { get; set; } = [];

    public Dictionary<string, string> Extra { get; set; } = [];

    [JsonIgnore] public long End => Start + Dur;
    [JsonIgnore] public string FileName => string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetFileName(Path);
}

public sealed class TKeyframe
{
    public long TimeUs { get; set; }
    public double? Scale { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Rotation { get; set; }
    public double? Opacity { get; set; }
}

/// <summary>Transitions every target editor understands.</summary>
public static class GenericCatalog
{
    public static readonly (string Name, string Desc)[] Transitions =
    [
        ("Cross Dissolve", "soft blend between clips (fade)"),
        ("Dip to Black", "fade out to black then in"),
        ("Dip to White", "flash to white"),
        ("Wipe", "wipe left to right"),
    ];

    public static readonly (string Name, string Desc)[] Animations =
    [
        ("Fade In", "in: opacity 0 → 1"),
        ("Fade Out", "out: opacity 1 → 0"),
    ];

    public static string Normalize(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.Contains("black")) return "Dip to Black";
        if (n.Contains("white") || n.Contains("flash")) return "Dip to White";
        if (n.Contains("wipe") || n.Contains("slide")) return "Wipe";
        return "Cross Dissolve";
    }
}
