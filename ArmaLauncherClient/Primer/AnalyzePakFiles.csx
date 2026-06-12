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

// Compare byte-by-byte to find where changes occur
Console.WriteLine("Analyzing byte-level differences...");

using var oldStream = File.OpenRead(oldPath);
using var newStream = File.OpenRead(newPath);

const int bufferSize = 1024 * 1024; // 1MB buffers
var oldBuffer = new byte[bufferSize];
var newBuffer = new byte[bufferSize];

long position = 0;
long totalDifferentBytes = 0;
var differentRegions = new List<(long Start, long End)>();
long? currentDiffStart = null;

long minLength = Math.Min(oldInfo.Length, newInfo.Length);

while (position < minLength)
{
    var toRead = (int)Math.Min(bufferSize, minLength - position);
    var oldRead = oldStream.Read(oldBuffer, 0, toRead);
    var newRead = newStream.Read(newBuffer, 0, toRead);
    
    for (int i = 0; i < oldRead; i++)
    {
        if (oldBuffer[i] != newBuffer[i])
        {
            totalDifferentBytes++;
            if (currentDiffStart == null)
                currentDiffStart = position + i;
        }
        else
        {
            if (currentDiffStart != null)
            {
                differentRegions.Add((currentDiffStart.Value, position + i));
                currentDiffStart = null;
            }
        }
    }
    
    position += oldRead;
    
    if (position % (100 * 1024 * 1024) == 0)
        Console.WriteLine($"  Analyzed {position / 1024 / 1024} MB...");
}

if (currentDiffStart != null)
    differentRegions.Add((currentDiffStart.Value, position));

Console.WriteLine();
Console.WriteLine($"=== Results ===");
Console.WriteLine($"Common length: {minLength:N0} bytes ({minLength / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"Different bytes in common area: {totalDifferentBytes:N0} ({totalDifferentBytes * 100.0 / minLength:F2}%)");
Console.WriteLine($"Number of different regions: {differentRegions.Count}");
Console.WriteLine();

// Merge nearby regions (within 4KB)
var mergedRegions = new List<(long Start, long End, long Size)>();
if (differentRegions.Count > 0)
{
    var current = differentRegions[0];
    foreach (var region in differentRegions.Skip(1))
    {
        if (region.Start - current.End < 4096) // Merge if within 4KB
        {
            current = (current.Start, region.End);
        }
        else
        {
            mergedRegions.Add((current.Start, current.End, current.End - current.Start));
            current = region;
        }
    }
    mergedRegions.Add((current.Start, current.End, current.End - current.Start));
}

Console.WriteLine($"Merged different regions (within 4KB): {mergedRegions.Count}");
Console.WriteLine();

// Show top 20 largest different regions
Console.WriteLine("Top 20 largest different regions:");
foreach (var region in mergedRegions.OrderByDescending(r => r.Size).Take(20))
{
    Console.WriteLine($"  Offset {region.Start:N0} - {region.End:N0}: {region.Size:N0} bytes ({region.Size / 1024.0:F1} KB)");
}

// Calculate theoretical minimum download
long theoreticalMinDownload = mergedRegions.Sum(r => r.Size);
if (newInfo.Length > oldInfo.Length)
    theoreticalMinDownload += (newInfo.Length - oldInfo.Length);

Console.WriteLine();
Console.WriteLine($"=== Theoretical Minimum Download ===");
Console.WriteLine($"Changed regions total: {mergedRegions.Sum(r => r.Size):N0} bytes ({mergedRegions.Sum(r => r.Size) / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"New data at end: {Math.Max(0, newInfo.Length - oldInfo.Length):N0} bytes");
Console.WriteLine($"Theoretical minimum: {theoreticalMinDownload:N0} bytes ({theoreticalMinDownload / 1024.0 / 1024.0:F2} MB)");
Console.WriteLine($"This is {theoreticalMinDownload * 100.0 / newInfo.Length:F1}% of full file");

// Test with different chunk sizes
Console.WriteLine();
Console.WriteLine("=== Testing Fixed Chunk Sizes ===");
foreach (var chunkSize in new[] { 256 * 1024, 512 * 1024, 1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024 })
{
    var (matching, different) = await TestFixedChunks(oldPath, newPath, chunkSize);
    var downloadSize = different * chunkSize;
    Console.WriteLine($"  {chunkSize / 1024}KB chunks: {matching} matching, {different} different -> {downloadSize / 1024.0 / 1024.0:F2} MB download ({downloadSize * 100.0 / newInfo.Length:F1}%)");
}

static async Task<(int Matching, int Different)> TestFixedChunks(string oldPath, string newPath, int chunkSize)
{
    var oldChunks = await ComputeFixedChunks(oldPath, chunkSize);
    var newChunks = await ComputeFixedChunks(newPath, chunkSize);
    
    var oldSet = oldChunks.ToHashSet();
    
    int matching = 0;
    int different = 0;
    
    foreach (var chunk in newChunks)
    {
        if (oldSet.Contains(chunk))
            matching++;
        else
            different++;
    }
    
    return (matching, different);
}

static async Task<List<string>> ComputeFixedChunks(string path, int chunkSize)
{
    var result = new List<string>();
    await using var stream = File.OpenRead(path);
    var buffer = new byte[chunkSize];
    
    while (true)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0) break;
        
        var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        result.Add(hash);
    }
    
    return result;
}
