using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LauncherServer.Models;
using MessagePack;

namespace LauncherServer.Services;

/// <summary>
/// File-based storage service with background chunk precomputation
/// </summary>
public sealed class LocalStorageService : IDisposable
{
    private readonly string _basePath;
    private readonly string _modelsPath;
    private readonly string _gamePath;
    private readonly string _addonsPath;
    private readonly string? _chunksPath;
    private readonly HashSet<string> _ignoredFolders;
    private readonly HashSet<string> _ignoredFiles;
    private readonly ILogger<LocalStorageService> _logger;

    private readonly ConcurrentDictionary<string, ModelInfo> _modelIndex = new();

    // Отдельный индекс для аддонов (модов) с информацией из ServerData.json
    private readonly ConcurrentDictionary<string, AddonInfo> _addonIndex = new();

    // In-memory chunk cache (key = fullPath)
    private readonly ConcurrentDictionary<string, List<FileChunkInfo>> _chunkCache = new();

    // File hash cache (key = path|size|modified)
    private readonly ConcurrentDictionary<string, string> _hashCache = new();

    // Background worker
    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;


    public string GamePath => _gamePath;
    public string AddonsPath => _addonsPath;

    /// <summary>
    /// Читает версию игры из метаданных exe файла (FileVersionInfo.ProductVersion)
    /// </summary>
    public string? GetGameVersionFromExe(string gamePath)
    {
        var exePath = Path.Combine(gamePath, "ArmaReforgerSteam.exe");

        if (!File.Exists(exePath))
        {
            _logger.LogWarning("Game exe not found: {Path}", exePath);
            return null;
        }

        try
        {
            var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var productVersion = versionInfo.ProductVersion;

            _logger.LogInformation("Exe version info: ProductVersion={ProductVersion}, FileVersion={Major}.{Minor}.{Build}.{Private}", 
                productVersion, versionInfo.FileMajorPart, versionInfo.FileMinorPart, versionInfo.FileBuildPart, versionInfo.FilePrivatePart);

            if (!string.IsNullOrWhiteSpace(productVersion))
                return productVersion.Trim();

            // Fallback to FileVersion if ProductVersion is empty
            if (versionInfo.FileMajorPart > 0)
                return $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}.{versionInfo.FilePrivatePart}";

            // На Linux/macOS FileVersionInfo не умеет читать ресурсы версии Windows PE,
            // поэтому парсим PE-файл вручную (кросс-платформенно)
            var peVersion = ReadVersionFromPeResource(exePath);
            if (!string.IsNullOrWhiteSpace(peVersion))
            {
                _logger.LogInformation("Exe version from PE resource: {Version}", peVersion);
                return peVersion;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read exe version from {Path}", exePath);
            return null;
        }
    }

    /// <summary>
    /// Кросс-платформенное чтение версии из ресурса VS_VERSION_INFO PE-файла.
    /// Работает на Linux/macOS, где FileVersionInfo не читает Windows-ресурсы.
    /// Читает поля dwFileVersion из структуры VS_FIXEDFILEINFO.
    /// </summary>
    private string? ReadVersionFromPeResource(string exePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(exePath);

            // DOS header: проверяем сигнатуру "MZ" и читаем e_lfanew (offset 0x3C)
            if (bytes.Length < 0x40 || bytes[0] != 'M' || bytes[1] != 'Z')
                return null;

            int peOffset = BitConverter.ToInt32(bytes, 0x3C);
            if (peOffset <= 0 || peOffset + 24 > bytes.Length)
                return null;

            // PE signature "PE\0\0"
            if (bytes[peOffset] != 'P' || bytes[peOffset + 1] != 'E' || bytes[peOffset + 2] != 0 || bytes[peOffset + 3] != 0)
                return null;

            int coffOffset = peOffset + 4;
            int numberOfSections = BitConverter.ToUInt16(bytes, coffOffset + 2);
            int sizeOfOptionalHeader = BitConverter.ToUInt16(bytes, coffOffset + 16);
            int optionalHeaderOffset = coffOffset + 20;

            // Magic: 0x10B = PE32, 0x20B = PE32+
            ushort magic = BitConverter.ToUInt16(bytes, optionalHeaderOffset);
            // Data directories начинаются по-разному для PE32 и PE32+
            int dataDirectoryOffset = optionalHeaderOffset + (magic == 0x20B ? 112 : 96);

            // Resource directory — индекс 2 в data directories (по 8 байт каждая)
            int resourceDirEntry = dataDirectoryOffset + 2 * 8;
            if (resourceDirEntry + 8 > bytes.Length)
                return null;

            uint resourceRva = BitConverter.ToUInt32(bytes, resourceDirEntry);
            if (resourceRva == 0)
                return null;

            // Section headers идут сразу после optional header
            int sectionHeadersOffset = optionalHeaderOffset + sizeOfOptionalHeader;

            // Функция перевода RVA -> файловое смещение
            int RvaToOffset(uint rva)
            {
                for (int i = 0; i < numberOfSections; i++)
                {
                    int sh = sectionHeadersOffset + i * 40;
                    if (sh + 40 > bytes.Length) break;
                    uint virtualSize = BitConverter.ToUInt32(bytes, sh + 8);
                    uint virtualAddress = BitConverter.ToUInt32(bytes, sh + 12);
                    uint rawSize = BitConverter.ToUInt32(bytes, sh + 16);
                    uint rawPointer = BitConverter.ToUInt32(bytes, sh + 20);
                    uint size = Math.Max(virtualSize, rawSize);
                    if (rva >= virtualAddress && rva < virtualAddress + size)
                        return (int)(rawPointer + (rva - virtualAddress));
                }
                return -1;
            }

            int resourceBase = RvaToOffset(resourceRva);
            if (resourceBase < 0)
                return null;

            // Ищем сигнатуру VS_FIXEDFILEINFO (0xFEEF04BD) в секции ресурсов.
            // Это надёжнее, чем полный обход дерева ресурсов.
            for (int i = resourceBase; i < bytes.Length - 4; i++)
            {
                if (bytes[i] == 0xBD && bytes[i + 1] == 0x04 && bytes[i + 2] == 0xEF && bytes[i + 3] == 0xFE)
                {
                    // VS_FIXEDFILEINFO:
                    //   +0  dwSignature (0xFEEF04BD)
                    //   +4  dwStrucVersion
                    //   +8  dwFileVersionMS (HIWORD.LOWORD = Major.Minor)
                    //   +12 dwFileVersionLS (HIWORD.LOWORD = Build.Private)
                    int sig = i;
                    if (sig + 16 > bytes.Length) break;

                    uint fileVersionMS = BitConverter.ToUInt32(bytes, sig + 8);
                    uint fileVersionLS = BitConverter.ToUInt32(bytes, sig + 12);

                    int major = (int)(fileVersionMS >> 16);
                    int minor = (int)(fileVersionMS & 0xFFFF);
                    int build = (int)(fileVersionLS >> 16);
                    int priv = (int)(fileVersionLS & 0xFFFF);

                    if (major == 0 && minor == 0 && build == 0 && priv == 0)
                        continue; // пустая запись, ищем дальше

                    return $"{major}.{minor}.{build}.{priv}";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse PE version resource from {Path}", exePath);
            return null;
        }
    }

    public LocalStorageService(IConfiguration config, ILogger<LocalStorageService> logger)
    {
        _logger = logger;

        var basePath = config["Storage:BasePath"];
        var modelsPath = config["Storage:ModelsPath"];

        _basePath = string.IsNullOrWhiteSpace(basePath) 
            ? Path.Combine(AppContext.BaseDirectory, "data") 
            : basePath;

        _modelsPath = string.IsNullOrWhiteSpace(modelsPath) 
            ? Path.Combine(_basePath, "models") 
            : modelsPath;

        _gamePath = config["Storage:GamePath"] ?? "";
        _addonsPath = config["Storage:AddonsPath"] ?? "";

        // Отдельная папка для чанков (если не указана - чанки только в памяти)
        var chunksPath = config["Storage:ChunksPath"];
        _chunksPath = string.IsNullOrWhiteSpace(chunksPath) ? null : chunksPath;

        if (_chunksPath != null)
        {
            Directory.CreateDirectory(_chunksPath);
            _logger.LogInformation("Chunks folder: {Path}", _chunksPath);
        }

        // Load ignored folders from config array
        var ignoredSection = config.GetSection("Storage:IgnoredFolders");
        _ignoredFolders = ignoredSection.Exists() 
            ? ignoredSection.Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []
            : [];

        // Load ignored files from config array
        var ignoredFilesSection = config.GetSection("Storage:IgnoredFiles");
        _ignoredFiles = ignoredFilesSection.Exists() 
            ? ignoredFilesSection.Get<string[]>()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []
            : [];

        if (_ignoredFolders.Count > 0)
        {
            _logger.LogInformation("Ignored folders: {Folders}", string.Join(", ", _ignoredFolders));
        }

        if (_ignoredFiles.Count > 0)
        {
            _logger.LogInformation("Ignored files: {Files}", string.Join(", ", _ignoredFiles));
        }

        if (!string.IsNullOrWhiteSpace(_modelsPath))
        {
            Directory.CreateDirectory(_modelsPath);
            _logger.LogInformation("Models folder: {Path}", _modelsPath);
        }
        
        if (!string.IsNullOrWhiteSpace(_gamePath))
        {
            _logger.LogInformation("Game folder: {Path}", _gamePath);
            if (!Directory.Exists(_gamePath))
            {
                _logger.LogWarning("Game folder does not exist: {Path}", _gamePath);
            }
        }
        
        if (!string.IsNullOrWhiteSpace(_addonsPath))
        {
            _logger.LogInformation("Addons folder: {Path}", _addonsPath);
            if (!Directory.Exists(_addonsPath))
            {
                _logger.LogWarning("Addons folder does not exist: {Path}", _addonsPath);
            }
        }

        ScanModelsFolder();
        ScanAddonsFolder();
        ScanGameFolder();
        LoadExistingChunkCaches();
        StartBackgroundWorker();
    }

    private void LoadExistingChunkCaches()
    {
        // Если ChunksPath не настроен - чанки только в памяти, нечего загружать
        if (_chunksPath == null)
        {
            _logger.LogInformation("ChunksPath not configured - chunks will be cached in memory only");
            return;
        }

        _logger.LogInformation("Loading chunk caches from {Path}...", _chunksPath);
        int loaded = 0;
        int invalid = 0;

        if (Directory.Exists(_chunksPath))
        {
            var chunkFiles = Directory.GetFiles(_chunksPath, "*.chunks", SearchOption.AllDirectories);
            _logger.LogInformation("Found {Count} .chunks files in chunks folder", chunkFiles.Length);

            foreach (var chunkFile in chunkFiles)
            {
                try
                {
                    var data = File.ReadAllBytes(chunkFile);
                    var entry = MessagePackSerializer.Deserialize<ChunkCacheEntry>(data);

                    // Проверяем что исходный файл существует и не изменился
                    if (!File.Exists(entry.SourcePath))
                    {
                        _logger.LogDebug("  Orphan chunk file (source missing): {File}", chunkFile);
                        invalid++;
                        continue;
                    }

                    var sourceInfo = new FileInfo(entry.SourcePath);
                    if (sourceInfo.Length != entry.SourceSize || 
                        sourceInfo.LastWriteTimeUtc.Ticks != entry.SourceModifiedUtc)
                    {
                        _logger.LogDebug("  Outdated chunk file: {File}", chunkFile);
                        invalid++;
                        continue;
                    }

                    _chunkCache[entry.SourcePath] = entry.Chunks;
                    loaded++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "  Failed to load: {File}", chunkFile);
                    invalid++;
                }
            }
        }

        _logger.LogInformation("Loaded {Loaded} chunk caches, {Invalid} invalid/outdated", loaded, invalid);
    }

    private void StartBackgroundWorker()
    {
        _backgroundTask = Task.Run(async () =>
        {
            _logger.LogInformation(">>> Background chunk generation STARTING <<<");

            try
            {
                // Process smaller models first
                foreach (var model in _modelIndex.Values.OrderBy(m => m.TotalSize))
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    await PrecomputeModelChunksAsync(model);
                }

                _logger.LogInformation(">>> Background chunk generation COMPLETE <<<");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Background worker cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background worker error");
            }
        });
    }

    private async Task PrecomputeModelChunksAsync(ModelInfo model)
    {
        try
        {
            var pakFiles = Directory.GetFiles(model.Path, "*.pak", SearchOption.AllDirectories);
            if (pakFiles.Length == 0) return;

            _logger.LogInformation("Processing: {Model} ({Count} .pak files)", model.ModelId, pakFiles.Length);

            foreach (var pakFile in pakFiles.OrderBy(f => new FileInfo(f).Length))
            {
                if (_cts.Token.IsCancellationRequested) break;

                var relativePath = Path.GetRelativePath(model.Path, pakFile).Replace('\\', '/');
                var pakInfo = new FileInfo(pakFile);
                var chunkFile = GetChunkFilePath(pakFile);

                // Already in memory?
                if (_chunkCache.ContainsKey(pakFile))
                {
                    // Make sure disk cache exists (only if ChunksPath configured)
                    if (chunkFile != null && !File.Exists(chunkFile))
                    {
                        await SaveChunkCacheAsync(pakFile, _chunkCache[pakFile], pakInfo, chunkFile);
                    }
                    continue;
                }

                // Check disk cache (only if ChunksPath configured)
                if (chunkFile != null && File.Exists(chunkFile))
                {
                    var loaded = await TryLoadChunkCacheAsync(chunkFile, pakFile, pakInfo);
                    if (loaded != null)
                    {
                        _chunkCache[pakFile] = loaded;
                        _logger.LogDebug("  From disk: {File} ({Count} chunks)", relativePath, loaded.Count);
                        continue;
                    }

                    // Invalid - delete
                    try { File.Delete(chunkFile); } catch { }
                }

                // Generate new chunks
                _logger.LogInformation("  GENERATING: {File} ({Size})...", 
                    relativePath, FormatBytes(pakInfo.Length));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var newChunks = await GenerateChunksAsync(pakFile, _cts.Token);
                sw.Stop();

                // Save to memory
                _chunkCache[pakFile] = newChunks;

                // Save to disk (only if ChunksPath configured)
                if (chunkFile != null)
                {
                    await SaveChunkCacheAsync(pakFile, newChunks, pakInfo, chunkFile);
                    _logger.LogInformation("  SAVED: {File} -> {Count} chunks in {Time:F1}s", 
                        relativePath, newChunks.Count, sw.Elapsed.TotalSeconds);
                }
                else
                {
                    _logger.LogInformation("  CACHED (memory only): {File} -> {Count} chunks in {Time:F1}s", 
                        relativePath, newChunks.Count, sw.Elapsed.TotalSeconds);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing model {Model}", model.ModelId);
        }
    }

    private async Task SaveChunkCacheAsync(string sourcePath, List<FileChunkInfo> chunks, FileInfo sourceInfo, string chunkFilePath)
    {
        try
        {
            var entry = new ChunkCacheEntry
            {
                SourcePath = sourcePath,
                SourceSize = sourceInfo.Length,
                SourceModifiedUtc = sourceInfo.LastWriteTimeUtc.Ticks,
                Chunks = chunks
            };

            var data = MessagePackSerializer.Serialize(entry);
            await File.WriteAllBytesAsync(chunkFilePath, data, _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save chunk cache: {File}", chunkFilePath);
        }
    }

    private async Task<List<FileChunkInfo>?> TryLoadChunkCacheAsync(string chunkFile, string sourcePath, FileInfo sourceInfo)
    {
        try
        {
            var data = await File.ReadAllBytesAsync(chunkFile, _cts.Token);

            // Try new format first
            try
            {
                var entry = MessagePackSerializer.Deserialize<ChunkCacheEntry>(data);

                // Validate
                if (entry.SourcePath != sourcePath || 
                    entry.SourceSize != sourceInfo.Length ||
                    entry.SourceModifiedUtc != sourceInfo.LastWriteTimeUtc.Ticks)
                {
                    return null;
                }

                return entry.Chunks;
            }
            catch
            {
                // Try old format (just list of chunks)
                var chunks = MessagePackSerializer.Deserialize<List<FileChunkInfo>>(data);
                if (chunks.Sum(c => (long)c.Size) != sourceInfo.Length)
                {
                    return null;
                }

                // Check for old algorithm
                if (chunks.Count >= 2)
                {
                    var avgSize = chunks.Average(c => c.Size);
                    var allSameSize = chunks.Take(chunks.Count - 1).All(c => Math.Abs(c.Size - 4 * 1024 * 1024) < 100000);
                    if (allSameSize && avgSize > 3.5 * 1024 * 1024)
                    {
                        _logger.LogWarning("  OLD ALGORITHM detected: {File} - will regenerate", chunkFile);
                        return null;
                    }
                }

                return chunks;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Возвращает путь к файлу чанков для указанного исходного файла.
    /// Возвращает null если ChunksPath не настроен (чанки только в памяти).
    /// </summary>
    private string? GetChunkFilePath(string sourceFilePath)
    {
        if (_chunksPath == null)
        {
            // Не создаём файлы чанков - только в памяти
            return null;
        }

        // Чанки в отдельной папке с сохранением структуры
        // Создаём уникальный путь на основе хэша полного пути
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sourceFilePath))).Substring(0, 16);
        var fileName = Path.GetFileName(sourceFilePath);

        // Структура: ChunksPath/AB/hash_filename.chunks
        var subDir = Path.Combine(_chunksPath, hash[..2]);
        Directory.CreateDirectory(subDir);

        return Path.Combine(subDir, $"{hash}_{fileName}.chunks");
    }

    private async Task<List<FileChunkInfo>> GenerateChunksAsync(string filePath, CancellationToken ct)
    {
        // Use FastCDC (Content-Defined Chunking) instead of fixed-size chunks
        // This dramatically reduces delta sizes when files are modified in the middle
        var cdcChunks = await FastCdcChunker.ChunkFileAsync(filePath, ct);
        
        return cdcChunks.Select(c => new FileChunkInfo
        {
            Index = c.Index,
            Offset = c.Offset,
            Size = c.Size,
            Hash = c.Hash
        }).ToList();
    }

    public void ScanModelsFolder()
    {
        if (string.IsNullOrWhiteSpace(_modelsPath) || !Directory.Exists(_modelsPath))
        {
            _logger.LogInformation("Models folder not configured or not found: {Path}", _modelsPath);
            return;
        }

        foreach (var modelDir in Directory.GetDirectories(_modelsPath))
        {
            try
            {
                var modelId = Path.GetFileName(modelDir);

                // Skip ignored folders
                if (_ignoredFolders.Contains(modelId))
                {
                    _logger.LogInformation("Skipping ignored folder: {Folder}", modelId);
                    continue;
                }

                var versionFile = Path.Combine(modelDir, "version.txt");
                var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "1.0.0";

                long totalSize = 0;
                int fileCount = 0;

                foreach (var file in Directory.GetFiles(modelDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var relativePath = Path.GetRelativePath(modelDir, file).Replace('\\', '/');

                    if (fileName == "version.txt" || fileName.EndsWith(".manifest") || fileName.EndsWith(".chunks"))
                        continue;

                    // Skip files in ignored folders
                    if (IsPathInIgnoredFolder(relativePath))
                        continue;

                    // Skip ignored files
                    if (IsFileIgnored(relativePath))
                        continue;

                    totalSize += new FileInfo(file).Length;
                    fileCount++;
                }

                _modelIndex[modelId] = new ModelInfo
                {
                    ModelId = modelId,
                    Name = modelId.Replace("-", " ").Replace("_", " "),
                    Version = version,
                    TotalSize = totalSize,
                    FileCount = fileCount,
                    Path = modelDir
                };

                _logger.LogInformation("Model: {ModelId} v{Version} ({FileCount} files, {Size})", 
                    modelId, version, fileCount, FormatBytes(totalSize));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning: {Dir}", modelDir);
            }
        }
    }

    /// <summary>
    /// Сканирует папку игры (если указана в GamePath)
    /// </summary>
    public void ScanGameFolder()
    {
        if (string.IsNullOrWhiteSpace(_gamePath) || !Directory.Exists(_gamePath))
        {
            _logger.LogInformation("Game folder not configured or not found: {Path}", _gamePath);
            return;
        }

        _logger.LogInformation("Scanning game folder: {Path}", _gamePath);

        try
        {
            // Используем имя папки как modelId
            var modelId = Path.GetFileName(_gamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            // Читаем версию из exe файла (приоритет)
            var version = GetGameVersionFromExe(_gamePath);

            // Fallback на version.txt если exe не найден
            if (string.IsNullOrWhiteSpace(version))
            {
                var versionFile = Path.Combine(_gamePath, "version.txt");
                version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "1.0.0";
                _logger.LogWarning("Exe version not found, using fallback: {Version}", version);
            }

            long totalSize = 0;
            int fileCount = 0;

            foreach (var file in Directory.GetFiles(_gamePath, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                var relativePath = Path.GetRelativePath(_gamePath, file).Replace('\\', '/');

                if (fileName == "version.txt" || fileName.EndsWith(".manifest") || fileName.EndsWith(".chunks"))
                    continue;

                // Skip files in ignored folders
                if (IsPathInIgnoredFolder(relativePath))
                    continue;

                // Skip ignored files
                if (IsFileIgnored(relativePath))
                    continue;

                totalSize += new FileInfo(file).Length;
                fileCount++;
            }

            _modelIndex[modelId] = new ModelInfo
            {
                ModelId = modelId,
                Name = "Arma Reforger",
                Version = version,
                TotalSize = totalSize,
                FileCount = fileCount,
                Path = _gamePath
            };

            _logger.LogInformation("Game: {ModelId} v{Version} ({FileCount} files, {Size})", 
                modelId, version, fileCount, FormatBytes(totalSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning game folder: {Dir}", _gamePath);
        }
    }

    /// <summary>
    /// Сканирует папку Addons и читает информацию из ServerData.json или meta файла
    /// Если оба отсутствуют - использует имя папки
    /// </summary>
    public void ScanAddonsFolder()
    {
        _addonIndex.Clear();
        
        if (string.IsNullOrWhiteSpace(_addonsPath) || !Directory.Exists(_addonsPath))
        {
            _logger.LogWarning("Addons folder not configured or not found: {Path}", _addonsPath);
            return;
        }
        
        _logger.LogInformation("Scanning addons folder: {Path}", _addonsPath);
        
        var directories = Directory.GetDirectories(_addonsPath);
        _logger.LogInformation("  Found {Count} directories in addons folder", directories.Length);
        
        foreach (var addonDir in directories)
        {
            try
            {
                var folderName = Path.GetFileName(addonDir);

                // Skip ignored folders
                if (_ignoredFolders.Contains(folderName))
                {
                    _logger.LogInformation("  Skipping ignored folder: {Folder}", folderName);
                    continue;
                }

                _logger.LogInformation("  Scanning: {Folder}", folderName);
                
                var serverDataPath = Path.Combine(addonDir, "ServerData.json");
                var metaPath = Path.Combine(addonDir, "meta");
                
                string modId;
                string name;
                string version;
                string? changelog = null;
                List<AddonDependency>? dependencies = null;
                
                if (File.Exists(serverDataPath))
                {
                    // Приоритет 1: ServerData.json
                    _logger.LogInformation("    Found ServerData.json");
                    var parsed = ParseServerDataJson(serverDataPath, folderName);
                    modId = parsed.ModId;
                    name = parsed.Name;
                    version = parsed.Version;
                    changelog = parsed.Changelog;
                    dependencies = parsed.Dependencies;
                }
                else if (File.Exists(metaPath))
                {
                    // Приоритет 2: meta файл (без расширения)
                    _logger.LogInformation("    Found meta file");
                    var parsed = ParseMetaFile(metaPath, folderName);
                    modId = parsed.ModId;
                    name = parsed.Name;
                    version = parsed.Version;
                    changelog = parsed.Changelog;
                    dependencies = parsed.Dependencies;
                }
                else
                {
                    // Приоритет 3: имя папки (формат: Name_ModId)
                    _logger.LogInformation("    No ServerData.json or meta - using folder name");
                    modId = ExtractModIdFromFolderName(folderName);
                    name = ExtractNameFromFolderName(folderName);
                    version = "unknown";
                }
                
                // Считаем размер и файлы
                long totalSize = 0;
                int fileCount = 0;

                foreach (var file in Directory.GetFiles(addonDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var relativePath = Path.GetRelativePath(addonDir, file).Replace('\\', '/');

                    if (fileName == "ServerData.json" || fileName == "meta" || fileName.EndsWith(".chunks"))
                        continue;

                    // Skip files in ignored folders
                    if (IsPathInIgnoredFolder(relativePath))
                        continue;

                    totalSize += new FileInfo(file).Length;
                    fileCount++;
                }

                var addon = new AddonInfo
                {
                    ModId = modId,
                    Name = name,
                    Version = version,
                    Path = addonDir,
                    FolderName = folderName,
                    TotalSize = totalSize,
                    FileCount = fileCount,
                    Changelog = changelog,
                    Dependencies = dependencies
                };
                
                _addonIndex[folderName] = addon;
                
                _logger.LogInformation("    -> {Name} [{ModId}] v{Version} ({FileCount} files, {Size})", 
                    addon.Name, addon.ModId, version, fileCount, FormatBytes(totalSize));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning addon: {Dir}", addonDir);
            }
        }
        
        _logger.LogInformation("Total addons found: {Count}", _addonIndex.Count);
    }
    
    /// <summary>
    /// Парсит ServerData.json
    /// </summary>
    private (string ModId, string Name, string Version, string? Changelog, List<AddonDependency>? Dependencies) 
        ParseServerDataJson(string path, string fallbackName)
    {
        try
        {
            var json = File.ReadAllText(path);
            var serverData = JsonSerializer.Deserialize<ServerDataInfo>(json, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
            
            if (serverData == null || string.IsNullOrWhiteSpace(serverData.Id))
            {
                _logger.LogWarning("    Invalid ServerData.json");
                return (ExtractModIdFromFolderName(fallbackName), ExtractNameFromFolderName(fallbackName), "unknown", null, null);
            }
            
            var dependencies = serverData.Revision?.Dependencies?.Select(d => new AddonDependency
            {
                ModId = d.AssetId,
                Name = d.AssetName,
                Version = d.Version
            }).ToList();
            
            return (
                serverData.Id,
                serverData.Name ?? fallbackName,
                serverData.Revision?.Version ?? "unknown",
                serverData.Revision?.Changelog,
                dependencies
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "    Failed to parse ServerData.json");
            return (ExtractModIdFromFolderName(fallbackName), ExtractNameFromFolderName(fallbackName), "unknown", null, null);
        }
    }
    
    /// <summary>
    /// Парсит meta файл (без расширения)
    /// </summary>
    private (string ModId, string Name, string Version, string? Changelog, List<AddonDependency>? Dependencies) 
        ParseMetaFile(string path, string fallbackName)
    {
        try
        {
            var json = File.ReadAllText(path);
            var metaWrapper = JsonSerializer.Deserialize<MetaFileWrapper>(json, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
            
            var meta = metaWrapper?.Meta;
            if (meta == null || string.IsNullOrWhiteSpace(meta.Id))
            {
                _logger.LogWarning("    Invalid meta file");
                return (ExtractModIdFromFolderName(fallbackName), ExtractNameFromFolderName(fallbackName), "unknown", null, null);
            }
            
            // Берём версию из selectedRev или первую в списке
            string version = "unknown";
            string? changelog = null;
            List<AddonDependency>? dependencies = null;
            
            if (meta.Versions != null && meta.Versions.Count > 0)
            {
                var selectedIdx = Math.Clamp(meta.SelectedRev, 0, meta.Versions.Count - 1);
                var selectedVersion = meta.Versions[selectedIdx];
                version = selectedVersion.Version;
                changelog = selectedVersion.Changelog;
                
                dependencies = selectedVersion.Dependencies?.Select(d => new AddonDependency
                {
                    ModId = d.Id,
                    Name = d.Name,
                    Version = d.Version
                }).ToList();
            }
            
            _logger.LogInformation("    Parsed meta: {Name} [{Id}] v{Version}", meta.Name, meta.Id, version);
            
            return (
                meta.Id,
                meta.Name ?? fallbackName,
                version,
                changelog,
                dependencies
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "    Failed to parse meta file");
            return (ExtractModIdFromFolderName(fallbackName), ExtractNameFromFolderName(fallbackName), "unknown", null, null);
        }
    }
    
    /// <summary>
    /// Извлекает ModId из имени папки (формат: Name_ModId)
    /// </summary>
    private static string ExtractModIdFromFolderName(string folderName)
    {
        var underscoreIndex = folderName.LastIndexOf('_');
        if (underscoreIndex > 0 && underscoreIndex < folderName.Length - 1)
        {
            return folderName[(underscoreIndex + 1)..];
        }
        return folderName;
    }
    
    /// <summary>
    /// Извлекает Name из имени папки (формат: Name_ModId)
    /// </summary>
    private static string ExtractNameFromFolderName(string folderName)
    {
        var underscoreIndex = folderName.LastIndexOf('_');
        if (underscoreIndex > 0)
        {
            return folderName[..underscoreIndex].Replace("-", " - ").Replace("_", " ");
        }
        return folderName.Replace("-", " - ").Replace("_", " ");
    }

    public IEnumerable<ModelInfo> GetModels() => _modelIndex.Values;
    public ModelInfo? GetModel(string modelId) => _modelIndex.GetValueOrDefault(modelId);

    /// <summary>
    /// Check if a file path is inside an ignored folder
    /// </summary>
    private bool IsPathInIgnoredFolder(string relativePath)
    {
        if (_ignoredFolders.Count == 0) return false;

        var parts = relativePath.Split('/', '\\');
        return parts.Any(part => _ignoredFolders.Contains(part));
    }

    /// <summary>
    /// Check if a file should be ignored (by name or path)
    /// </summary>
    private bool IsFileIgnored(string relativePath)
    {
        if (_ignoredFiles.Count == 0) return false;

        var fileName = Path.GetFileName(relativePath);

        // Check exact file name match
        if (_ignoredFiles.Contains(fileName))
            return true;

        // Check if path matches any pattern (supports wildcards like "*.txt")
        foreach (var pattern in _ignoredFiles)
        {
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                // Simple wildcard matching
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
            }
            else if (relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                // Exact path match
                return true;
            }
        }

        return false;
    }

    // Методы для работы с аддонами
    public IEnumerable<AddonInfo> GetAddons() => _addonIndex.Values;
    public AddonInfo? GetAddon(string folderName) => _addonIndex.GetValueOrDefault(folderName);
    
    /// <summary>
    /// Получает файлы аддона
    /// </summary>
    public List<ModelFileInfo> GetAddonFilesQuick(string folderName)
    {
        var result = new List<ModelFileInfo>();
        var addon = GetAddon(folderName);
        if (addon == null) return result;

        foreach (var file in Directory.GetFiles(addon.Path, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(addon.Path, file).Replace('\\', '/');

            if (relativePath == "ServerData.json" || relativePath.EndsWith(".chunks"))
                continue;

            // Skip files in ignored folders
            if (IsPathInIgnoredFolder(relativePath))
                continue;

            // Skip ignored files
            if (IsFileIgnored(relativePath))
                continue;

            var fileInfo = new FileInfo(file);

            result.Add(new ModelFileInfo
            {
                Path = relativePath,
                FullPath = file,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                Hash = null,
                IsPak = relativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            });
        }

        return result;
    }

    /// <summary>
    /// Get model files - fast, no hash computation
    /// </summary>
    public List<ModelFileInfo> GetModelFilesQuick(string modelId)
    {
        var result = new List<ModelFileInfo>();
        var model = GetModel(modelId);
        if (model == null) return result;

        foreach (var file in Directory.GetFiles(model.Path, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(model.Path, file).Replace('\\', '/');

            if (relativePath == "version.txt" || relativePath.EndsWith(".manifest") || relativePath.EndsWith(".chunks"))
                continue;

            // Skip files in ignored folders
            if (IsPathInIgnoredFolder(relativePath))
                continue;

            // Skip ignored files
            if (IsFileIgnored(relativePath))
                continue;

            var fileInfo = new FileInfo(file);

            result.Add(new ModelFileInfo
            {
                Path = relativePath,
                FullPath = file,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                Hash = null, // Don't compute - too slow for large files
                IsPak = relativePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            });
        }

        return result;
    }

    /// <summary>
    /// Get chunk count only (not the full chunks - for status API)
    /// Validates that chunks are up-to-date with the file
    /// </summary>
    public int GetChunkCount(string modelId, string filePath)
    {
        var model = GetModel(modelId);
        if (model == null) return 0;

        var fullPath = Path.Combine(model.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
        return GetChunkCountInternal(fullPath, model);
    }
    
    /// <summary>
    /// Get chunk count for addon file
    /// </summary>
    public int GetAddonChunkCount(string folderId, string filePath)
    {
        var addon = GetAddon(folderId);
        if (addon == null) return 0;

        var fullPath = Path.Combine(addon.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
        return GetChunkCountInternal(fullPath, null);
    }
    
    private int GetChunkCountInternal(string fullPath, ModelInfo? model)
    {
        if (!File.Exists(fullPath)) return 0;
        
        if (_chunkCache.TryGetValue(fullPath, out var chunks))
        {
            // Validate that chunks are still valid (file hasn't changed)
            var fileInfo = new FileInfo(fullPath);
            var totalChunkSize = chunks.Sum(c => (long)c.Size);
            
            if (totalChunkSize != fileInfo.Length)
            {
                // File changed, chunks are invalid!
                _logger.LogWarning("Chunks outdated for {File}: chunk size {ChunkSize} != file size {FileSize}",
                    fullPath, totalChunkSize, fileInfo.Length);
                _chunkCache.TryRemove(fullPath, out _);
                
                // Delete outdated .chunks file
                var chunkFile = fullPath + ".chunks";
                if (File.Exists(chunkFile))
                {
                    try { File.Delete(chunkFile); } catch { }
                }
                
                // Trigger background regeneration
                if (model != null)
                {
                    _ = Task.Run(async () => await RegenerateChunksForFileAsync(model, fullPath));
                }
                
                return 0;
            }
            
            return chunks.Count;
        }
        
        return 0;
    }

    /// <summary>
    /// Get full chunk list (for delta updates)
    /// Validates that chunks are up-to-date
    /// </summary>
    public List<FileChunkInfo> GetFileChunksSync(string modelId, string filePath)
    {
        var model = GetModel(modelId);
        if (model == null) return [];

        var fullPath = Path.Combine(model.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return [];
        
        if (_chunkCache.TryGetValue(fullPath, out var chunks))
        {
            // Validate chunks
            var fileInfo = new FileInfo(fullPath);
            var totalChunkSize = chunks.Sum(c => (long)c.Size);
            
            if (totalChunkSize != fileInfo.Length)
            {
                _logger.LogWarning("Chunks outdated for {File}", filePath);
                _chunkCache.TryRemove(fullPath, out _);
                return [];
            }
            
            return chunks;
        }
        
        return [];
    }

    public async Task<List<FileChunkInfo>> GetFileChunksAsync(string modelId, string filePath, CancellationToken ct = default)
    {
        var model = GetModel(modelId);
        if (model == null) return [];

        var fullPath = Path.Combine(model.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return [];

        var fileInfo = new FileInfo(fullPath);

        if (_chunkCache.TryGetValue(fullPath, out var cached))
        {
            // Validate
            if (cached.Sum(c => (long)c.Size) == fileInfo.Length)
                return cached;
            
            // Invalid, remove
            _chunkCache.TryRemove(fullPath, out _);
        }
        
        // Try disk
        var chunkFile = fullPath + ".chunks";
        if (File.Exists(chunkFile))
        {
            var chunkFileInfo = new FileInfo(chunkFile);
            
            // Only valid if .chunks is newer than .pak
            if (chunkFileInfo.LastWriteTimeUtc > fileInfo.LastWriteTimeUtc)
            {
                try
                {
                    var data = await File.ReadAllBytesAsync(chunkFile, ct);
                    var chunks = MessagePackSerializer.Deserialize<List<FileChunkInfo>>(data);
                    
                    if (chunks.Sum(c => (long)c.Size) == fileInfo.Length)
                    {
                        _chunkCache[fullPath] = chunks;
                        return chunks;
                    }
                }
                catch { }
            }
            
            // Delete invalid chunk file
            try { File.Delete(chunkFile); } catch { }
        }
        
        return [];
    }

    /// <summary>
    /// Regenerate chunks for a single file
    /// </summary>
    private async Task RegenerateChunksForFileAsync(ModelInfo model, string fullPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(model.Path, fullPath).Replace('\\', '/');
            var fileInfo = new FileInfo(fullPath);

            _logger.LogInformation("Regenerating chunks for {File} ({Size})...", relativePath, FormatBytes(fileInfo.Length));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var chunks = await GenerateChunksAsync(fullPath, CancellationToken.None);
            sw.Stop();

            _chunkCache[fullPath] = chunks;

            // Save to disk only if ChunksPath configured
            var chunkFile = GetChunkFilePath(fullPath);
            if (chunkFile != null)
            {
                await SaveChunkCacheAsync(fullPath, chunks, fileInfo, chunkFile);
            }

            _logger.LogInformation("Regenerated {File}: {Count} chunks in {Time:F1}s", 
                relativePath, chunks.Count, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to regenerate chunks for {File}", fullPath);
        }
    }

    /// <summary>
    /// Get a specific chunk of a file
    /// </summary>
    public byte[]? GetFileChunk(string modelId, string filePath, long offset, int size)
    {
        var model = GetModel(modelId);
        if (model == null) return null;

        var fullPath = Path.Combine(model.Path, filePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return null;

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (offset >= stream.Length) return null;
            
            stream.Seek(offset, SeekOrigin.Begin);
            var actualSize = (int)Math.Min(size, stream.Length - offset);
            var buffer = new byte[actualSize];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading chunk from {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Stream a file to output
    /// </summary>
    public async Task StreamFileAsync(string fullPath, Stream outputStream, CancellationToken ct)
    {
        await using var fileStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        await fileStream.CopyToAsync(outputStream, ct);
    }

    /// <summary>
    /// Refresh a model - clear caches and regenerate chunks
    /// </summary>
    public async Task RefreshModelAsync(string modelId, CancellationToken ct = default)
    {
        var model = GetModel(modelId);
        if (model == null) return;
        
        _logger.LogInformation("Refreshing model: {Model}", modelId);
        
        // Clear caches
        foreach (var key in _chunkCache.Keys.Where(k => k.StartsWith(model.Path)).ToList())
            _chunkCache.TryRemove(key, out _);
        
        // Delete .chunks files
        foreach (var chunkFile in Directory.GetFiles(model.Path, "*.chunks", SearchOption.AllDirectories))
        {
            try { File.Delete(chunkFile); _logger.LogInformation("Deleted: {File}", chunkFile); } 
            catch { }
        }
        
        _hashCache.Clear();
        ScanModelsFolder();
        
        model = GetModel(modelId);
        if (model != null)
            await PrecomputeModelChunksAsync(model);
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < suffixes.Length - 1) { size /= 1024; i++; }
        return $"{size:F2} {suffixes[i]}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _backgroundTask?.Wait(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}

public class ModelInfo
{
    public required string ModelId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public long TotalSize { get; init; }
    public int FileCount { get; init; }
    public required string Path { get; init; }
}

public class ModelFileInfo
{
    public required string Path { get; init; }
    public required string FullPath { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public string? Hash { get; init; }
    public bool IsPak { get; init; }
}
