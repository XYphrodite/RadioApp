namespace RadioApp.Core.Models;

public sealed record StationTheme(
    string Id,
    string Name,
    double FrequencyMhz,
    string Icon,
    string AccentHex);

public static class StationThemes
{
    private static readonly Dictionary<string, StationTheme> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nightdrive FM"] = new("nightdrive", "Nightdrive FM", 104.5, "🌃", "#ff2a85"),
        ["Westside FM"]   = new("westside",   "Westside FM",   88.7,  "🌴", "#19e68c"),
        ["Velvet 98.3"]   = new("velvet",     "Velvet 98.3",   98.3,  "🎷", "#f4b961"),
        ["Thunder Rock"]  = new("thunder",    "Thunder Rock",  92.5,  "⚡", "#ff5c33"),
        ["Drift FM"]      = new("drift",      "Drift FM",      89.1,  "🏎️", "#b44cff"),
        ["Golden Era FM"] = new("goldenera",  "Golden Era FM", 90.3,  "📼", "#ffd166"),
        ["Static Radio"]  = new("static",     "Static Radio",  96.1,  "🎸", "#9ef01a"),
        ["Island Radio"]  = new("island",     "Island Radio",  91.2,  "🏝️", "#00d4b2"),
        ["Funk Zone"]     = new("funk",       "Funk Zone",     99.4,  "🪩", "#ff70a6"),
        ["Country Trail"] = new("country",    "Country Trail", 87.9,  "🤠", "#e08e45"),
        ["Soul Wave"]     = new("soul",       "Soul Wave",     103.9, "💜", "#c77dff"),
    };

    public static StationTheme Resolve(string name, string? rawFreq = null)
    {
        if (ByName.TryGetValue(name, out var theme))
            return theme;

        double.TryParse(
            rawFreq,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var f);

        return new StationTheme(
            name.ToLowerInvariant().Replace(' ', '-'),
            name,
            f is >= 87.5 and <= 108.0 ? f : 100.1,
            "📻",
            "#00d4ff");
    }
}

public sealed record CatalogStats(
    int TotalTracks,
    int TotalArtists,
    int TotalHours);

public sealed record HistoryEntry(
    RadioTrack Track,
    int MinutesAgo);

public sealed record StationLiveState(
    string Id,
    string Name,
    string Format,
    double FrequencyMhz,
    string Icon,
    string AccentHex,
    int TotalTrackCount,
    RadioTrack CurrentTrack,
    double ElapsedSec,
    double DurationSec,
    IReadOnlyList<HistoryEntry> RecentHistory,
    RadioTrack? NextTrack = null)
{
    public double ProgressPercent => DurationSec > 0
        ? Math.Clamp(ElapsedSec / DurationSec * 100.0, 0.0, 100.0)
        : 0.0;
}
