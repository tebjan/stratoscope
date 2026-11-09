namespace Stratoscope.Core;

/// <summary>How well a request used the prompt cache. Drives the colour of the progress bar and the dots.</summary>
public enum CacheHit
{
    Hit,
    Partial,
    Miss,
    /// <summary>The prompt is too short for the cache to matter.</summary>
    Small,
}

public static class CacheHits
{
    public static string Label(this CacheHit hit)
    {
        switch (hit)
        {
            case CacheHit.Hit: return "Hit";
            case CacheHit.Partial: return "Partial";
            case CacheHit.Miss: return "Miss";
            default: return "Small prompt";
        }
    }

    /// <summary>
    /// hit: at least 90 % of the prompt came from the cache; partial: less, but at least 8,192 tokens were reused;
    /// miss: anything else; small: the prompt is under 4,096 tokens.
    /// </summary>
    public static CacheHit Classify(double prompt, double reused)
    {
        if (prompt < 4096) return CacheHit.Small;
        if (reused >= 0.9 * prompt) return CacheHit.Hit;
        return reused >= 8192 ? CacheHit.Partial : CacheHit.Miss;
    }

    /// <summary>Classifies a request, or returns null when the engine did not report both numbers.</summary>
    public static CacheHit? Classify(double? prompt, double? reused) =>
        prompt is null || reused is null ? (CacheHit?)null : Classify(prompt.Value, reused.Value);
}
