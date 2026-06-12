using System.Security.Cryptography;

// Analyze two PAK files to find optimal chunking strategy

var oldPath = @"C:\Users\Administrator\source\repos\ArmaLauncher\ArmaLauncherClient\Primer\data_old.pak";
var newPath = @"C:\Users\Administrator\source\repos\ArmaLauncher\ArmaLauncherClient\Primer\data_new.pak";

Console.WriteLine("=== PAK File Analysis ===\n");

var oldInfo = new FileInfo(oldPath);
var newInfo = new FileInfo(newPath);

Console.WriteLine($"Old file: {oldInfo.Length:N0} bytes ({oldInfo.Length / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"New file: {newInfo.Length:N0} bytes ({newInfo.Length / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"Size diff: {newInfo.Length - oldInfo.Length:N0} bytes ({(newInfo.Length - oldInfo.Length) / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine();

Console.WriteLine("=== CDC Hash-Based Matching (What our launcher does) ===");
Console.WriteLine();

// Test with 1MB average (matching server settings)
var oldChunks = await ComputeCdcChunks(oldPath, 256*1024, 1024*1024, 4*1024*1024);
var newChunks = await ComputeCdcChunks(newPath, 256*1024, 1024*1024, 4*1024*1024);

Console.WriteLine($"Old file: {oldChunks.Count} chunks");
Console.WriteLine($"New file: {newChunks.Count} chunks");

// Build hash set from old file
var oldHashSet = oldChunks.Select(c => c.Hash).ToHashSet();

int matchingByHash = 0;
int needDownload = 0;
long bytesToDownload = 0;
long bytesFromLocal = 0;

foreach (var chunk in newChunks)
{
    if (oldHashSet.Contains(chunk.Hash))
    {
        matchingByHash++;
        bytesFromLocal += chunk.Size;
    }
    else
    {
        needDownload++;
        bytesToDownload += chunk.Size;
    }
}

Console.WriteLine();
Console.WriteLine($"Chunks matching by HASH: {matchingByHash}/{newChunks.Count} ({matchingByHash * 100.0 / newChunks.Count:F1}%)");
Console.WriteLine($"Chunks to download: {needDownload}");
Console.WriteLine($"Bytes to download: {bytesToDownload:N0} ({bytesToDownload / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"Bytes from local: {bytesFromLocal:N0} ({bytesFromLocal / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"Download is {bytesToDownload * 100.0 / newInfo.Length:F1}% of full file");

Console.WriteLine();
Console.WriteLine("=== Comparison with Position-Based (Old approach) ===");

// Position-based: compare chunks at same index
int matchingByPosition = 0;
long bytesToDownloadPosition = 0;

for (int i = 0; i < newChunks.Count; i++)
{
    if (i < oldChunks.Count && newChunks[i].Hash == oldChunks[i].Hash)
    {
        matchingByPosition++;
    }
    else
    {
        bytesToDownloadPosition += newChunks[i].Size;
    }
}

Console.WriteLine($"Position-based matching: {matchingByPosition}/{newChunks.Count}");
Console.WriteLine($"Position-based download: {bytesToDownloadPosition / 1024.0 / 1024.0:F2} MB ({bytesToDownloadPosition * 100.0 / newInfo.Length:F1}%)");

Console.WriteLine();
Console.WriteLine($"IMPROVEMENT: Hash-based saves {(bytesToDownloadPosition - bytesToDownload) / 1024.0 / 1024.0:F2} MB ({(1 - (double)bytesToDownload / bytesToDownloadPosition) * 100:F0}% reduction)");

static async Task<List<(string Hash, int Size, long Offset)>> ComputeCdcChunks(string path, int minSize, int avgSize, int maxSize)
{
    var result = new List<(string Hash, int Size, long Offset)>();
    var fileInfo = new FileInfo(path);
    
    if (fileInfo.Length == 0) return result;

    await using var stream = File.OpenRead(path);
    var buffer = new byte[maxSize];
    long fileOffset = 0;

    var gearTable = GenerateGearTable();
    var maskS = (1UL << (int)Math.Ceiling(Math.Log2(avgSize / 2))) - 1;
    var maskL = (1UL << (int)Math.Ceiling(Math.Log2(avgSize * 2))) - 1;

    while (fileOffset < fileInfo.Length)
    {
        var bytesRemaining = fileInfo.Length - fileOffset;
        var bytesToRead = (int)Math.Min(maxSize, bytesRemaining);
        
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead));
        if (bytesRead == 0) break;

        var chunkSize = FindChunkBoundary(buffer.AsSpan(0, bytesRead), minSize, avgSize, maxSize, gearTable, maskS, maskL);
        
        var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, chunkSize))).ToLowerInvariant();
        result.Add((hash, chunkSize, fileOffset));

        if (chunkSize < bytesRead)
        {
            stream.Seek(fileOffset + chunkSize, SeekOrigin.Begin);
        }

        fileOffset += chunkSize;
    }

    return result;
}

static int FindChunkBoundary(ReadOnlySpan<byte> data, int minSize, int avgSize, int maxSize, ulong[] gearTable, ulong maskS, ulong maskL)
{
    int length = data.Length;
    
    if (length <= minSize)
        return length;

    int i = minSize;
    ulong hash = 0;

    int normalPoint = Math.Min(i + (avgSize - minSize) / 2, length);
    int endPoint = Math.Min(maxSize, length);

    for (; i < normalPoint; i++)
    {
        hash = (hash << 1) + gearTable[data[i]];
        if ((hash & maskS) == 0)
            return i;
    }

    for (; i < endPoint; i++)
    {
        hash = (hash << 1) + gearTable[data[i]];
        if ((hash & maskL) == 0)
            return i;
    }

    return endPoint;
}

static ulong[] GenerateGearTable()
{
    var table = new ulong[256];
    var random = new Random(0x1337BEEF); // MUST MATCH SERVER
    
    var bytes = new byte[8];
    for (int i = 0; i < 256; i++)
    {
        random.NextBytes(bytes);
        table[i] = BitConverter.ToUInt64(bytes);
    }
    
    return table;
}
