using AspNetCoreRateLimit;
using LauncherServer.Cli;
using LauncherServer.Models;
using LauncherServer.Services;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO;
using System.IO.Compression;

// CLI mode
if (args.Length > 0 && args[0] == "chunk")
{
    return await CliCommands.RunChunkerAsync(args[1..]);
}

// Logging
var logPath = Path.Combine(AppContext.BaseDirectory, "server.log");
File.WriteAllText(logPath, $"=== Server Log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");

void Log(string message)
{
    var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + "\n"); } catch { }
}

Log("Server starting...");

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
    options.Limits.MaxRequestBodySize = null;
});

var serverConfig = new
{
    IP = builder.Configuration["Server:IP"] ?? "0.0.0.0",
    Port = builder.Configuration.GetValue<int>("Server:Port", 5000),
    Version = "2.0.0" // Server API version, not game version
};

var gameConfig = new
{
    ModelId = builder.Configuration["Game:ModelId"],
    Name = builder.Configuration["Game:Name"] ?? "Arma Reforger"
};

builder.Services.AddSingleton<LocalStorageService>();
builder.Services.AddMemoryCache();
builder.Services.Configure<IpRateLimitOptions>(o => o.GeneralRules = [new() { Endpoint = "*", Period = "1s", Limit = 1000 }]);
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
builder.Services.AddInMemoryRateLimiting();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
    .WithExposedHeaders("Content-Length", "Content-Disposition", "X-File-Size")));

// Add response compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["application/json", "application/octet-stream", "text/plain"];
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options => 
    options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => 
    options.Level = CompressionLevel.Fastest);

var app = builder.Build();
app.UseCors();
app.UseResponseCompression(); // Enable compression
app.UseIpRateLimiting();

// ==================== API ====================

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/info", (LocalStorageService storage) =>
{
    var gameModel = string.IsNullOrWhiteSpace(gameConfig.ModelId)
        ? null
        : storage.GetModel(gameConfig.ModelId);

    return Results.Ok(new
    {
        version = serverConfig.Version,
        supportsDeltaUpdate = true,
        supportsCompression = true,
        modelsCount = storage.GetModels().Count(),
        game = gameModel == null ? null : new
        {
            id = gameModel.ModelId,
            name = string.IsNullOrWhiteSpace(gameModel.Name) ? gameConfig.Name : gameModel.Name,
            version = gameModel.Version,
            totalSize = gameModel.TotalSize,
            totalSizeFormatted = FormatBytes(gameModel.TotalSize)
        }
    });
});

// Game endpoint - returns game info separately
app.MapGet("/game", (LocalStorageService storage) =>
{
    if (string.IsNullOrWhiteSpace(gameConfig.ModelId))
        return Results.NotFound(new { error = "No game configured on this server" });

    var gameModel = storage.GetModel(gameConfig.ModelId);
    if (gameModel == null)
        return Results.NotFound(new { error = "Game model not found" });

    return Results.Ok(new
    {
        id = gameModel.ModelId,
        name = string.IsNullOrWhiteSpace(gameModel.Name) ? gameConfig.Name : gameModel.Name,
        version = gameModel.Version,
        totalSize = gameModel.TotalSize,
        totalSizeFormatted = FormatBytes(gameModel.TotalSize),
        fileCount = gameModel.FileCount
    });
});

// List models (fast) - excludes game
app.MapGet("/models", (LocalStorageService storage) =>
{
    Log("GET /models");
    var models = storage.GetModels()
        .Where(m => string.IsNullOrWhiteSpace(gameConfig.ModelId) ||
            !string.Equals(m.ModelId, gameConfig.ModelId, StringComparison.OrdinalIgnoreCase))
        .Select(m => new
        {
            id = m.ModelId,
            name = m.Name,
            version = m.Version,
            fileCount = m.FileCount,
            totalSize = m.TotalSize,
            totalSizeFormatted = FormatBytes(m.TotalSize)
        }).ToList();
    Log($"  -> {models.Count} models");
    return Results.Ok(new { models });
});

// ==================== ADDONS API ====================

// List all addons (mods from Addons folder with ServerData.json)
app.MapGet("/addons", (LocalStorageService storage) =>
{
    Log("GET /addons");
    var addons = storage.GetAddons()
        .Select(a => new
        {
            id = a.FolderName,           // ID для скачивания (имя папки)
            modId = a.ModId,             // Уникальный ID мода из ServerData.json  
            name = a.Name,
            version = a.Version,
            fileCount = a.FileCount,
            totalSize = a.TotalSize,
            totalSizeFormatted = FormatBytes(a.TotalSize),
            changelog = a.Changelog,
            dependencies = a.Dependencies?.Select(d => new
            {
                modId = d.ModId,
                name = d.Name,
                version = d.Version
            })
        }).ToList();
    Log($"  -> {addons.Count} addons");
    return Results.Ok(new { addons });
});

// Addon details with files
app.MapGet("/addons/{folderId}", (string folderId, LocalStorageService storage) =>
{
    Log($"GET /addons/{folderId}");
    
    var addon = storage.GetAddon(folderId);
    if (addon == null)
        return Results.NotFound(new { error = "Addon not found" });

    var files = storage.GetAddonFilesQuick(folderId);
    
    var fileInfos = files.Select(f => new
    {
        path = f.Path,
        size = f.Size,
        hash = (string?)null,
        isPak = f.IsPak,
        chunksReady = f.IsPak && storage.GetAddonChunkCount(folderId, f.Path) > 0,
        chunkCount = f.IsPak ? storage.GetAddonChunkCount(folderId, f.Path) : 0
    }).ToList();

    Log($"  -> {files.Count} files");

    return Results.Ok(new
    {
        id = addon.FolderName,
        modId = addon.ModId,
        name = addon.Name,
        version = addon.Version,
        totalSize = addon.TotalSize,
        totalSizeFormatted = FormatBytes(addon.TotalSize),
        fileCount = files.Count,
        changelog = addon.Changelog,
        dependencies = addon.Dependencies?.Select(d => new
        {
            modId = d.ModId,
            name = d.Name,
            version = d.Version
        }),
        files = fileInfos
    });
});

// Download addon file
app.MapGet("/addons/{folderId}/files/{**filePath}", async (string folderId, string filePath, LocalStorageService storage, HttpContext ctx) =>
{
    var addon = storage.GetAddon(folderId);
    if (addon == null) return Results.NotFound();
    
    var fullPath = Path.Combine(addon.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(fullPath)) return Results.NotFound();

    var fileInfo = new FileInfo(fullPath);
    ctx.Response.Headers["X-File-Size"] = fileInfo.Length.ToString();
    ctx.Response.Headers["Accept-Ranges"] = "bytes";
    
    return Results.File(fullPath, "application/octet-stream", enableRangeProcessing: true);
});

// ==================== MODELS API ====================

// Model details - NO CHUNKS in response! (fast)
app.MapGet("/models/{modelId}", (string modelId, LocalStorageService storage) =>
{
    Log($"GET /models/{modelId}");
    
    var model = storage.GetModel(modelId);
    if (model == null)
        return Results.NotFound(new { error = "Model not found" });

    var files = storage.GetModelFilesQuick(modelId);
    
    // DON'T include chunks here - they're too large!
    // Client will request chunks separately via /chunks/{modelId}/{filePath}
    var fileInfos = files.Select(f => new
    {
        path = f.Path,
        size = f.Size,
        hash = (string?)null, // Not computed for performance
        isPak = f.IsPak,
        // Just indicate if chunks are ready, don't send them!
        chunksReady = f.IsPak && storage.GetChunkCount(modelId, f.Path) > 0,
        chunkCount = f.IsPak ? storage.GetChunkCount(modelId, f.Path) : 0
    }).ToList();

    Log($"  -> {files.Count} files");

    return Results.Ok(new
    {
        id = model.ModelId,
        name = model.Name,
        version = model.Version,
        totalSize = model.TotalSize,
        totalSizeFormatted = FormatBytes(model.TotalSize),
        fileCount = files.Count,
        supportsDelta = true,
        files = fileInfos
    });
});

app.MapGet("/models/{modelId}/latest", (string modelId, LocalStorageService storage) =>
{
    var model = storage.GetModel(modelId);
    return model == null 
        ? Results.NotFound(new { error = "Model not found" }) 
        : Results.Ok(new { modelId, latestVersion = model.Version });
});

// File list
app.MapGet("/files/{modelId}", (string modelId, LocalStorageService storage) =>
{
    var model = storage.GetModel(modelId);
    if (model == null) return Results.NotFound(new { error = "Model not found" });

    return Results.Ok(new { modelId, files = storage.GetModelFilesQuick(modelId).Select(f => new
    {
        path = f.Path,
        size = f.Size,
        isPak = f.IsPak,
        downloadUrl = $"/files/{modelId}/{f.Path}"
    })});
});

// Download file - with Range request support for parallel downloads
app.MapGet("/files/{modelId}/{**filePath}", async (string modelId, string filePath, LocalStorageService storage, HttpContext ctx, CancellationToken ct) =>
{
    var model = storage.GetModel(modelId);
    if (model == null) { ctx.Response.StatusCode = 404; return; }
    
    var fullPath = Path.Combine(model.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(fullPath)) { ctx.Response.StatusCode = 404; return; }

    var fileInfo = new FileInfo(fullPath);
    var fileLength = fileInfo.Length;
    
    // Check for Range header (partial content request)
    var rangeHeader = ctx.Request.Headers.Range.FirstOrDefault();
    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
    {
        // Parse range: "bytes=0-1023" or "bytes=0-"
        var rangeValue = rangeHeader["bytes=".Length..];
        var parts = rangeValue.Split('-');
        
        if (parts.Length == 2)
        {
            var start = long.TryParse(parts[0], out var s) ? s : 0;
            var end = long.TryParse(parts[1], out var e) ? e : fileLength - 1;
            
            // Validate range
            if (start >= 0 && start < fileLength && end >= start && end < fileLength)
            {
                var length = end - start + 1;
                
                ctx.Response.StatusCode = 206; // Partial Content
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = length;
                ctx.Response.Headers.Append("Content-Range", $"bytes {start}-{end}/{fileLength}");
                ctx.Response.Headers.Append("Accept-Ranges", "bytes");
                
                await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                fs.Seek(start, SeekOrigin.Begin);
                
                var buffer = new byte[256 * 1024];
                var remaining = length;
                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
                    if (read == 0) break;
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
                return;
            }
        }
    }
    
    // Full file download
    Log($"GET /files/{modelId}/{filePath}");
    Log($"  Streaming: {FormatBytes(fileLength)}");

    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength = fileLength;
    ctx.Response.Headers.Append("Accept-Ranges", "bytes");
    ctx.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(filePath)}\"");
    
    try { await storage.StreamFileAsync(fullPath, ctx.Response.Body, ct); Log($"  Done"); }
    catch (Exception ex) { Log($"  Error: {ex.Message}"); }
});

// ==================== CHUNKS API (for delta updates) ====================

// Get chunks for a specific file
app.MapGet("/chunks/{modelId}/{**filePath}", async (string modelId, string filePath, LocalStorageService storage, CancellationToken ct) =>
{
    Log($"GET /chunks/{modelId}/{filePath}");
    var chunks = await storage.GetFileChunksAsync(modelId, filePath, ct);
    
    if (chunks.Count == 0)
        return Results.NotFound(new { error = "Chunks not ready" });
    
    Log($"  -> {chunks.Count} chunks");
    return Results.Ok(new { modelId, filePath, chunkCount = chunks.Count, 
        chunks = chunks.Select(c => new { c.Index, c.Offset, c.Size, c.Hash }) });
});

// Download a specific chunk - with optional compression for compressible data
app.MapGet("/chunk/{modelId}/{offset}/{size}/{**filePath}", async (string modelId, long offset, int size, string filePath, LocalStorageService storage, HttpContext ctx) =>
{
    var bytes = storage.GetFileChunk(modelId, filePath, offset, size);
    if (bytes == null) { ctx.Response.StatusCode = 404; return; }

    // Check if client accepts compression
    var acceptEncoding = ctx.Request.Headers.AcceptEncoding.ToString();
    var useCompression = bytes.Length > 4096 && // Only compress if larger than 4KB
                         (acceptEncoding.Contains("br") || acceptEncoding.Contains("gzip"));
    
    if (useCompression && acceptEncoding.Contains("br"))
    {
        // Use Brotli (best compression for this use case)
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
        {
            brotli.Write(bytes);
        }
        var compressed = output.ToArray();
        
        // Only use compression if it actually reduces size
        if (compressed.Length < bytes.Length * 0.9)
        {
            ctx.Response.Headers.ContentEncoding = "br";
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = compressed.Length;
            ctx.Response.Headers.Append("X-Original-Size", bytes.Length.ToString());
            await ctx.Response.Body.WriteAsync(compressed);
            return;
        }
    }
    else if (useCompression && acceptEncoding.Contains("gzip"))
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(bytes);
        }
        var compressed = output.ToArray();
        
        if (compressed.Length < bytes.Length * 0.9)
        {
            ctx.Response.Headers.ContentEncoding = "gzip";
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = compressed.Length;
            ctx.Response.Headers.Append("X-Original-Size", bytes.Length.ToString());
            await ctx.Response.Body.WriteAsync(compressed);
            return;
        }
    }

    // No compression or compression wasn't beneficial
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength = bytes.Length;
    await ctx.Response.Body.WriteAsync(bytes);
});

// ==================== SPEED TEST ====================

// Pre-generate random data for speed tests (avoid generating on each request)
var speedTestData = new byte[1024 * 1024]; // 1MB reusable chunk
Random.Shared.NextBytes(speedTestData);

// Speed test endpoint - streams data for accurate speed measurement
// Optimized for parallel connections
app.MapGet("/speedtest", async (HttpContext ctx, CancellationToken ct) =>
{
    var totalSize = 50 * 1024 * 1024; // 50MB
    
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength = totalSize;
    ctx.Response.Headers.Append("Cache-Control", "no-cache, no-store");
    ctx.Response.Headers.Append("X-SpeedTest", "true");
    
    var sent = 0;
    while (sent < totalSize && !ct.IsCancellationRequested)
    {
        var toSend = Math.Min(speedTestData.Length, totalSize - sent);
        await ctx.Response.Body.WriteAsync(speedTestData.AsMemory(0, toSend), ct);
        sent += toSend;
    }
});

// Speed test with custom size (1-100 MB)
app.MapGet("/speedtest/{sizeMb:int}", async (int sizeMb, HttpContext ctx, CancellationToken ct) =>
{
    sizeMb = Math.Clamp(sizeMb, 1, 100);
    var totalSize = sizeMb * 1024 * 1024;
    
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.ContentLength = totalSize;
    ctx.Response.Headers.Append("Cache-Control", "no-cache, no-store");
    ctx.Response.Headers.Append("X-SpeedTest", "true");
    
    var sent = 0;
    while (sent < totalSize && !ct.IsCancellationRequested)
    {
        var toSend = Math.Min(speedTestData.Length, totalSize - sent);
        await ctx.Response.Body.WriteAsync(speedTestData.AsMemory(0, toSend), ct);
        sent += toSend;
    }
});

// ==================== ADMIN ====================

app.MapPost("/admin/refresh", (LocalStorageService storage) =>
{
    Log("POST /admin/refresh");
    storage.ScanModelsFolder();
    return Results.Ok(new { success = true, modelsCount = storage.GetModels().Count() });
});

app.MapPost("/admin/refresh/{modelId}", async (string modelId, LocalStorageService storage, CancellationToken ct) =>
{
    Log($"POST /admin/refresh/{modelId}");
    await storage.RefreshModelAsync(modelId, ct);
    return Results.Ok(new { success = true, modelId });
});

app.MapGet("/admin/status", (LocalStorageService storage) => Results.Ok(new
{
    models = storage.GetModels().Select(m => new
    {
        id = m.ModelId,
        version = m.Version,
        size = FormatBytes(m.TotalSize),
        pakFiles = storage.GetModelFilesQuick(m.ModelId).Where(f => f.IsPak).Select(f => new
        {
            path = f.Path,
            size = FormatBytes(f.Size),
            chunksReady = storage.GetChunkCount(m.ModelId, f.Path) > 0,
            chunkCount = storage.GetChunkCount(m.ModelId, f.Path)
        })
    })
}));

// ==================== STARTUP ====================

var storage = app.Services.GetRequiredService<LocalStorageService>();
var models = storage.GetModels().ToList();
var addons = storage.GetAddons().ToList();

Log("═══════════════════════════════════════════════════════════");
Log($"  Arma Launcher Server v{serverConfig.Version}");
Log($"  Listening: http://{serverConfig.IP}:{serverConfig.Port}");
Log($"  Compression: Brotli + GZip enabled");
Log($"  Models: {models.Count}");
foreach (var m in models)
    Log($"    - {m.ModelId}: {m.FileCount} files, {FormatBytes(m.TotalSize)}");

Log($"  Addons: {addons.Count}");
foreach (var a in addons)
    Log($"    - {a.Name} [{a.ModId}] v{a.Version}: {a.FileCount} files, {FormatBytes(a.TotalSize)}");

if (!string.IsNullOrWhiteSpace(gameConfig.ModelId))
{
    var gameModel = models.FirstOrDefault(m => string.Equals(m.ModelId, gameConfig.ModelId, StringComparison.OrdinalIgnoreCase));
    if (gameModel != null)
    {
        var displayName = string.IsNullOrWhiteSpace(gameModel.Name) ? gameConfig.Name : gameModel.Name;
        Log($"  Game Target: {displayName} ({gameModel.ModelId}) v{gameModel.Version}");
    }
    else
    {
        Log($"  Game Target: '{gameConfig.ModelId}' not found in storage");
    }
}

Log("═══════════════════════════════════════════════════════════");

app.Run();
return 0;

static string FormatBytes(long bytes)
{
    if (bytes == 0) return "0 B";
    string[] suffixes = ["B", "KB", "MB", "GB"];
    int i = 0;
    double size = bytes;
    while (size >= 1024 && i < suffixes.Length - 1) { size /= 1024; i++; }
    return $"{size:F2} {suffixes[i]}";
}