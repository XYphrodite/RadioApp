using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using RadioApp.Core.Models;

namespace RadioApp.Server.Services;

public sealed record ServerTrackEntry(
    int Id,
    string FullPath,
    string ContentType,
    bool IsAlac,
    double DurationSec);

public sealed record CachedCover(byte[]? Bytes, string ContentType);

public sealed class ServerCatalogCache
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false
    };

    private static readonly string[] PreferredCoverNames =
    [
        "cover.jpg", "cover.jpeg", "cover.png", "cover.webp",
        "folder.jpg", "folder.jpeg", "folder.png",
        "front.jpg", "front.png", "album.jpg", "artwork.jpg"
    ];

    private readonly Lock _reloadLock = new();
    private readonly ILogger<ServerCatalogCache> _logger;
    private readonly ConcurrentDictionary<string, CachedCover> _coverCache = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastSourceWriteUtc = DateTime.MinValue;
    private Dictionary<int, ServerTrackEntry> _tracksById = new();
    private byte[] _slimCatalogJsonBytes = [];
    private int _stationCount;
    private int _taggedCount;

    public string StationsJsonPath { get; }
    public string MusicRootPath { get; }
    public string FfmpegExecutable { get; }

    public int TaggedCount { get { EnsureFresh(); return _taggedCount; } }
    public int StationCount { get { EnsureFresh(); return _stationCount; } }
    public DateTime LastSourceWriteUtc => _lastSourceWriteUtc;

    public ServerCatalogCache(IConfiguration config, ILogger<ServerCatalogCache> logger)
    {
        _logger = logger;
        StationsJsonPath = config["RadioServer:StationsJsonPath"] ?? @"D:\Repos\RadioWork\stations.json";
        MusicRootPath = Path.GetFullPath(config["RadioServer:MusicRootPath"] ?? @"D:\Music");
        FfmpegExecutable = ResolveFfmpegPath(config["RadioServer:FfmpegPath"]);
        EnsureFresh();
    }

    public byte[] GetSlimCatalogUtf8Bytes()
    {
        EnsureFresh();
        return _slimCatalogJsonBytes;
    }

    public ServerTrackEntry? GetTrack(int id)
    {
        EnsureFresh();
        return _tracksById.GetValueOrDefault(id);
    }

    public bool IsSafeLibraryPath(string fullPath)
    {
        var normalizedFull = Path.GetFullPath(fullPath);
        var rootWithSep = MusicRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? MusicRootPath
            : MusicRootPath + Path.DirectorySeparatorChar;
        return normalizedFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CachedCover> GetOrExtractCoverAsync(ServerTrackEntry track, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(track.FullPath) ?? "";
        if (_coverCache.TryGetValue(dir, out var hit))
            return hit;

        if (_coverCache.Count > 600)
            _coverCache.Clear();

        // 1. Check external cover image in album directory (or parent if Disc/CD subfolder)
        var externalFile = FindExternalCoverFile(dir);
        if (externalFile is not null)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(externalFile, ct);
                var ext = Path.GetExtension(externalFile).ToLowerInvariant();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                var cached = new CachedCover(bytes, mime);
                _coverCache[dir] = cached;
                return cached;
            }
            catch
            {
                // Fall through to embedded extraction
            }
        }

        // 2. Try extracting embedded artwork via ffmpeg (runs at most once per album folder)
        var embedded = await ExtractEmbeddedCoverJpegAsync(track.FullPath, ct);
        var result = embedded is { Length: > 128 }
            ? new CachedCover(embedded, "image/jpeg")
            : new CachedCover(null, "image/jpeg");

        _coverCache[dir] = result;
        return result;
    }

    private void EnsureFresh()
    {
        if (!File.Exists(StationsJsonPath))
            return;

        var writeUtc = File.GetLastWriteTimeUtc(StationsJsonPath);
        if (writeUtc == _lastSourceWriteUtc && _slimCatalogJsonBytes.Length > 0)
            return;

        lock (_reloadLock)
        {
            if (writeUtc == _lastSourceWriteUtc && _slimCatalogJsonBytes.Length > 0)
                return;

            try
            {
                using var fs = File.OpenRead(StationsJsonPath);
                var rawCatalog = JsonSerializer.Deserialize<RadioCatalog>(fs, ReadOpts);
                if (rawCatalog is null)
                    return;

                var byId = new Dictionary<int, ServerTrackEntry>(rawCatalog.Tagged + 64);
                var slimStations = new Dictionary<string, StationCatalogEntry>(StringComparer.Ordinal);

                foreach (var (key, st) in rawCatalog.Stations)
                {
                    if (st.Tracks is null || st.Tracks.Count == 0)
                        continue;

                    var slimTracks = new List<RadioTrack>(st.Tracks.Count);
                    foreach (var tr in st.Tracks)
                    {
                        if (!string.IsNullOrWhiteSpace(tr.Path))
                        {
                            var fullPath = Path.GetFullPath(tr.Path);
                            byId[tr.Id] = new ServerTrackEntry(
                                Id: tr.Id,
                                FullPath: fullPath,
                                ContentType: GuessAudioContentType(tr.Filename),
                                IsAlac: tr.IsAlac,
                                DurationSec: tr.EffectiveDurationSec);
                        }

                        // Strip server disk path in client catalog DTO
                        slimTracks.Add(new RadioTrack
                        {
                            Id = tr.Id,
                            Path = null,
                            Filename = tr.Filename,
                            Artist = tr.Artist,
                            Album = tr.Album,
                            Genre = tr.Genre,
                            Station = tr.Station,
                            Bpm = tr.Bpm,
                            Energy = tr.Energy,
                            DurationSec = tr.DurationSec
                        });
                    }

                    slimStations[key] = new StationCatalogEntry
                    {
                        Name = string.IsNullOrWhiteSpace(st.Name) ? key : st.Name,
                        Frequency = st.Frequency,
                        Format = st.Format,
                        Count = slimTracks.Count,
                        Tracks = slimTracks
                    };
                }

                var slimCatalog = new RadioCatalog
                {
                    Library = rawCatalog.Library,
                    Source = rawCatalog.Source,
                    Tagged = byId.Count,
                    Stations = slimStations
                };

                _slimCatalogJsonBytes = JsonSerializer.SerializeToUtf8Bytes(slimCatalog, WriteOpts);
                _tracksById = byId;
                _stationCount = slimStations.Count;
                _taggedCount = byId.Count;
                _lastSourceWriteUtc = writeUtc;

                _logger.LogInformation(
                    "Loaded stations.json: {Tracks} tracks across {Stations} stations (slim DTO: {Kb} KB)",
                    _taggedCount,
                    _stationCount,
                    _slimCatalogJsonBytes.Length / 1024);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload catalog from {Path}", StationsJsonPath);
            }
        }
    }

    private static string? FindExternalCoverFile(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;

        var found = SearchDirForCover(dir);
        if (found is not null)
            return found;

        var folderName = Path.GetFileName(dir);
        if (folderName.StartsWith("CD", StringComparison.OrdinalIgnoreCase) ||
            folderName.StartsWith("Disc", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                return SearchDirForCover(parent);
        }

        return null;
    }

    private static string? SearchDirForCover(string dir)
    {
        foreach (var name in PreferredCoverNames)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".jpg" or ".jpeg" or ".png" or ".webp";
                })
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> ExtractEmbeddedCoverJpegAsync(string audioPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-vcodec");
            psi.ArgumentList.Add("mjpeg");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("scale='min(320,iw)':-2");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("image2pipe");
            psi.ArgumentList.Add("pipe:1");

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            await using var ms = new MemoryStream();
            await proc.StandardOutput.BaseStream.CopyToAsync(ms, timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);

            return proc.ExitCode == 0 && ms.Length > 128 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessAudioContentType(string filename)
    {
        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".flac" => "audio/flac",
            ".mp3" => "audio/mpeg",
            ".m4a" or ".mp4" or ".aac" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }

    private static string ResolveFfmpegPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var wingetLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(wingetLink))
            return wingetLink;

        return "ffmpeg";
    }
}
