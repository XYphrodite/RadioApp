using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RadioApp.Core.Models;

public sealed partial class RadioCatalog
{
    [JsonPropertyName("library")]
    public string Library { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("tagged")]
    public int Tagged { get; set; }

    [JsonPropertyName("stations")]
    public Dictionary<string, StationCatalogEntry> Stations { get; set; } = new();
}

public sealed class StationCatalogEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("frequency")]
    public string Frequency { get; set; } = "";

    [JsonPropertyName("format")]
    public string Format { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("tracks")]
    public List<RadioTrack> Tracks { get; set; } = new();
}

public sealed partial class RadioTrack
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Path { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = "";

    [JsonPropertyName("album")]
    public string Album { get; set; } = "";

    [JsonPropertyName("genre")]
    public string Genre { get; set; } = "";

    [JsonPropertyName("station")]
    public string Station { get; set; } = "";

    [JsonPropertyName("bpm")]
    public double? Bpm { get; set; }

    [JsonPropertyName("energy")]
    public double? Energy { get; set; }

    [JsonPropertyName("duration_sec")]
    public double DurationSec { get; set; }

    [JsonIgnore]
    public double EffectiveDurationSec => DurationSec >= 5.0 ? DurationSec : 180.0;

    [JsonIgnore]
    public string DisplayTitle => field ??= ExtractCleanTitle(Filename, Artist);

    [JsonIgnore]
    public string CodecBadge => field ??= GetCodecBadge(Filename, Album);

    [JsonIgnore]
    public bool IsAlac =>
        Filename.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) &&
        Album.Contains("[ALAC]", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public int EnergyPercent => (int)Math.Clamp(Math.Round((Energy ?? 0.65) * 100.0), 1, 100);

    [JsonIgnore]
    public int BpmRounded => (int)Math.Clamp(Math.Round(Bpm is > 30 ? Bpm.Value : 110), 40, 220);

    public static string ExtractCleanTitle(string filename, string artist)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return "Unknown Track";

        var stem = System.IO.Path.GetFileNameWithoutExtension(filename)
            .Replace('_', ' ')
            .Trim();

        // Strip leading disc/track numbers like "01. ", "01 - ", "1-02 ", "01 "
        stem = LeadingTrackNumberRegex().Replace(stem, "").Trim();

        // Strip "Artist - Title" prefix if it matches or starts with artist
        var dashIdx = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0 && !string.IsNullOrWhiteSpace(artist))
        {
            var normalizedArtist = artist.Replace('_', ' ').Trim();
            var prefix = stem[..dashIdx].Trim();
            if (prefix.StartsWith(normalizedArtist, StringComparison.OrdinalIgnoreCase) ||
                normalizedArtist.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[(dashIdx + 3)..].Trim();
                stem = LeadingTrackNumberRegex().Replace(stem, "").Trim();
            }
        }

        // Remove common site watermark suffixes like (SkySound.cc), (zaycev.net), [muzofon.com]
        stem = SiteWatermarkRegex().Replace(stem, "").Trim();

        return string.IsNullOrWhiteSpace(stem) ? filename : stem;
    }

    private static string GetCodecBadge(string filename, string album)
    {
        var ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
        if (album.Contains("[24-96]", StringComparison.OrdinalIgnoreCase) ||
            album.Contains("24/96", StringComparison.OrdinalIgnoreCase) ||
            album.Contains("[24-", StringComparison.OrdinalIgnoreCase))
            return "FLAC 24/96";
        if (album.Contains("[ALAC]", StringComparison.OrdinalIgnoreCase))
            return "ALAC LOSSLESS";
        return ext switch
        {
            ".flac" => "FLAC LOSSLESS",
            ".m4a" => "AAC / M4A",
            ".ogg" or ".opus" => "OGG VORBIS",
            ".wav" => "WAV PCM",
            ".mp3" => "MP3",
            _ => string.IsNullOrEmpty(ext) ? "AUDIO" : ext.TrimStart('.').ToUpperInvariant()
        };
    }

    [GeneratedRegex(@"^(?:(?:cd|disc)\s*\d+\s*[-.]?\s*)?(?:\d{1,2}-)?\d{1,3}(?:\s*[.-]\s*|\s+)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingTrackNumberRegex();

    [GeneratedRegex(@"\s*[\(\[](?:skysound\.cc|zaycev\.net|muzofon\.[a-z]+|mp3party\.[a-z]+|promodj\.com)[\)\]]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteWatermarkRegex();
}

public static class SearchNormalizer
{
    private static readonly Dictionary<char, string> RuToEn = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) || c == '-' || c == '/' || c == '_' || c == '.')
                sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    public static string TransliterateRuToEn(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (char c in input.ToLowerInvariant())
        {
            if (RuToEn.TryGetValue(c, out var repl))
                sb.Append(repl);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool MatchesFuzzy(RadioTrack track, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return true;

        var titleNorm = Normalize(track.DisplayTitle);
        var artistNorm = Normalize(track.Artist);
        var albumNorm = Normalize(track.Album);

        var titleTranslit = TransliterateRuToEn(titleNorm);
        var artistTranslit = TransliterateRuToEn(artistNorm);
        var albumTranslit = TransliterateRuToEn(albumNorm);

        foreach (var w in words)
        {
            var wNorm = Normalize(w);
            if (string.IsNullOrEmpty(wNorm)) continue;
            var wTranslit = TransliterateRuToEn(wNorm);

            bool matches =
                titleNorm.Contains(wNorm, StringComparison.OrdinalIgnoreCase) ||
                artistNorm.Contains(wNorm, StringComparison.OrdinalIgnoreCase) ||
                albumNorm.Contains(wNorm, StringComparison.OrdinalIgnoreCase) ||
                titleTranslit.Contains(wTranslit, StringComparison.OrdinalIgnoreCase) ||
                artistTranslit.Contains(wTranslit, StringComparison.OrdinalIgnoreCase) ||
                albumTranslit.Contains(wTranslit, StringComparison.OrdinalIgnoreCase);

            if (!matches) return false;
        }

        return true;
    }
}
