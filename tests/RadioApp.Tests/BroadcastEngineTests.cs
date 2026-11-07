using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RadioApp.Core.Models;
using RadioApp.Core.Services;

namespace RadioApp.Tests;

public sealed class BroadcastEngineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _stateFile;

    public BroadcastEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RadioAppTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _stateFile = Path.Combine(_tempDir, "user_state.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Theory]
    [InlineData("01 - Future Club.flac", "Perturbator", "Future Club")]
    [InlineData("04. Perturbator - Humans Are Such Easy Prey.flac", "Perturbator", "Humans Are Such Easy Prey")]
    [InlineData("aceyalone_ft._bionik_-_i_can_get_it_myself_(SkySound.cc).mp3", "aceyalone ft. bionik", "i can get it myself")]
    [InlineData("1-02 Ambitionz Az A Ridah.m4a", "2Pac", "Ambitionz Az A Ridah")]
    public void ExtractCleanTitle_StripsNumbersArtistAndWatermarks(string filename, string artist, string expected)
    {
        var actual = RadioTrack.ExtractCleanTitle(filename, artist);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LiveTimeline_AdvancesSimultaneouslyAcrossAllStations()
    {
        var store = new UserRadioStore(_stateFile);
        var engine = new RadioBroadcastEngine(store);
        engine.LoadCatalog(CreateSampleCatalog());

        var t0 = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddSeconds(25);

        var night0 = engine.GetStationSnapshot("Nightdrive FM", t0)!;
        var west0 = engine.GetStationSnapshot("Westside FM", t0)!;

        // Simulate listener switching to Westside FM and coming back 25 seconds later
        engine.SetActiveStation("Westside FM");
        var west1 = engine.GetStationSnapshot("Westside FM", t1)!;
        var night1 = engine.GetStationSnapshot("Nightdrive FM", t1)!;

        // If still on same track, elapsed must have advanced by exactly 25s
        if (night0.CurrentTrack.Id == night1.CurrentTrack.Id)
        {
            Assert.Equal(night0.ElapsedSec + 25.0, night1.ElapsedSec, precision: 2);
        }

        if (west0.CurrentTrack.Id == west1.CurrentTrack.Id)
        {
            Assert.Equal(west0.ElapsedSec + 25.0, west1.ElapsedSec, precision: 2);
        }
    }

    [Fact]
    public void ToggleLikeTrack_PreservesCurrentTrackAndElapsedWithoutJumping()
    {
        var store = new UserRadioStore(_stateFile);
        var engine = new RadioBroadcastEngine(store);
        engine.LoadCatalog(CreateSampleCatalog());

        var now = new DateTimeOffset(2026, 11, 3, 9, 15, 30, TimeSpan.Zero);
        var before = engine.GetStationSnapshot("Nightdrive FM", now)!;

        bool liked = engine.ToggleLikeTrack(before.CurrentTrack.Id, "Nightdrive FM", now);
        Assert.True(liked);

        var after = engine.GetStationSnapshot("Nightdrive FM", now)!;
        Assert.Equal(before.CurrentTrack.Id, after.CurrentTrack.Id);
        Assert.Equal(before.ElapsedSec, after.ElapsedSec, precision: 1);
    }

    [Fact]
    public void BanTrack_RemovesTrackFromStationRotationImmediately()
    {
        var store = new UserRadioStore(_stateFile);
        var engine = new RadioBroadcastEngine(store);
        engine.LoadCatalog(CreateSampleCatalog());

        var now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var before = engine.GetStationSnapshot("Nightdrive FM", now)!;
        int bannedId = before.CurrentTrack.Id;

        var after = engine.BanTrack(bannedId, "Nightdrive FM", now)!;
        Assert.NotEqual(bannedId, after.CurrentTrack.Id);
        Assert.InRange(after.ElapsedSec, 0.0, 1.0);

        // Verify banned track never airs across an entire 2-hour sweep
        for (int m = 0; m < 120; m++)
        {
            var probe = engine.GetStationSnapshot("Nightdrive FM", now.AddMinutes(m))!;
            Assert.NotEqual(bannedId, probe.CurrentTrack.Id);
        }
    }

    [Fact]
    public void SkipCurrentTrack_AdvancesPhaseToNextTrackStart()
    {
        var store = new UserRadioStore(_stateFile);
        var engine = new RadioBroadcastEngine(store);
        engine.LoadCatalog(CreateSampleCatalog());

        var now = new DateTimeOffset(2026, 11, 3, 11, 0, 0, TimeSpan.Zero);
        var before = engine.GetStationSnapshot("Nightdrive FM", now)!;
        var after = engine.SkipCurrentTrack("Nightdrive FM", now)!;

        Assert.NotEqual(before.CurrentTrack.Id, after.CurrentTrack.Id);
        Assert.InRange(after.ElapsedSec, 0.0, 1.0);
    }

    [Fact]
    public void NextTrack_MatchesActualUpcomingSlotOnStation()
    {
        var store = new UserRadioStore(_stateFile);
        var engine = new RadioBroadcastEngine(store);
        engine.LoadCatalog(CreateSampleCatalog());

        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var snap = engine.GetStationSnapshot("Nightdrive FM", now)!;
        Assert.NotNull(snap.NextTrack);

        double remainingSec = snap.DurationSec - snap.ElapsedSec;
        var afterTrackEnds = engine.GetStationSnapshot("Nightdrive FM", now.AddSeconds(remainingSec + 1.0))!;
        Assert.Equal(snap.NextTrack!.Id, afterTrackEnds.CurrentTrack.Id);
    }

    [Fact]
    public void GetStreamUrl_PreferOpus_IncludesFormatAndOffset()
    {
        var store = new UserRadioStore(_stateFile);
        store.Preferences.ServerBaseUrl = "http://192.168.1.50:5480";
        var engine = new RadioBroadcastEngine(store);

        var track = new RadioTrack { Id = 42, Filename = "01 - Track.flac", Album = "Test [FLAC]", DurationSec = 240 };
        var url = engine.GetStreamUrl(track, startOffsetSec: 87.35, preferOpus: true);

        Assert.Equal("http://192.168.1.50:5480/api/stream/42?transcode=1&format=opus&ss=87.35", url);
    }

    [Fact]
    public async Task DiskCacheAndETag_LoadsInstantlyAndShortCircuitsOn304()
    {
        const string serverUrl = "http://127.0.0.1:5480";
        const string etag = "\"abc123etag\"";
        var catalogJson = JsonSerializer.SerializeToUtf8Bytes(CreateSampleCatalog());

        int fullPayloadsServed = 0;
        string? seenIfNoneMatch = null;

        using var http = new HttpClient(new StubHttpHandler(req =>
        {
            seenIfNoneMatch = req.Headers.IfNoneMatch.ToString();
            if (seenIfNoneMatch == etag)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            fullPayloadsServed++;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(catalogJson)
            };
            resp.Headers.ETag = new EntityTagHeaderValue(etag);
            return resp;
        }));

        // First launch: fetches 200 OK and persists disk cache + ETag
        var store1 = new UserRadioStore(_stateFile);
        store1.Preferences.ServerBaseUrl = serverUrl;
        store1.Save();

        var engine1 = new RadioBroadcastEngine(store1);
        Assert.False(engine1.TryLoadFromDiskCache());
        Assert.True(await engine1.LoadFromServerAsync(http));
        Assert.Equal(1, fullPayloadsServed);

        // Second launch: loads from disk cache immediately, then sends If-None-Match -> 304 Not Modified
        var store2 = new UserRadioStore(_stateFile);
        var engine2 = new RadioBroadcastEngine(store2);
        Assert.True(engine2.TryLoadFromDiskCache());
        Assert.True(engine2.IsLoaded);

        Assert.True(await engine2.LoadFromServerAsync(http));
        Assert.Equal(etag, seenIfNoneMatch);
        Assert.Equal(1, fullPayloadsServed);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static RadioCatalog CreateSampleCatalog()
    {
        return new RadioCatalog
        {
            Library = @"D:\Music",
            Tagged = 9,
            Stations = new Dictionary<string, StationCatalogEntry>
            {
                ["Nightdrive FM"] = new()
                {
                    Name = "Nightdrive FM",
                    Frequency = "104.5",
                    Format = "synthwave / electronic",
                    Count = 6,
                    Tracks =
                    [
                        new() { Id = 1, Filename = "01 - Future Club.flac", Artist = "Perturbator", Album = "Dangerous Days [FLAC]", DurationSec = 288 },
                        new() { Id = 2, Filename = "02 - Acid Rain.flac", Artist = "Lorn", Album = "Maze [FLAC]", DurationSec = 202 },
                        new() { Id = 3, Filename = "03 - This Means War.flac", Artist = "Volkor X", Album = "This Means War [FLAC]", DurationSec = 344 },
                        new() { Id = 4, Filename = "04 - Turbo Killer.flac", Artist = "Carpenter Brut", Album = "EP III [FLAC]", DurationSec = 208 },
                        new() { Id = 5, Filename = "05 - Behemoth.flac", Artist = "GosT", Album = "Behemoth [FLAC]", DurationSec = 256 },
                        new() { Id = 6, Filename = "06 - Leave Me Like This.m4a", Artist = "Skrillex", Album = "Quest for Fire [ALAC]", DurationSec = 188 }
                    ]
                },
                ["Westside FM"] = new()
                {
                    Name = "Westside FM",
                    Frequency = "88.7",
                    Format = "hip-hop / rap",
                    Count = 3,
                    Tracks =
                    [
                        new() { Id = 10, Filename = "01 - Lay Low.flac", Artist = "Snoop Dogg", Album = "Tha Last Meal [FLAC]", DurationSec = 223 },
                        new() { Id = 11, Filename = "02 - Hard In Da Paint.mp3", Artist = "Waka Flocka Flame", Album = "Flockaveli", DurationSec = 246 },
                        new() { Id = 12, Filename = "03 - Go to Church.m4a", Artist = "Ice Cube", Album = "Laugh Now", DurationSec = 240 }
                    ]
                }
            }
        };
    }
}
