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
