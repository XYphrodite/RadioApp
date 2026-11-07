using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using RadioApp.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("Content-Range", "Accept-Ranges", "Content-Length", "ETag"));
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

// Allow Chromium / Android BlazorWebView (https://0.0.0.0) Private Network Access to 127.0.0.1 / Tailnet HTTP
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
    {
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }
    await next();
});

app.UseCors();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();

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
    var acceptEncoding = ctx.Request.Headers.AcceptEncoding.ToString();
    var (bytes, contentEncoding, etag) = cache.GetSlimCatalogPayload(acceptEncoding);
    if (bytes.Length == 0)
        return Results.Problem("Catalog is empty or stations.json was not found.", statusCode: 503);

    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Vary = "Accept-Encoding";
    if (!string.IsNullOrEmpty(etag))
    {
        ctx.Response.Headers.ETag = etag;
        var inm = ctx.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(inm) && (inm == "*" || inm.Contains(etag, StringComparison.Ordinal)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
    }

    if (!string.IsNullOrEmpty(contentEncoding))
    {
        ctx.Response.Headers.ContentEncoding = contentEncoding;
    }

    return Results.Bytes(bytes, "application/json; charset=utf-8");
});

app.MapGet("/api/stream/{id:int}", async (
    int id,
    int? transcode,
    string? format,
    double? ss,
    HttpContext ctx,
    ServerCatalogCache cache,
    ILoggerFactory loggerFactory) =>
{
    var track = cache.GetTrack(id);
    if (track is null || !cache.IsSafeLibraryPath(track.FullPath) || !File.Exists(track.FullPath))
        return Results.NotFound();

    bool isOpus = string.Equals(format, "opus", StringComparison.OrdinalIgnoreCase);
    bool shouldTranscode = isOpus || transcode == 1 || track.IsAlac;
    if (!shouldTranscode)
    {
        return Results.File(
            track.FullPath,
            contentType: track.ContentType,
            enableRangeProcessing: true);
    }

    // Stream real-time Opus 192k (mobile fast-start) or lossless FLAC via ffmpeg with server-side seek (-ss)
    double startSec = Math.Clamp(ss ?? 0.0, 0.0, Math.Max(0.0, track.DurationSec - 0.5));
    var psi = new ProcessStartInfo
    {
        FileName = cache.FfmpegExecutable,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    psi.ArgumentList.Add("-nostdin");
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
    psi.ArgumentList.Add("-sn");
    psi.ArgumentList.Add("-dn");
    psi.ArgumentList.Add("-map_metadata");
    psi.ArgumentList.Add("-1");

    if (isOpus)
    {
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("libopus");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add("-vbr");
        psi.ArgumentList.Add("on");
        psi.ArgumentList.Add("-compression_level");
        psi.ArgumentList.Add("5");
        psi.ArgumentList.Add("-frame_duration");
        psi.ArgumentList.Add("20");
        psi.ArgumentList.Add("-flush_packets");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-cluster_time_limit");
        psi.ArgumentList.Add("200");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("webm");
    }
    else
    {
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("flac");
        psi.ArgumentList.Add("-compression_level");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-flush_packets");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("flac");
    }
    psi.ArgumentList.Add("pipe:1");

    Process? proc;
    try
    {
        proc = Process.Start(psi);
    }
    catch (Exception ex)
    {
        loggerFactory.CreateLogger("Stream").LogError(ex, "Failed to start ffmpeg ({Exe})", cache.FfmpegExecutable);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }

    if (proc is null)
        return Results.StatusCode(StatusCodes.Status500InternalServerError);

    using (proc)
    {
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            int firstRead = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted);
            if (firstRead <= 0)
            {
                var errText = await stderrTask;
                loggerFactory.CreateLogger("Stream").LogWarning(
                    "ffmpeg exited with {Code} and 0 output bytes for track {Id}: {Err}",
                    proc.HasExited ? proc.ExitCode : -1,
                    id,
                    errText);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = isOpus ? "audio/webm; codecs=opus" : "audio/flac";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, firstRead), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            long totalSent = firstRead;
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ctx.RequestAborted)) > 0)
            {
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                totalSent += read;
                if (totalSent <= 131072)
                {
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }

            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Listener switched station or muted
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
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
