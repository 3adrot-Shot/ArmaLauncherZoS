using System.CommandLine;
using System.Diagnostics;
using System.IO;
using LauncherServer.Services;
using LauncherServer.Models;
using MessagePack;

namespace LauncherServer.Cli;

/// <summary>
/// CLI commands for content preparation
/// </summary>
public static class CliCommands
{
    public static async Task<int> RunChunkerAsync(string[] args)
    {
        var inputOption = new Option<string>(
            ["--input", "-i"],
            "Input file or directory to chunk")
        { IsRequired = true };

        var outputOption = new Option<string>(
            ["--output", "-o"],
            "Output directory for chunks")
        { IsRequired = true };

        var modelIdOption = new Option<string>(
            ["--model-id", "-m"],
            "Model identifier")
        { IsRequired = true };

        var versionOption = new Option<string>(
            ["--version", "-v"],
            "Model version")
        { IsRequired = true };

        var compressionOption = new Option<int>(
            ["--compression", "-c"],
            () => 3,
            "Zstandard compression level (1-22)");

        var rootCommand = new RootCommand("AI Model Chunker - Prepare models for distribution")
        {
            inputOption,
            outputOption,
            modelIdOption,
            versionOption,
            compressionOption
        };

        rootCommand.SetHandler(async (input, output, modelId, version, compression) =>
        {
            await ChunkModelAsync(input, output, modelId, version, compression);
        }, inputOption, outputOption, modelIdOption, versionOption, compressionOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task ChunkModelAsync(
        string input,
        string output,
        string modelId,
        string version,
        int compressionLevel)
    {
        Console.WriteLine($"????????????????????????????????????????????????????????????????");
        Console.WriteLine($"?              AI Model Chunker v1.0                           ?");
        Console.WriteLine($"????????????????????????????????????????????????????????????????");
        Console.WriteLine();
        Console.WriteLine($"Model ID:    {modelId}");
        Console.WriteLine($"Version:     {version}");
        Console.WriteLine($"Input:       {input}");
        Console.WriteLine($"Output:      {output}");
        Console.WriteLine($"Compression: Zstd level {compressionLevel}");
        Console.WriteLine();

        Directory.CreateDirectory(output);
        var chunksDir = Path.Combine(output, "chunks");
        Directory.CreateDirectory(chunksDir);

        var chunker = new ChunkerService(compressionLevel);
        var crypto = new CryptoService(Path.Combine(output, "signing.key"));

        var files = new List<FileEntry>();
        var uniqueChunks = new Dictionary<string, ChunkInfo>();
        long totalSize = 0;
        long totalCompressedSize = 0;
        var stopwatch = Stopwatch.StartNew();

        var inputFiles = Directory.Exists(input)
            ? Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).OrderBy(f => f).ToList()
            : [input];

        var basePath = Directory.Exists(input) ? input : Path.GetDirectoryName(input)!;

        foreach (var filePath in inputFiles)
        {
            var relativePath = Path.GetRelativePath(basePath, filePath);
            var fileInfo = new FileInfo(filePath);

            Console.WriteLine($"Processing: {relativePath} ({FormatBytes(fileInfo.Length)})");

            var chunks = new List<ChunkInfo>();
            var chunkCount = 0;
            var newChunks = 0;

            await foreach (var chunk in chunker.ChunkFileAsync(filePath))
            {
                chunkCount++;

                if (!uniqueChunks.ContainsKey(chunk.Hash))
                {
                    var chunkPath = Path.Combine(chunksDir, chunk.Hash[..2], chunk.Hash[2..4], chunk.Hash);
                    Directory.CreateDirectory(Path.GetDirectoryName(chunkPath)!);
                    await File.WriteAllBytesAsync(chunkPath, chunk.CompressedData);

                    uniqueChunks[chunk.Hash] = new ChunkInfo
                    {
                        Hash = chunk.Hash,
                        Offset = chunk.Offset,
                        Size = chunk.OriginalSize,
                        CompressedSize = chunk.CompressedSize,
                        Compression = chunk.Compression
                    };

                    totalCompressedSize += chunk.CompressedSize;
                    newChunks++;
                }

                chunks.Add(new ChunkInfo
                {
                    Hash = chunk.Hash,
                    Offset = chunk.Offset,
                    Size = chunk.OriginalSize,
                    CompressedSize = uniqueChunks[chunk.Hash].CompressedSize,
                    Compression = chunk.Compression
                });
            }

            using var fileStream = File.OpenRead(filePath);
            var fileHash = await CryptoService.HashSha256Async(fileStream);

            files.Add(new FileEntry
            {
                Path = relativePath.Replace('\\', '/'),
                Size = fileInfo.Length,
                Hash = fileHash,
                Chunks = chunks,
                Type = DetectFileType(relativePath)
            });

            totalSize += fileInfo.Length;

            var dedup = chunkCount > 0 ? 100.0 * (chunkCount - newChunks) / chunkCount : 0;
            Console.WriteLine($"  ? {chunkCount} chunks, {newChunks} new, {dedup:F1}% deduplicated");
        }

        stopwatch.Stop();

        var manifest = new Manifest
        {
            ModelId = modelId,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            TotalSize = totalSize,
            TotalCompressedSize = totalCompressedSize,
            Files = files,
            FormatVersion = 1
        };

        var manifestData = MessagePackSerializer.Serialize(manifest);
        var signature = crypto.Sign(manifestData);

        var signedManifest = new SignedManifest
        {
            ManifestData = manifestData,
            Signature = signature,
            KeyId = crypto.KeyId,
            SignedAt = DateTimeOffset.UtcNow
        };

        var manifestPath = Path.Combine(output, $"{modelId}-{version}.manifest");
        await File.WriteAllBytesAsync(manifestPath, MessagePackSerializer.Serialize(signedManifest));

        crypto.ExportPublicKey(Path.Combine(output, "signing.pub"));

        Console.WriteLine();
        Console.WriteLine($"????????????????????????????????????????????????????????????????");
        Console.WriteLine($"?                         Summary                              ?");
        Console.WriteLine($"????????????????????????????????????????????????????????????????");
        Console.WriteLine($"Files processed:     {files.Count}");
        Console.WriteLine($"Total size:          {FormatBytes(totalSize)}");
        Console.WriteLine($"Compressed size:     {FormatBytes(totalCompressedSize)}");
        Console.WriteLine($"Compression ratio:   {100.0 * totalCompressedSize / totalSize:F1}%");
        Console.WriteLine($"Unique chunks:       {uniqueChunks.Count}");
        Console.WriteLine($"Time elapsed:        {stopwatch.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Speed:               {FormatBytes((long)(totalSize / stopwatch.Elapsed.TotalSeconds))}/s");
        Console.WriteLine();
        Console.WriteLine($"Manifest:            {manifestPath}");
        Console.WriteLine($"Public key:          {Path.Combine(output, "signing.pub")}");
        Console.WriteLine($"Key ID:              {crypto.KeyId}");

        crypto.Dispose();
    }

    private static FileType DetectFileType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".gguf" => FileType.Gguf,
            ".safetensors" => FileType.SafeTensors,
            ".pth" or ".pt" or ".bin" => FileType.Pth,
            ".json" => FileType.Json,
            ".txt" or ".md" => FileType.Text,
            _ => FileType.Binary
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;

        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:F2} {suffixes[i]}";
    }
}
