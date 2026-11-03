using System.Net.Http.Json;
using System.Text.Json;
using RadioApp.Core.Models;

namespace RadioApp.Core.Services;

public sealed class RadioBroadcastEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Lock _sync = new();
    private readonly UserRadioStore _store;
    private readonly Dictionary<string, StationSchedule> _schedules = new(StringComparer.OrdinalIgnoreCase);

    public CatalogStats Stats { get; private set; } = new(0, 0, 0);
    public bool IsLoaded => _schedules.Count > 0;
    public UserRadioPreferences Preferences => _store.Preferences;

    public RadioBroadcastEngine(UserRadioStore store)
    {
        _store = store;
    }

    public async Task<bool> LoadFromServerAsync(HttpClient httpClient, CancellationToken ct = default)
    {
        var baseUrl = UserRadioStore.NormalizeServerUrl(_store.Preferences.ServerBaseUrl);
        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.GetAsync($"{baseUrl}/api/stations", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode && TryResolveIpBaseUrl(baseUrl, out var ipUrl))
            {
                response.Dispose();
                response = await httpClient.GetAsync($"{ipUrl}/api/stations", HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    _store.Preferences.ServerBaseUrl = ipUrl;
                    _store.Save();
                }
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var catalog = await JsonSerializer.DeserializeAsync<RadioCatalog>(stream, JsonOpts, ct);
            if (catalog is null || catalog.Stations.Count == 0)
                return false;

            LoadCatalog(catalog);
            return true;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static bool TryResolveIpBaseUrl(string baseUrl, out string ipBaseUrl)
    {
        ipBaseUrl = baseUrl;
        try
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                System.Net.IPAddress.TryParse(uri.Host, out _))
                return false;

            var ip = System.Net.Dns.GetHostAddresses(uri.Host)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip is null)
                return false;

            ipBaseUrl = $"{uri.Scheme}://{ip}:{uri.Port}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void LoadCatalog(RadioCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        lock (_sync)
        {
            _schedules.Clear();

            var uniqueArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalTracks = 0;
            double totalDurationSec = 0;

            // Assign deterministic per-station phase offsets so stations aren't synchronized at t=0
            int stationIndex = 0;
            foreach (var (key, entry) in catalog.Stations.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Tracks is null || entry.Tracks.Count == 0)
                    continue;

                var stationName = string.IsNullOrWhiteSpace(entry.Name) ? key : entry.Name;
                var theme = StationThemes.Resolve(stationName, entry.Frequency);

                foreach (var tr in entry.Tracks)
                {
                    totalTracks++;
                    totalDurationSec += tr.EffectiveDurationSec;
                    if (!string.IsNullOrWhiteSpace(tr.Artist))
                        uniqueArtists.Add(tr.Artist.Trim());
                }

                double initialPhaseSec = ComputeDeterministicHash(stationName) % 86_400 + stationIndex * 137.0;
                stationIndex++;

                var schedule = BuildSchedule(
                    stationName,
                    entry.Format,
                    theme,
                    entry.Tracks,
                    initialPhaseSec,
                    _store.Preferences.LikedTrackIds,
                    _store.Preferences.BannedTrackIds);

                _schedules[stationName] = schedule;
            }

            Stats = new CatalogStats(
                TotalTracks: catalog.Tagged > 0 ? catalog.Tagged : totalTracks,
                TotalArtists: uniqueArtists.Count,
                TotalHours: (int)Math.Round(totalDurationSec / 3600.0));

            if (!_schedules.ContainsKey(_store.Preferences.ActiveStationName) && _schedules.Count > 0)
            {
                var biggest = _schedules.Values
                    .OrderByDescending(s => s.RawTracks.Count)
                    .First();
                _store.Preferences.ActiveStationName = biggest.Name;
                _store.Save();
            }
        }
    }

    public IReadOnlyList<StationLiveState> GetAllStationsSnapshot(DateTimeOffset utcNow, string? sortMode = null)
    {
        lock (_sync)
        {
            var mode = sortMode ?? _store.Preferences.SortMode;
            IEnumerable<StationSchedule> ordered = string.Equals(mode, "freq", StringComparison.OrdinalIgnoreCase)
                ? _schedules.Values.OrderBy(s => s.Theme.FrequencyMhz)
                : _schedules.Values.OrderByDescending(s => s.RawTracks.Count).ThenBy(s => s.Theme.FrequencyMhz);

            var result = new List<StationLiveState>(_schedules.Count);
            foreach (var schedule in ordered)
            {
                result.Add(ComputeLiveStateUnlocked(schedule, utcNow));
            }
            return result;
        }
    }

    public StationLiveState? GetStationSnapshot(string stationName, DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            return _schedules.TryGetValue(stationName, out var schedule)
                ? ComputeLiveStateUnlocked(schedule, utcNow)
                : null;
        }
    }

    public string StepStationByFrequency(string currentStationName, int direction)
    {
        lock (_sync)
        {
            if (_schedules.Count == 0)
                return currentStationName;

            var byFreq = _schedules.Values
                .OrderBy(s => s.Theme.FrequencyMhz)
                .ToList();

            int idx = byFreq.FindIndex(s => string.Equals(s.Name, currentStationName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                idx = 0;

            int nextIdx = (idx + direction % byFreq.Count + byFreq.Count) % byFreq.Count;
            var nextName = byFreq[nextIdx].Name;
            _store.Preferences.ActiveStationName = nextName;
            _store.Save();
            return nextName;
        }
    }

    public string FindNearestStationByFrequency(double targetMhz)
    {
        lock (_sync)
        {
            if (_schedules.Count == 0)
                return _store.Preferences.ActiveStationName;

            StationSchedule? nearest = null;
            double minDiff = double.MaxValue;
            foreach (var s in _schedules.Values)
            {
                double diff = Math.Abs(s.Theme.FrequencyMhz - targetMhz);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    nearest = s;
                }
            }

            return nearest?.Name ?? _store.Preferences.ActiveStationName;
        }
    }

    public void SetActiveStation(string stationName)
    {
        lock (_sync)
        {
            if (_schedules.ContainsKey(stationName) &&
                !string.Equals(_store.Preferences.ActiveStationName, stationName, StringComparison.OrdinalIgnoreCase))
            {
                _store.Preferences.ActiveStationName = _schedules[stationName].Name;
                _store.Save();
            }
        }
    }

    public StationLiveState? SkipCurrentTrack(string stationName, DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            if (!_schedules.TryGetValue(stationName, out var schedule))
                return null;

            var current = ComputeLiveStateUnlocked(schedule, utcNow);
            double remaining = Math.Max(0.08, current.DurationSec - current.ElapsedSec + 0.08);
            double existingOffset = _store.Preferences.StationSkipOffsetsSec.GetValueOrDefault(schedule.Name, 0.0);
            _store.Preferences.StationSkipOffsetsSec[schedule.Name] = PositiveMod(
                existingOffset + remaining,
                schedule.TotalCycleDurationSec);
            _store.Save();

            return ComputeLiveStateUnlocked(schedule, utcNow);
        }
    }

    public bool IsTrackLiked(int trackId)
    {
        lock (_sync)
        {
            return _store.Preferences.LikedTrackIds.Contains(trackId);
        }
    }

    public bool ToggleLikeTrack(int trackId, string stationName, DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            bool nowLiked;
            if (!_store.Preferences.LikedTrackIds.Remove(trackId))
            {
                _store.Preferences.LikedTrackIds.Add(trackId);
                nowLiked = true;
            }
            else
            {
                nowLiked = false;
            }

            if (_schedules.TryGetValue(stationName, out var oldSchedule))
            {
                var before = ComputeLiveStateUnlocked(oldSchedule, utcNow);
                var rebuilt = BuildSchedule(
                    oldSchedule.Name,
                    oldSchedule.Format,
                    oldSchedule.Theme,
                    oldSchedule.RawTracks,
                    oldSchedule.InitialPhaseSec,
                    _store.Preferences.LikedTrackIds,
                    _store.Preferences.BannedTrackIds);

                _schedules[oldSchedule.Name] = rebuilt;

                // Anchor the currently playing track at its exact elapsed timestamp so audio never jumps on Like!
                AnchorTrackPlaybackUnlocked(rebuilt, before.CurrentTrack.Id, before.ElapsedSec, utcNow);
            }

            _store.Save();
            return nowLiked;
        }
    }

    public StationLiveState? BanTrack(int trackId, string stationName, DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            _store.Preferences.BannedTrackIds.Add(trackId);
            _store.Preferences.LikedTrackIds.Remove(trackId);

            if (!_schedules.TryGetValue(stationName, out var oldSchedule))
            {
                _store.Save();
                return null;
            }

            // Pick a target replacement track (the next non-banned track in the old rotation)
            var (_, curSlotIdx, _) = LocateSlotUnlocked(oldSchedule, utcNow);
            int replacementTrackId = -1;
            for (int step = 1; step <= oldSchedule.Slots.Count; step++)
            {
                var candidate = oldSchedule.Slots[(curSlotIdx + step) % oldSchedule.Slots.Count];
                if (!_store.Preferences.BannedTrackIds.Contains(candidate.Id))
                {
                    replacementTrackId = candidate.Id;
                    break;
                }
            }

            var rebuilt = BuildSchedule(
                oldSchedule.Name,
                oldSchedule.Format,
                oldSchedule.Theme,
                oldSchedule.RawTracks,
                oldSchedule.InitialPhaseSec,
                _store.Preferences.LikedTrackIds,
                _store.Preferences.BannedTrackIds);

            _schedules[oldSchedule.Name] = rebuilt;

            if (replacementTrackId >= 0)
            {
                AnchorTrackPlaybackUnlocked(rebuilt, replacementTrackId, 0.05, utcNow);
            }

            _store.Save();
            return ComputeLiveStateUnlocked(rebuilt, utcNow);
        }
    }

    public void ClearBannedTracks(DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            if (_store.Preferences.BannedTrackIds.Count == 0)
                return;

            var activeSnap = GetStationSnapshot(_store.Preferences.ActiveStationName, utcNow);
            _store.Preferences.BannedTrackIds.Clear();

            foreach (var key in _schedules.Keys.ToList())
            {
                var old = _schedules[key];
                _schedules[key] = BuildSchedule(
                    old.Name,
                    old.Format,
                    old.Theme,
                    old.RawTracks,
                    old.InitialPhaseSec,
                    _store.Preferences.LikedTrackIds,
                    _store.Preferences.BannedTrackIds);
            }

            if (activeSnap is not null && _schedules.TryGetValue(activeSnap.Name, out var activeRebuilt))
            {
                AnchorTrackPlaybackUnlocked(activeRebuilt, activeSnap.CurrentTrack.Id, activeSnap.ElapsedSec, utcNow);
            }

            _store.Save();
        }
    }

    public string GetStreamUrl(RadioTrack track, bool forceTranscode = false, double startOffsetSec = 0)
    {
        var baseUrl = UserRadioStore.NormalizeServerUrl(_store.Preferences.ServerBaseUrl);
        if (forceTranscode || track.IsAlac)
        {
            var ss = Math.Max(0, startOffsetSec).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return $"{baseUrl}/api/stream/{track.Id}?transcode=1&ss={ss}";
        }
        return $"{baseUrl}/api/stream/{track.Id}";
    }

    public string GetCoverUrl(RadioTrack track)
    {
        var baseUrl = UserRadioStore.NormalizeServerUrl(_store.Preferences.ServerBaseUrl);
        return $"{baseUrl}/api/cover/{track.Id}";
    }

    private void AnchorTrackPlaybackUnlocked(StationSchedule schedule, int targetTrackId, double targetElapsedSec, DateTimeOffset utcNow)
    {
        int slotIdx = -1;
        for (int i = 0; i < schedule.Slots.Count; i++)
        {
            if (schedule.Slots[i].Id == targetTrackId)
            {
                slotIdx = i;
                break;
            }
        }

        if (slotIdx < 0)
            return;

        double slotStart = slotIdx == 0 ? 0.0 : schedule.CumulativeEndSec[slotIdx - 1];
        double clampedElapsed = Math.Clamp(targetElapsedSec, 0.0, schedule.Slots[slotIdx].EffectiveDurationSec - 0.05);
        double desiredCyclePos = slotStart + clampedElapsed;
        double nowSec = utcNow.ToUnixTimeMilliseconds() / 1000.0;
        double newOffset = PositiveMod(desiredCyclePos - (nowSec + schedule.InitialPhaseSec), schedule.TotalCycleDurationSec);
        _store.Preferences.StationSkipOffsetsSec[schedule.Name] = newOffset;
    }

    private StationLiveState ComputeLiveStateUnlocked(StationSchedule schedule, DateTimeOffset utcNow)
    {
        var (currentTrack, slotIdx, elapsedSec) = LocateSlotUnlocked(schedule, utcNow);

        // Compute recent 2 aired tracks before slotIdx
        var history = new List<HistoryEntry>(2);
        double secondsBack = elapsedSec;
        int count = Math.Min(2, Math.Max(0, schedule.Slots.Count - 1));
        for (int h = 1; h <= count; h++)
        {
            int prevIdx = (slotIdx - h + schedule.Slots.Count) % schedule.Slots.Count;
            var prevTrack = schedule.Slots[prevIdx];
            int minsAgo = Math.Max(1, (int)Math.Round(secondsBack / 60.0));
            history.Add(new HistoryEntry(prevTrack, minsAgo));
            secondsBack += prevTrack.EffectiveDurationSec;
        }

        return new StationLiveState(
            Id: schedule.Theme.Id,
            Name: schedule.Name,
            Format: schedule.Format,
            FrequencyMhz: schedule.Theme.FrequencyMhz,
            Icon: schedule.Theme.Icon,
            AccentHex: schedule.Theme.AccentHex,
            TotalTrackCount: schedule.RawTracks.Count,
            CurrentTrack: currentTrack,
            ElapsedSec: elapsedSec,
            DurationSec: currentTrack.EffectiveDurationSec,
            RecentHistory: history);
    }

    private (RadioTrack Track, int SlotIndex, double ElapsedSec) LocateSlotUnlocked(StationSchedule schedule, DateTimeOffset utcNow)
    {
        double nowSec = utcNow.ToUnixTimeMilliseconds() / 1000.0;
        double skipOffset = _store.Preferences.StationSkipOffsetsSec.GetValueOrDefault(schedule.Name, 0.0);
        double cyclePos = PositiveMod(nowSec + schedule.InitialPhaseSec + skipOffset, schedule.TotalCycleDurationSec);

        int idx = Array.BinarySearch(schedule.CumulativeEndSec, cyclePos);
        if (idx < 0)
            idx = ~idx;
        else
            idx = Math.Min(idx + 1, schedule.Slots.Count - 1); // exact boundary belongs to next slot

        idx = Math.Clamp(idx, 0, schedule.Slots.Count - 1);
        double slotStart = idx == 0 ? 0.0 : schedule.CumulativeEndSec[idx - 1];
        var track = schedule.Slots[idx];
        double elapsed = Math.Clamp(cyclePos - slotStart, 0.0, track.EffectiveDurationSec);
        return (track, idx, elapsed);
    }

    private static StationSchedule BuildSchedule(
        string name,
        string format,
        StationTheme theme,
        List<RadioTrack> rawTracks,
        double initialPhaseSec,
        HashSet<int> likedIds,
        HashSet<int> bannedIds)
    {
        var activePool = rawTracks.Where(t => !bannedIds.Contains(t.Id)).ToList();
        if (activePool.Count == 0)
            activePool = new List<RadioTrack>(rawTracks);

        // Deterministic shuffle seeded by station name so all devices share the same live FM schedule
        uint seed = ComputeDeterministicHash(name) ^ 0x9E3779B9u;
        var shuffled = DeterministicShuffle(activePool, seed);

        // Boost liked tracks (2x weight) by interleaving a second pass offset by half the rotation
        if (likedIds.Count > 0)
        {
            var likedInStation = shuffled.Where(t => likedIds.Contains(t.Id)).ToList();
            if (likedInStation.Count > 0 && shuffled.Count >= 4)
            {
                var boosted = new List<RadioTrack>(shuffled.Count + likedInStation.Count);
                boosted.AddRange(shuffled);
                int stride = Math.Max(2, boosted.Count / (likedInStation.Count + 1));
                int insertPos = stride / 2;
                foreach (var lt in likedInStation)
                {
                    int clampedPos = Math.Clamp(insertPos, 0, boosted.Count);
                    boosted.Insert(clampedPos, lt);
                    insertPos = (clampedPos + stride + 1) % (boosted.Count + 1);
                }
                shuffled = boosted;
            }
        }

        // Anti-clumping pass: avoid playing the exact same artist back-to-back when alternatives exist
        SpreadConsecutiveArtists(shuffled);

        var cumulative = new double[shuffled.Count];
        double running = 0.0;
        for (int i = 0; i < shuffled.Count; i++)
        {
            running += shuffled[i].EffectiveDurationSec;
            cumulative[i] = running;
        }

        return new StationSchedule(
            Name: name,
            Format: format,
            Theme: theme,
            RawTracks: rawTracks,
            Slots: shuffled,
            CumulativeEndSec: cumulative,
            TotalCycleDurationSec: Math.Max(1.0, running),
            InitialPhaseSec: initialPhaseSec);
    }

    private static List<RadioTrack> DeterministicShuffle(IReadOnlyList<RadioTrack> input, uint seed)
    {
        var arr = input.OrderBy(t => t.Id).ToList();
        ulong state = seed == 0 ? 0xCAFEBABE12345678UL : seed * 0x9E3779B97F4A7C15UL;

        for (int i = arr.Count - 1; i > 0; i--)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong rnd = state * 2685821657736338717UL;
            int j = (int)(rnd % (ulong)(i + 1));
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }

        return arr;
    }

    private static void SpreadConsecutiveArtists(List<RadioTrack> tracks)
    {
        if (tracks.Count < 3)
            return;

        for (int i = 1; i < tracks.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(tracks[i].Artist))
                continue;

            if (string.Equals(tracks[i].Artist, tracks[i - 1].Artist, StringComparison.OrdinalIgnoreCase))
            {
                for (int swap = i + 1; swap < Math.Min(tracks.Count, i + 25); swap++)
                {
                    if (!string.Equals(tracks[swap].Artist, tracks[i - 1].Artist, StringComparison.OrdinalIgnoreCase))
                    {
                        (tracks[i], tracks[swap]) = (tracks[swap], tracks[i]);
                        break;
                    }
                }
            }
        }
    }

    private static uint ComputeDeterministicHash(string text)
    {
        uint hash = 2166136261u;
        foreach (char c in text)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619u;
        }
        return hash;
    }

    private static double PositiveMod(double value, double modulus)
    {
        if (modulus <= 0)
            return 0;
        double r = value % modulus;
        return r < 0 ? r + modulus : r;
    }

    private sealed record StationSchedule(
        string Name,
        string Format,
        StationTheme Theme,
        List<RadioTrack> RawTracks,
        List<RadioTrack> Slots,
        double[] CumulativeEndSec,
        double TotalCycleDurationSec,
        double InitialPhaseSec);
}
