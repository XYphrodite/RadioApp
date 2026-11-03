using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using RadioApp.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("Content-Range", "Accept-Ranges", "Content-Length"));
});

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["application/json", "text/plain"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddSingleton<ServerCatalogCache>();

var app = builder.Build();

app.UseCors();
app.UseResponseCompression();

// Warm up catalog cache on startup
var startupCache = app.Services.GetRequiredService<ServerCatalogCache>();

app.MapGet("/api/health", (ServerCatalogCache cache) => Results.Ok(new
{
    ok = true,
    host = Environment.MachineName,
    tagged = cache.TaggedCount,
    stations = cache.StationCount,
    updatedUtc = cache.LastSourceWriteUtc
}));

app.MapGet("/api/stations", (HttpContext ctx, ServerCatalogCache cache) =>
{
    var bytes = cache.GetSlimCatalogUtf8Bytes();
    if (bytes.Length == 0)
        return Results.Problem("Catalog is empty or stations.json was not found.", statusCode: 503);

    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Bytes(bytes, "application/json; charset=utf-8");
});

app.MapGet("/api/stream/{id:int}", async (
    int id,
    int? transcode,
    double? ss,
    HttpContext ctx,
    ServerCatalogCache cache) =>
{
    var track = cache.GetTrack(id);
    if (track is null || !cache.IsSafeLibraryPath(track.FullPath) || !File.Exists(track.FullPath))
        return Results.NotFound();

    bool shouldTranscode = transcode == 1 || track.IsAlac;
    if (!shouldTranscode)
    {
        return Results.File(
            track.FullPath,
            contentType: track.ContentType,
            enableRangeProcessing: true);
    }

    // Stream real-time lossless FLAC via ffmpeg (handles ALAC .m4a and seek offset ss)
    double startSec = Math.Clamp(ss ?? 0.0, 0.0, Math.Max(0.0, track.DurationSec - 0.5));
    var psi = new ProcessStartInfo
    {
        FileName = cache.FfmpegExecutable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    psi.ArgumentList.Add("-v");
    psi.ArgumentList.Add("error");
    if (startSec > 0.05)
    {
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startSec.ToString("F2", CultureInfo.InvariantCulture));
    }
    psi.ArgumentList.Add("-i");
    psi.ArgumentList.Add(track.FullPath);
    psi.ArgumentList.Add("-vn");
    psi.ArgumentList.Add("-c:a");
    psi.ArgumentList.Add("flac");
    psi.ArgumentList.Add("-compression_level");
    psi.ArgumentList.Add("2");
    psi.ArgumentList.Add("-f");
    psi.ArgumentList.Add("flac");
    psi.ArgumentList.Add("pipe:1");

    using var proc = Process.Start(psi);
    if (proc is null)
        return Results.StatusCode(500);

    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = "audio/flac";
    ctx.Response.Headers.CacheControl = "no-store";

    try
    {
        await proc.StandardOutput.BaseStream.CopyToAsync(ctx.Response.Body, 65536, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // Listener switched station or muted
    }
    finally
    {
        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    return Results.Empty;
});

app.MapGet("/api/cover/{id:int}", async (int id, HttpContext ctx, ServerCatalogCache cache) =>
{
    var track = cache.GetTrack(id);
    if (track is null || !cache.IsSafeLibraryPath(track.FullPath) || !File.Exists(track.FullPath))
        return Results.NotFound();

    var cover = await cache.GetOrExtractCoverAsync(track, ctx.RequestAborted);
    if (cover.Bytes is null || cover.Bytes.Length == 0)
        return Results.NotFound();

    ctx.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.Bytes(cover.Bytes, cover.ContentType);
});

app.Run();
