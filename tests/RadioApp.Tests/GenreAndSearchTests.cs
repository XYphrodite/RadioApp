using RadioApp.Core.Models;

namespace RadioApp.Tests;

public sealed class GenreAndSearchTests
{
    [Fact]
    public void HipHopPreset_MatchesDiscogsHipHopWithSpace()
    {
        var track = new RadioTrack { Genre = "Hip Hop / Gangsta", Station = "Westside FM" };
        Assert.True(GenreCatalog.MatchesPreset(track, "hiphop"));
        Assert.True(GenreCatalog.MatchesPreset(track, "Hip-Hop"));
    }

    [Fact]
    public void PhonkPreset_MatchesDriftFmStationAndSubgenres()
    {
        var driftTrack = new RadioTrack { Genre = "Electronic / Ambient", Station = "Drift FM" };
        var horrorcoreTrack = new RadioTrack { Genre = "Hip Hop / Horrorcore", Station = "Westside FM" };
        var rockTrack = new RadioTrack { Genre = "Rock / Hard Rock", Station = "Thunder Rock" };

        Assert.True(GenreCatalog.MatchesPreset(driftTrack, "phonk"));
        Assert.True(GenreCatalog.MatchesPreset(horrorcoreTrack, "phonk"));
        Assert.False(GenreCatalog.MatchesPreset(rockTrack, "phonk"));
    }

    [Fact]
    public void FormatDisplayGenre_NormalizesVerboseDiscogsNames()
    {
        Assert.Equal("Hip-Hop / R&B", GenreCatalog.FormatDisplayGenre("Hip Hop / RnB/Swing"));
        Assert.Equal("Folk & Country / Folk", GenreCatalog.FormatDisplayGenre("Folk, World, & Country / Folk"));
        Assert.Equal("Funk & Soul / Disco", GenreCatalog.FormatDisplayGenre("Funk / Soul / Disco"));
        Assert.Equal("Electronic / Synthwave", GenreCatalog.FormatDisplayGenre("Electronic / Synthwave"));
    }

    [Fact]
    public void SearchNormalizer_SupportsTranslitAndRussianGenreAliases()
    {
        var kino = new RadioTrack { Artist = "Кино", Filename = "01 - Группа крови.mp3", Genre = "Rock / Post-Punk", Station = "Static Radio" };
        var jazz = new RadioTrack { Artist = "Miles Davis", Filename = "01 - So What.flac", Genre = "Jazz / Hard Bop", Station = "Velvet 98.3" };
        var phonk = new RadioTrack { Artist = "Kordhell", Filename = "Murder In My Mind.mp3", Genre = "Electronic / Techno", Station = "Drift FM" };

        Assert.True(SearchNormalizer.MatchesFuzzy(kino, "kino"));
        Assert.True(SearchNormalizer.MatchesFuzzy(kino, "рок"));
        Assert.True(SearchNormalizer.MatchesFuzzy(jazz, "джаз"));
        Assert.True(SearchNormalizer.MatchesFuzzy(phonk, "фонк"));
        Assert.False(SearchNormalizer.MatchesFuzzy(jazz, "метал"));
    }
}
