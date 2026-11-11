using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadioApp.Core.Services;

public sealed class UserRadioPreferences
{
    [JsonPropertyName("server_base_url")]
    public string ServerBaseUrl { get; set; } = UserRadioStore.DefaultServerBaseUrl;

    [JsonPropertyName("active_station")]
    public string ActiveStationName { get; set; } = "Nightdrive FM";

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 0.75;

    [JsonPropertyName("power_on")]
    public bool PowerOn { get; set; } = true;

    [JsonPropertyName("static_fx")]
    public bool StaticFxEnabled { get; set; } = true;

    [JsonPropertyName("sort_mode")]
    public string SortMode { get; set; } = "count";

    [JsonPropertyName("liked_track_ids")]
    public HashSet<int> LikedTrackIds { get; set; } = new();

    [JsonPropertyName("banned_track_ids")]
    public HashSet<int> BannedTrackIds { get; set; } = new();

    [JsonPropertyName("station_skip_offsets_sec")]
    public Dictionary<string, double> StationSkipOffsetsSec { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("tailscale_auth_key")]
    public string TailscaleAuthKey { get; set; } = "";

    /// <summary>"radio" (live FM broadcast) or "player" (regular media player over the whole library).</summary>
    [JsonPropertyName("app_mode")]
    public string AppMode { get; set; } = "radio";

    [JsonPropertyName("player_track_id")]
    public int PlayerTrackId { get; set; } = -1;

    [JsonPropertyName("player_shuffle")]
    public bool PlayerShuffle { get; set; }

    /// <summary>"off" | "all" | "one"</summary>
    [JsonPropertyName("player_repeat")]
    public string PlayerRepeat { get; set; } = "all";

    /// <summary>"default" | "artist" | "album" | "title" | "duration"</summary>
    [JsonPropertyName("player_sort_key")]
    public string PlayerSortKey { get; set; } = "default";

    [JsonPropertyName("player_sort_asc")]
    public bool PlayerSortAsc { get; set; } = true;

    [JsonPropertyName("player_artist")]
    public string PlayerArtist { get; set; } = "";

    [JsonPropertyName("player_station")]
    public string PlayerStation { get; set; } = "";
}

public sealed class UserRadioStore
{
    public const string DefaultServerBaseUrl = "http://mks68i7rtx:5480";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly Lock _sync = new();
    private readonly string _filePath;
    private readonly string _catalogCachePath;
    private readonly string _catalogMetaPath;

    public UserRadioPreferences Preferences { get; private set; } = new();

    public UserRadioStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _filePath = customFilePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = AppContext.BaseDirectory;
            _filePath = Path.Combine(appData, "RadioWork", "user_state.json");
        }

        var dir = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(dir))
            dir = AppContext.BaseDirectory;

        _catalogCachePath = Path.Combine(dir, "catalog_cache.json");
        _catalogMetaPath = Path.Combine(dir, "catalog_cache.meta");

        Load();
    }

    public void Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return;

                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<UserRadioPreferences>(json, JsonOpts);
                if (loaded is not null)
                {
                    loaded.ServerBaseUrl = NormalizeServerUrl(loaded.ServerBaseUrl);
                    loaded.Volume = Math.Clamp(loaded.Volume, 0.0, 1.0);
                    loaded.StationSkipOffsetsSec = new Dictionary<string, double>(
                        loaded.StationSkipOffsetsSec ?? new(),
                        StringComparer.OrdinalIgnoreCase);
                    Preferences = loaded;
                }
            }
            catch
            {
                Preferences = new UserRadioPreferences();
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var tmpPath = _filePath + ".tmp";
                var json = JsonSerializer.Serialize(Preferences, JsonOpts);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _filePath, overwrite: true);
            }
            catch
            {
                // Ignore transient disk write errors
            }
        }
    }

    public bool TryReadCatalogCache(out byte[] utf8Json, out string? serverUrl, out string? etag)
    {
        lock (_sync)
        {
            utf8Json = [];
            serverUrl = null;
            etag = null;

            try
            {
                if (!File.Exists(_catalogCachePath))
                    return false;

                utf8Json = File.ReadAllBytes(_catalogCachePath);
                if (utf8Json.Length < 16)
                {
                    utf8Json = [];
                    return false;
                }

                if (File.Exists(_catalogMetaPath))
                {
                    var lines = File.ReadAllLines(_catalogMetaPath);
                    if (lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0]))
                        serverUrl = NormalizeServerUrl(lines[0]);
                    if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1]))
                        etag = lines[1].Trim();
                }

                return true;
            }
            catch
            {
                utf8Json = [];
                return false;
            }
        }
    }

    public (string? ServerUrl, string? ETag) GetCachedCatalogMeta()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_catalogMetaPath) || !File.Exists(_catalogCachePath))
                    return (null, null);

                var lines = File.ReadAllLines(_catalogMetaPath);
                var srv = lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0])
                    ? NormalizeServerUrl(lines[0])
                    : null;
                var tag = lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1])
                    ? lines[1].Trim()
                    : null;
                return (srv, tag);
            }
            catch
            {
                return (null, null);
            }
        }
    }

    public void WriteCatalogCache(byte[] utf8Json, string serverUrl, string? etag)
    {
        if (utf8Json is null || utf8Json.Length == 0)
            return;

        lock (_sync)
        {
            try
            {
                var dir = Path.GetDirectoryName(_catalogCachePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var tmpJson = _catalogCachePath + ".tmp";
                File.WriteAllBytes(tmpJson, utf8Json);
                File.Move(tmpJson, _catalogCachePath, overwrite: true);

                var metaContent = $"{NormalizeServerUrl(serverUrl)}\n{etag?.Trim() ?? ""}";
                var tmpMeta = _catalogMetaPath + ".tmp";
                File.WriteAllText(tmpMeta, metaContent);
                File.Move(tmpMeta, _catalogMetaPath, overwrite: true);
            }
            catch
            {
                // Ignore transient disk write errors
            }
        }
    }

    public static string NormalizeServerUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultServerBaseUrl;

        var trimmed = raw.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        return trimmed;
    }
}
