namespace RadioApp.Core.Models;

public sealed record GenreFilterPreset(
    string Id,
    string Label,
    string Icon,
    Func<RadioTrack, bool> Matcher);

/// <summary>
/// Curated genre filter presets calibrated against Discogs EffNet labels and station formats.
/// </summary>
public static class GenreCatalog
{
    public static readonly IReadOnlyList<GenreFilterPreset> Presets =
    [
        new("hiphop", "Hip-Hop", "🎤", static t => ContainsAny(t.Genre,
            "hip hop", "hip-hop", "hiphop", "rap", "gangsta", "boom bap", "g-funk", "crunk", "grime")),
        new("synthwave", "Synthwave", "🌃", static t => ContainsAny(t.Genre,
            "synthwave", "synth-pop", "new beat", "chiptune")),
        new("rock", "Rock", "🎸", static t => ContainsAny(t.Genre, "rock")),
        new("phonk", "Phonk", "🏎️", static t =>
            string.Equals(t.Station, "Drift FM", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(t.Genre, "phonk", "memphis", "horrorcore", "cloud rap")),
        new("jazz", "Jazz / Soul", "🎷", static t => ContainsAny(t.Genre,
            "jazz", "soul", "funk", "rnb", "r&b", "disco", "blues")),
        new("electronic", "Electronic", "⚡", static t => ContainsAny(t.Genre, "electronic")),
        new("ambient", "Ambient / Chill", "🌊", static t => ContainsAny(t.Genre,
            "ambient", "downtempo", "chill", "new age", "lounge", "classical", "soundtrack")),
        new("metal", "Metal / Punk", "🤘", static t => ContainsAny(t.Genre,
            "metal", "punk", "hardcore", "goregrind", "grunge")),
        new("pop", "Pop", "🌟", static t => ContainsAny(t.Genre, "pop")),
        new("reggae", "Reggae / Folk", "🏝️", static t => ContainsAny(t.Genre,
            "reggae", "dub", "ragga", "dancehall", "latin", "salsa", "country", "folk")),
    ];

    private static readonly Dictionary<string, GenreFilterPreset> ById =
        Presets.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static bool MatchesPreset(RadioTrack track, string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId))
            return true;

        if (ById.TryGetValue(presetId, out var preset))
            return preset.Matcher(track);

        var byLabel = Presets.FirstOrDefault(p =>
            string.Equals(p.Label, presetId, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null)
            return byLabel.Matcher(track);

        return track.Genre.Contains(presetId, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatDisplayGenre(string? rawGenre)
    {
        if (string.IsNullOrWhiteSpace(rawGenre))
            return "";

        var s = rawGenre.Trim();
        if (s.StartsWith("Folk, World, & Country", StringComparison.OrdinalIgnoreCase))
            s = "Folk & Country" + s["Folk, World, & Country".Length..];

        if (s.StartsWith("Hip Hop", StringComparison.OrdinalIgnoreCase))
            s = "Hip-Hop" + s["Hip Hop".Length..];

        if (s.StartsWith("Funk / Soul", StringComparison.OrdinalIgnoreCase))
            s = "Funk & Soul" + s["Funk / Soul".Length..];

        s = s.Replace("RnB/Swing", "R&B", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static bool ContainsAny(string? haystack, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(haystack))
            return false;

        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
