using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
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
    private const long MaxRawExternalCoverBytes = 96 * 1024;

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
    private readonly SemaphoreSlim _coverExtractGate = new(2, 2);
    private readonly ILogger<ServerCatalogCache> _logger;
    private readonly ConcurrentDictionary<string, CachedCover> _coverCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<CachedCover>> _inflightCovers = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastSourceWriteUtc = DateTime.MinValue;
    private Dictionary<int, ServerTrackEntry> _tracksById = new();
    private byte[] _slimCatalogJsonBytes = [];
    private byte[] _slimCatalogBrotliBytes = [];
    private byte[] _slimCatalogGzipBytes = [];
    private string _catalogETag = "";
    private int _stationCount;
    private int _taggedCount;

    public string StationsJsonPath { get; }
    public string MusicRootPath { get; }
    public string FfmpegExecutable { get; }

    public int TaggedCount { get { EnsureFresh(); return _taggedCount; } }
    public int StationCount { get { EnsureFresh(); return _stationCount; } }
    public string CatalogETag { get { EnsureFresh(); return _catalogETag; } }
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

    public (byte[] Bytes, string? ContentEncoding, string ETag) GetSlimCatalogPayload(string? acceptEncoding)
    {
        EnsureFresh();
        if (_slimCatalogJsonBytes.Length == 0)
            return ([], null, "");

        if (!string.IsNullOrEmpty(acceptEncoding))
        {
            if (_slimCatalogBrotliBytes.Length > 0 &&
                acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            {
                return (_slimCatalogBrotliBytes, "br", _catalogETag);
            }

            if (_slimCatalogGzipBytes.Length > 0 &&
                acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                return (_slimCatalogGzipBytes, "gzip", _catalogETag);
            }
        }

        return (_slimCatalogJsonBytes, null, _catalogETag);
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

        if (_coverCache.Count > 2000)
            _coverCache.Clear();

        var inflight = _inflightCovers.GetOrAdd(dir, _ => LoadAndCompressCoverCoreAsync(dir, track.FullPath));
        try
        {
            return await inflight.WaitAsync(ct);
        }
        finally
        {
            _inflightCovers.TryRemove(new KeyValuePair<string, Task<CachedCover>>(dir, inflight));
        }
    }

    private async Task<CachedCover> LoadAndCompressCoverCoreAsync(string dir, string audioFullPath)
    {
        // 1. Check external cover image in album directory (or parent if Disc/CD subfolder)
        var externalFile = FindExternalCoverFile(dir);
        if (externalFile is not null)
        {
            try
            {
                var info = new FileInfo(externalFile);
                var ext = info.Extension.ToLowerInvariant();
                if (info.Length <= MaxRawExternalCoverBytes && ext is ".jpg" or ".jpeg" or ".webp")
                {
                    var smallBytes = await File.ReadAllBytesAsync(externalFile);
                    var smallMime = ext == ".webp" ? "image/webp" : "image/jpeg";
                    var smallCached = new CachedCover(smallBytes, smallMime);
                    _coverCache[dir] = smallCached;
                    return smallCached;
                }

                // Downscale large multi-megabyte scans (cover.png / cover.jpg) to compact 360px JPEG
                var resized = await ExtractOrResizeCoverJpegAsync(externalFile, CancellationToken.None);
                if (resized is { Length: > 128 })
                {
                    var resizedCached = new CachedCover(resized, "image/jpeg");
                    _coverCache[dir] = resizedCached;
                    return resizedCached;
                }

                if (info.Length <= 2 * 1024 * 1024)
                {
                    var fallbackBytes = await File.ReadAllBytesAsync(externalFile);
                    var fallbackMime = ext switch
                    {
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        _ => "image/jpeg"
                    };
                    var fallbackCached = new CachedCover(fallbackBytes, fallbackMime);
                    _coverCache[dir] = fallbackCached;
                    return fallbackCached;
                }
            }
            catch
            {
                // Fall through to embedded extraction
            }
        }

        // 2. Try extracting embedded artwork via ffmpeg (runs at most once per album folder)
        var embedded = await ExtractOrResizeCoverJpegAsync(audioFullPath, CancellationToken.None);
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

                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(slimCatalog, WriteOpts);
                var hash = SHA256.HashData(jsonBytes);
                var etag = $"\"{Convert.ToHexStringLower(hash.AsSpan(0, 12))}\"";

                _slimCatalogJsonBytes = jsonBytes;
                _slimCatalogBrotliBytes = CompressBrotli(jsonBytes);
                _slimCatalogGzipBytes = CompressGzip(jsonBytes);
                _catalogETag = etag;
                _tracksById = byId;
                _stationCount = slimStations.Count;
                _taggedCount = byId.Count;
                _lastSourceWriteUtc = writeUtc;

                _logger.LogInformation(
                    "Loaded stations.json: {Tracks} tracks across {Stations} stations (raw: {RawKb} KB, br: {BrKb} KB)",
                    _taggedCount,
                    _stationCount,
                    _slimCatalogJsonBytes.Length / 1024,
                    _slimCatalogBrotliBytes.Length / 1024);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reload catalog from {Path}", StationsJsonPath);
            }
        }
    }

    private static byte[] CompressBrotli(byte[] input)
    {
        using var ms = new MemoryStream(input.Length / 5);
        using (var br = new BrotliStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            br.Write(input, 0, input.Length);
        }
        return ms.ToArray();
    }

    private static byte[] CompressGzip(byte[] input)
    {
        using var ms = new MemoryStream(input.Length / 4);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(input, 0, input.Length);
        }
        return ms.ToArray();
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

    private async Task<byte[]?> ExtractOrResizeCoverJpegAsync(string inputPath, CancellationToken ct)
    {
        await _coverExtractGate.WaitAsync(ct);
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
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-sn");
            psi.ArgumentList.Add("-vcodec");
            psi.ArgumentList.Add("mjpeg");
            psi.ArgumentList.Add("-q:v");
            psi.ArgumentList.Add("4");
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add("scale='min(360,iw)':-2");
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("image2pipe");
            psi.ArgumentList.Add("pipe:1");

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            _ = proc.StandardError.ReadToEndAsync();

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
        finally
        {
            _coverExtractGate.Release();
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
