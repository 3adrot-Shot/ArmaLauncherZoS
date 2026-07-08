using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ArmaLauncherClient.Models;

namespace ArmaLauncherClient.Services;

public enum DownloadMode
{
    /// <summary>Phased: Download all first, then assemble. Max speed, more RAM.</summary>
    Phased,
    /// <summary>Streaming: Download and write in parallel pipeline. Less RAM.</summary>
    Streaming
}

public sealed class UpdateManager : IAsyncDisposable
{
    private const uint WeeGamesPathHashSeed = 0x000AB0BA;
    private const string WeeGamesEmuDirectoryName = "WeeGamesEmu";
    private const string WeeGamesHashFileName = ".hashe";

    private readonly HttpClient _httpClient;
    private readonly DeduplicationCache _cache;
    private readonly CryptoService _crypto;
    private string _gameDirectory;
    private string _modsDirectory;
    private readonly string _defaultGameDirectory;
    private readonly string _defaultModsDirectory;
    private readonly string _baseUrl;
    private CancellationTokenSource? _downloadCts;
    
    private readonly Queue<(DateTime Time, long Bytes)> _speedSamples = new();
    private readonly object _speedLock = new();
    private double _currentSpeed;
    private readonly int _maxConcurrentDownloads;
    
    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private readonly TimeSpan _progressThrottle = TimeSpan.FromMilliseconds(50);
    
    private const string SettingsFileName = "launcher_settings.json";
    private static readonly string[] KnownGameModelIds = ["fullgame", "arma-reforger"];

    public event Action<DownloadProgress>? ProgressChanged;
    public string ServerUrl => _baseUrl;
    public DownloadMode Mode { get; set; } = DownloadMode.Phased;
    /// <summary>
    /// Мгновенная установка: начинать скачивание+сборку каждого файла сразу после его анализа,
    /// не дожидаясь окончания анализа всех файлов.
    /// </summary>
    public bool InstantInstall { get; set; } = false;
    public string GameInstallRoot => _gameDirectory;
    public string ModsInstallRoot => _modsDirectory;
    public string InstallRoot => _gameDirectory; // Для совместимости

    public UpdateManager(HttpClient httpClient, DeduplicationCache cache, CryptoService crypto, string baseUrl, string? modelsDirectory = null)
    {
        _httpClient = httpClient;
        _cache = cache;
        _crypto = crypto;
        _baseUrl = baseUrl.TrimEnd('/');
        
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmaLauncher");
        _defaultGameDirectory = Path.Combine(appData, "Game");
        _defaultModsDirectory = Path.Combine(appData, "Addons");
        
        LoadSettings();
        
        Directory.CreateDirectory(_gameDirectory);
        Directory.CreateDirectory(_modsDirectory);
        
        _maxConcurrentDownloads = Math.Clamp(Environment.ProcessorCount * 4, 8, 64);
        
        FileLogger.Log($"");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        FileLogger.Log($"?  UpdateManager Initialized");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        FileLogger.Log($"?  Server:      {_baseUrl}");
        FileLogger.Log($"?  Game Dir:    {_gameDirectory}");
        FileLogger.Log($"?  Mods Dir:    {_modsDirectory}");
        FileLogger.Log($"?  Concurrency: {_maxConcurrentDownloads} connections");
        FileLogger.Log($"?  CPU Cores:   {Environment.ProcessorCount}");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        FileLogger.Log($"");
    }

    #region Path Management
    
    public void SetGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var oldPath = _gameDirectory;
        _gameDirectory = path;
        Directory.CreateDirectory(_gameDirectory);
        SaveSettings();
        FileLogger.Log($"[CONFIG] Game path changed: {oldPath} -> {path}");
    }

    public void SetModsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var oldPath = _modsDirectory;
        _modsDirectory = path;
        Directory.CreateDirectory(_modsDirectory);
        SaveSettings();
        FileLogger.Log($"[CONFIG] Mods path changed: {oldPath} -> {path}");
    }

    public void ResetGamePath()
    {
        var oldPath = _gameDirectory;
        _gameDirectory = _defaultGameDirectory;
        Directory.CreateDirectory(_gameDirectory);
        SaveSettings();
        FileLogger.Log($"[CONFIG] Game path reset: {oldPath} -> {_gameDirectory}");
    }

    public void ResetModsPath()
    {
        var oldPath = _modsDirectory;
        _modsDirectory = _defaultModsDirectory;
        Directory.CreateDirectory(_modsDirectory);
        SaveSettings();
        FileLogger.Log($"[CONFIG] Mods path reset: {oldPath} -> {_modsDirectory}");
    }

    // Для совместимости
    public void SetInstallRoot(string path) => SetGamePath(path);
    public void ResetInstallRoot() => ResetGamePath();

    /// <summary>
    /// Читает версию игры из метаданных exe файла (FileVersionInfo.ProductVersion)
    /// </summary>
    public static string? GetGameVersionFromExe(string gamePath)
    {
        var exePath = Path.Combine(gamePath, "ArmaReforgerSteam.exe");
        if (!File.Exists(exePath))
        {
            FileLogger.Log($"[VERSION] Exe not found: {exePath}");
            return null;
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
            var productVersion = versionInfo.ProductVersion;

            if (!string.IsNullOrWhiteSpace(productVersion))
            {
                FileLogger.Log($"[VERSION] Read from exe: {productVersion}");
                return productVersion.Trim();
            }

            // Fallback to FileVersion if ProductVersion is empty
            if (versionInfo.FileMajorPart > 0)
            {
                var fileVersion = $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}.{versionInfo.FilePrivatePart}";
                FileLogger.Log($"[VERSION] Read FileVersion from exe: {fileVersion}");
                return fileVersion;
            }

            FileLogger.Log($"[VERSION] No version info in exe");
            return null;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[VERSION] Failed to read exe version: {ex.Message}");
            return null;
        }
    }

    private void LoadSettings()
    {
        _gameDirectory = _defaultGameDirectory;
        _modsDirectory = _defaultModsDirectory;
        
        try
        {
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmaLauncher", SettingsFileName);
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<LauncherSettings>(json);
                if (settings != null)
                {
                    if (!string.IsNullOrWhiteSpace(settings.GamePath) && Directory.Exists(Path.GetDirectoryName(settings.GamePath)))
                        _gameDirectory = settings.GamePath;
                    if (!string.IsNullOrWhiteSpace(settings.ModsPath) && Directory.Exists(Path.GetDirectoryName(settings.ModsPath)))
                        _modsDirectory = settings.ModsPath;
                }
                FileLogger.Log($"[CONFIG] Loaded settings from {settingsPath}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[CONFIG] Failed to load settings: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmaLauncher");
            Directory.CreateDirectory(settingsDir);
            var settingsPath = Path.Combine(settingsDir, SettingsFileName);
            
            var settings = new LauncherSettings
            {
                GamePath = _gameDirectory,
                ModsPath = _modsDirectory
            };
            
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
            FileLogger.Log($"[CONFIG] Settings saved to {settingsPath}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[CONFIG] Failed to save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет существующую установку игры по указанному пути
    /// </summary>
    public async Task<ExistingGameInfo?> DetectExistingGameAsync(string gamePath, CancellationToken ct = default)
    {
        FileLogger.Log($"[DETECT] Checking for existing game at: {gamePath}");

        if (!Directory.Exists(gamePath))
        {
            FileLogger.Log($"[DETECT] Directory does not exist");
            return null;
        }

        // Сначала пробуем читать версию из exe файла (приоритет)
        string? installedVersion = GetGameVersionFromExe(gamePath);

        // Fallback на .version файл если exe не найден или версия не читается
        if (installedVersion == null)
        {
            var versionFile = Path.Combine(gamePath, ".version");
            if (File.Exists(versionFile))
            {
                installedVersion = File.ReadAllText(versionFile).Trim();
                FileLogger.Log($"[DETECT] Fallback to .version file: {installedVersion}");
            }
        }

        // Проверяем наличие КОНКРЕТНЫХ файлов игры Arma Reforger
        var gameExe = Path.Combine(gamePath, "ArmaReforgerSteam.exe");
        var hasGameExe = File.Exists(gameExe);

        // Или проверяем структуру папок игры (addons/data с pak файлами)
        var addonsDataPath = Path.Combine(gamePath, "addons", "data");
        var hasGameStructure = Directory.Exists(addonsDataPath) && 
                               Directory.GetFiles(addonsDataPath, "*.pak").Length > 0;

        var hasGameFiles = hasGameExe || hasGameStructure;

        FileLogger.Log($"[DETECT] HasExe={hasGameExe}, HasStructure={hasGameStructure}, InstalledVersion={installedVersion ?? "null"}");

        if (!hasGameFiles)
        {
            FileLogger.Log($"[DETECT] No game files found (looking for ArmaReforgerSteam.exe or addons/data/*.pak)");
            return null;
        }

        // Получаем инфо о последней версии с сервера (тоже должна читаться из exe на сервере)
        var serverInfo = await GetGameInfoAsync(ct);
        var latestVersion = serverInfo?.Version;


        var needsUpdate = installedVersion == null;
        if (!needsUpdate && latestVersion != null && installedVersion != null)
        {
            needsUpdate = CompareVersions(latestVersion, installedVersion) > 0;
        }

        var result = new ExistingGameInfo
        {
            Path = gamePath,
            InstalledVersion = installedVersion,
            LatestVersion = latestVersion,
            NeedsUpdate = needsUpdate,
            HasGameFiles = hasGameFiles,
            FileCount = hasGameFiles ? Directory.GetFiles(gamePath, "*", SearchOption.AllDirectories).Length : 0
        };

        if (installedVersion == null && hasGameFiles)
        {
            FileLogger.Log($"[DETECT] Game files found but version unknown - needs update/verification");
        }

        FileLogger.Log($"[DETECT] Result: Version={installedVersion ?? "unknown"}, Latest={latestVersion ?? "unknown"}, NeedsUpdate={needsUpdate}, HasFiles={hasGameFiles}");
        return result;
    }

    /// <summary>
    /// Определяет, является ли модель игрой или модом
    /// </summary>
    public bool IsGameModel(string modelId) =>
        KnownGameModelIds.Any(gameId => modelId.Equals(gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Возвращает путь установки для конкретной модели
    /// </summary>
    public string GetInstallPath(string modelId)
    {
        if (IsGameModel(modelId))
            return _gameDirectory;
        else
            return Path.Combine(_modsDirectory, modelId);
    }

    #endregion

    public async Task<ServerInfoResponse?> GetServerInfoAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/info";
        FileLogger.Log($"[API] GET {url}");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _httpClient.GetFromJsonAsync<ServerInfoResponse>(url, ct);
            FileLogger.Log($"[API] GET {url} -> OK ({sw.ElapsedMilliseconds}ms) Version={result?.Version}, HasGame={result?.Game != null}");
            return result;
        }
        catch (HttpRequestException ex)
        {
            FileLogger.Log($"[API] GET {url} -> FAILED ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            return null;
        }
    }

    public async Task<ServerGameInfo?> GetGameInfoAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/game";
        FileLogger.Log($"[API] GET {url}");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _httpClient.GetFromJsonAsync<ServerGameInfo>(url, ct);
            FileLogger.Log($"[API] GET {url} -> OK ({sw.ElapsedMilliseconds}ms) Game={result?.Name} v{result?.Version}, Size={result?.TotalSizeFormatted}");
            return result;
        }
        catch (HttpRequestException ex)
        {
            FileLogger.Log($"[API] GET {url} -> FAILED ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            return null;
        }
    }

    public async Task<List<ServerModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/models";
        FileLogger.Log($"[API] GET {url}");
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.GetFromJsonAsync<ModelsResponse>(url, ct);
        var models = response?.Models ?? [];
        FileLogger.Log($"[API] GET {url} -> OK ({sw.ElapsedMilliseconds}ms) Found {models.Count} models");
        return models;
    }

    /// <summary>
    /// Получает список аддонов (модов) с сервера
    /// </summary>
    public async Task<List<ServerAddonInfo>> GetAddonsAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/addons";
        FileLogger.Log($"[API] GET {url}");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.GetFromJsonAsync<AddonsResponse>(url, ct);
            var addons = response?.Addons ?? [];
            FileLogger.Log($"[API] GET {url} -> OK ({sw.ElapsedMilliseconds}ms) Found {addons.Count} addons");
            return addons;
        }
        catch (HttpRequestException ex)
        {
            FileLogger.Log($"[API] GET {url} -> FAILED ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Получает детали аддона с сервера
    /// </summary>
    public async Task<ServerAddonDetails?> GetAddonDetailsAsync(string folderId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/addons/{Uri.EscapeDataString(folderId)}";
        FileLogger.Log($"[API] GET {url}");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _httpClient.GetFromJsonAsync<ServerAddonDetails>(url, ct);
            FileLogger.Log($"[API] GET {url} -> OK ({sw.ElapsedMilliseconds}ms) Files={result?.Files?.Count ?? 0}, Size={FormatBytes(result?.TotalSize ?? 0)}");
            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            FileLogger.Log($"[API] GET {url} -> NOT FOUND ({sw.ElapsedMilliseconds}ms)");
            return null;
        }
    }

    /// <summary>
    /// Проверяет установку аддона - читает версию из meta или ServerData.json файла
    /// </summary>
    public UpdateCheckResult CheckAddonInstallation(string folderId, string latestVersion)
    {
        var addonDir = Path.Combine(_modsDirectory, folderId);

        // Проверяем существует ли папка
        if (!Directory.Exists(addonDir))
        {
            return new UpdateCheckResult
            {
                ModelId = folderId,
                IsInstalled = false,
                InstalledVersion = null,
                LatestVersion = latestVersion,
                UpdateAvailable = true
            };
        }

        // Читаем версию из мета-файлов мода (как на сервере)
        var installedVersion = ReadAddonVersion(addonDir);

        if (installedVersion == null)
        {
            // Папка есть но файлов мода нет
            return new UpdateCheckResult
            {
                ModelId = folderId,
                IsInstalled = false,
                InstalledVersion = null,
                LatestVersion = latestVersion,
                UpdateAvailable = true
            };
        }

        // Сравниваем версии - обновление только если серверная новее
        var updateAvailable = false;
        if (installedVersion != latestVersion)
        {
            var comparison = CompareVersions(latestVersion, installedVersion);
            updateAvailable = comparison > 0;

            if (comparison != 0)
            {
                FileLogger.Log($"[VERSION] {folderId}: local={installedVersion} vs server={latestVersion}, comparison={comparison}");
            }
        }

        return new UpdateCheckResult
        {
            ModelId = folderId,
            IsInstalled = true,
            InstalledVersion = installedVersion,
            LatestVersion = latestVersion,
            UpdateAvailable = updateAvailable
        };
    }

    /// <summary>
    /// Читает версию аддона из meta файла или ServerData.json
    /// </summary>
    private string? ReadAddonVersion(string addonDir)
    {
        // 1. Пробуем прочитать из файла "meta" (без расширения)
        var metaPath = Path.Combine(addonDir, "meta");
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                var metaWrapper = JsonSerializer.Deserialize<MetaFileWrapper>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                var meta = metaWrapper?.Meta;
                if (meta?.Versions != null && meta.Versions.Count > 0)
                {
                    var selectedIdx = Math.Clamp(meta.SelectedRev, 0, meta.Versions.Count - 1);
                    var version = meta.Versions[selectedIdx].Version;
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        return version;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[ADDON-CHECK] Failed to parse meta file: {ex.Message}");
            }
        }

        // 2. Пробуем прочитать из ServerData.json
        var serverDataPath = Path.Combine(addonDir, "ServerData.json");
        if (File.Exists(serverDataPath))
        {
            try
            {
                var json = File.ReadAllText(serverDataPath);
                var serverData = JsonSerializer.Deserialize<ServerDataInfo>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (!string.IsNullOrWhiteSpace(serverData?.Revision?.Version))
                {
                    return serverData.Revision.Version;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[ADDON-CHECK] Failed to parse ServerData.json: {ex.Message}");
            }
        }

        // 3. Проверяем есть ли вообще файлы мода
        var hasFiles = Directory.GetFiles(addonDir, "*", SearchOption.AllDirectories)
            .Any(f => !f.EndsWith(".chunks"));

        if (hasFiles)
        {
            // Файлы есть но версия неизвестна
            return "unknown";
        }

        return null;
    }

    // Модели для парсинга meta файла
    private class MetaFileWrapper
    {
        [JsonPropertyName("meta")]
        public MetaInfo? Meta { get; set; }
    }

    private class MetaInfo
    {
        [JsonPropertyName("versions")]
        public List<MetaVersionInfo>? Versions { get; set; }

        [JsonPropertyName("selectedRev")]
        public int SelectedRev { get; set; }
    }

    private class MetaVersionInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "";
    }

    // Модели для парсинга ServerData.json
    private class ServerDataInfo
    {
        [JsonPropertyName("revision")]
        public ServerDataRevision? Revision { get; set; }
    }

    private class ServerDataRevision
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    /// <summary>
    /// Устанавливает или обновляет аддон (с поддержкой multi-connection download как для игры)
    /// </summary>
    public async Task<InstallResult> InstallAddonAsync(string folderId, string version, CancellationToken ct = default)
    {
        FileLogger.Log($"");
        FileLogger.Log($"====================================================================");
        FileLogger.Log($"=  ADDON INSTALL START: {folderId} v{version}");
        FileLogger.Log($"=  Mode: {Mode}");
        FileLogger.Log($"====================================================================");

        var globalSw = Stopwatch.StartNew();
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _downloadCts.Token;

        FileLogger.Log($"[ADDON] Fetching addon details...");
        var details = await GetAddonDetailsAsync(folderId, linkedCt);
        if (details == null)
        {
            FileLogger.Log($"[ADDON] FAILED: Addon not found on server");
            return new InstallResult { Success = false, ModelId = folderId, Error = "Addon not found" };
        }

        var addonDir = Path.Combine(_modsDirectory, folderId);
        var tempDir = addonDir + ".downloading";
        var cacheDir = Path.Combine(tempDir, ".cache");
        var existingDir = Directory.Exists(addonDir) ? addonDir : null;

        FileLogger.Log($"[ADDON] Paths:");
        FileLogger.Log($"  Target:   {addonDir}");
        FileLogger.Log($"  Temp:     {tempDir}");
        FileLogger.Log($"  Existing: {existingDir ?? "none"}");

        try
        {
            if (Directory.Exists(tempDir))
            {
                FileLogger.Log($"[ADDON] Cleaning old temp directory...");
                Directory.Delete(tempDir, true);
            }
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(cacheDir);

            var files = details.Files ?? [];
            if (files.Count == 0)
            {
                FileLogger.Log($"[ADDON] FAILED: No files in addon");
                return new InstallResult { Success = false, ModelId = folderId, Error = "No files" };
            }

            FileLogger.Log($"[ADDON] Addon has {files.Count} files, total size: {FormatBytes(details.TotalSize)}");

            _speedSamples.Clear();
            _currentSpeed = 0;

            // Определяем, изменилась ли версия мода - если да, принудительно обновляем метаданные
            // (meta/ServerData.json могут меняться без изменения размера файла)
            var localVersion = existingDir != null ? ReadAddonVersion(existingDir) : null;
            var versionChanged = existingDir != null && !string.Equals(localVersion, version, StringComparison.OrdinalIgnoreCase);
            if (versionChanged)
            {
                FileLogger.Log($"[ADDON] Version change detected: local={localVersion ?? "none"} -> server={version}");
                FileLogger.Log($"[ADDON] Non-pak files (meta, ServerData.json, ...) will be re-downloaded");
            }

            // Build download plan (same as for game models)
            FileLogger.Log($"");
            FileLogger.Log($">>> PHASE 1: ANALYZING FILES <<<");
            var analysisSw = Stopwatch.StartNew();
            var downloadPlan = BuildAddonDownloadPlan(folderId, files, existingDir, versionChanged);
            analysisSw.Stop();

            long totalToDownload = downloadPlan.Sum(p => p.BytesToDownload);
            var copyCount = downloadPlan.Count(p => p.Action == FileAction.Copy);
            var fullDownloadCount = downloadPlan.Count(p => p.Action == FileAction.FullDownload);

            FileLogger.Log($"[ANALYSIS] Completed in {analysisSw.ElapsedMilliseconds}ms");
            FileLogger.Log($"[ANALYSIS] Plan summary:");
            FileLogger.Log($"  Copy (unchanged):  {copyCount} files");
            FileLogger.Log($"  Full download:     {fullDownloadCount} files");
            FileLogger.Log($"  Total to download: {FormatBytes(totalToDownload)}");

            // FAST PATH: All files already up-to-date
            if (fullDownloadCount == 0 && copyCount == files.Count && existingDir != null)
            {
                FileLogger.Log($"");
                FileLogger.Log($">>> ALL FILES UP TO DATE - FAST PATH <<<");

                // Удаляем лишние файлы, которых больше нет на сервере
                RemoveObsoleteAddonFiles(addonDir, files);

                globalSw.Stop();

                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

                FileLogger.Log($"");
                FileLogger.Log($"====================================================================");
                FileLogger.Log($"=  ADDON VERIFICATION COMPLETED - ALL FILES OK");
                FileLogger.Log($"=  Addon: {folderId} v{version}");
                FileLogger.Log($"=  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
                FileLogger.Log($"====================================================================");

                ReportProgress(folderId, version, DownloadState.Completed, 0, 0, 0, null, files.Count, files.Count);
                return new InstallResult { Success = true, ModelId = folderId, Version = version, InstallPath = addonDir };
            }

            // IN-PLACE UPDATE: Existing installation with some files to update
            if (existingDir != null && copyCount > 0 && fullDownloadCount > 0)
            {
                FileLogger.Log($"");
                FileLogger.Log($"> IN-PLACE UPDATE - NO COPY NEEDED <");
                FileLogger.Log($"[INPLACE] {fullDownloadCount} files to download, {copyCount} files unchanged (stay in place)");

                var downloadedData = await DownloadAddonDataPhasedAsync(folderId, version, downloadPlan, cacheDir, totalToDownload, linkedCt);

                FileLogger.Log($"");
                FileLogger.Log($"> IN-PLACE ASSEMBLING <");
                await AssembleAddonFilesInPlaceAsync(downloadPlan, downloadedData, addonDir, linkedCt);

                // Удаляем лишние файлы, которых больше нет на сервере
                RemoveObsoleteAddonFiles(addonDir, files);

                try { Directory.Delete(cacheDir, true); } catch { }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

                // Читаем актуальную версию из файлов мода
                var inplaceVersion = ReadAddonVersion(addonDir) ?? version;
                globalSw.Stop();

                FileLogger.Log($"");
                FileLogger.Log($"====================================================================");
                FileLogger.Log($"=  ADDON IN-PLACE UPDATE COMPLETED SUCCESSFULLY");
                FileLogger.Log($"=  Addon: {folderId} v{inplaceVersion}");
                FileLogger.Log($"=  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
                FileLogger.Log($"=  Downloaded: {FormatBytes(totalToDownload)}");
                FileLogger.Log($"=  Files updated: {fullDownloadCount}, unchanged: {copyCount}");
                FileLogger.Log($"====================================================================");

                ReportProgress(folderId, inplaceVersion, DownloadState.Completed, totalToDownload, totalToDownload, 0, null, files.Count, files.Count);
                return new InstallResult { Success = true, ModelId = folderId, Version = inplaceVersion, InstallPath = addonDir };
            }

            // FULL INSTALL: New installation with atomic swap
            FileLogger.Log($"");
            FileLogger.Log($">>> PHASE 2: DOWNLOADING (FULL INSTALL) <<<");
            var swFull = Stopwatch.StartNew();

            var downloadedFiles = await DownloadAddonDataPhasedAsync(folderId, version, downloadPlan, cacheDir, totalToDownload, linkedCt);

            FileLogger.Log($"");
            FileLogger.Log($">>> PHASE 3: ASSEMBLING <<<");
            await AssembleAddonFilesAsync(downloadPlan, downloadedFiles, tempDir, existingDir, linkedCt);

            swFull.Stop();
            FileLogger.Log($"");
            FileLogger.Log($">>> PHASE 4: FINALIZING <<<");
            FileLogger.Log($"[FINALIZE] Download completed in {swFull.Elapsed.TotalSeconds:F1}s");
            FileLogger.Log($"[FINALIZE] Average speed: {FormatBytes((long)(totalToDownload / Math.Max(swFull.Elapsed.TotalSeconds, 0.1)))}/s");

            try { Directory.Delete(cacheDir, true); } catch { }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, linkedCt);

            FileLogger.Log($"[FINALIZE] Performing atomic directory swap...");
            await PerformAtomicSwapAsync(addonDir, tempDir, linkedCt);

            // Читаем актуальную версию из файлов мода (не используем .version)
            var actualVersion = ReadAddonVersion(addonDir) ?? version;
            globalSw.Stop();

            FileLogger.Log($"");
            FileLogger.Log($"====================================================================");
            FileLogger.Log($"=  ADDON INSTALL COMPLETED SUCCESSFULLY");
            FileLogger.Log($"=  Addon: {folderId} v{actualVersion}");
            FileLogger.Log($"=  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
            FileLogger.Log($"=  Downloaded: {FormatBytes(totalToDownload)}");
            FileLogger.Log($"====================================================================");
            FileLogger.Log($"");

            ReportProgress(folderId, actualVersion, DownloadState.Completed, totalToDownload, totalToDownload, 0, null, files.Count, files.Count);
            return new InstallResult { Success = true, ModelId = folderId, Version = actualVersion, InstallPath = addonDir };
        }
        catch (OperationCanceledException)
        {
            FileLogger.Log($"[ADDON] CANCELLED by user");
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Addon install failed", ex);
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { }
            return new InstallResult { Success = false, ModelId = folderId, Error = ex.Message };
        }
        finally
        {
            _downloadCts = null;
        }
    }

    /// <summary>
    /// Build download plan for addon files (simplified - no delta, just size comparison)
    /// </summary>
    private List<FilePlan> BuildAddonDownloadPlan(string folderId, List<ServerFileInfo> files, string? existingDir, bool forceMetadataRefresh = false)
    {
        FileLogger.Log($"[ANALYSIS] Starting file analysis for {files.Count} addon files...");
        var plans = new List<FilePlan>();

        int fileNum = 0;
        foreach (var file in files)
        {
            fileNum++;
            var existingPath = existingDir != null ? Path.Combine(existingDir, file.Path.Replace('/', Path.DirectorySeparatorChar)) : null;
            var existingFile = existingPath != null && File.Exists(existingPath) ? new FileInfo(existingPath) : null;

            var plan = new FilePlan { File = file, ExistingPath = existingPath, ExistingFile = existingFile };

            if (existingFile == null)
            {
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> FULL DOWNLOAD (new file, {FormatBytes(file.Size)})");
            }
            else if (forceMetadataRefresh && !file.IsPak && existingFile.Length == file.Size)
            {
                // Файлы типа meta/ServerData.json могут меняться без изменения размера.
                // При смене версии мода принудительно перекачиваем все не-pak файлы.
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> FULL DOWNLOAD (metadata refresh, {FormatBytes(file.Size)})");
            }
            else if (existingFile.Length == file.Size)
            {
                plan.Action = FileAction.Copy;
                plan.BytesToDownload = 0;
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> COPY (size match: {FormatBytes(file.Size)})");
            }
            else
            {
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                var reason = $"size mismatch: local={FormatBytes(existingFile.Length)} server={FormatBytes(file.Size)}";
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> FULL DOWNLOAD ({reason})");
            }

            plans.Add(plan);
        }

        return plans;
    }

    /// <summary>
    /// Download addon files with multi-connection support (same as game models)
    /// </summary>
    private async Task<DownloadedData> DownloadAddonDataPhasedAsync(string folderId, string version, List<FilePlan> plans, string cacheDir, long totalToDownload, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long downloadedBytes = 0;
        var downloadedFiles = new ConcurrentDictionary<string, string>();
        var semaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        var tasks = new List<Task>();

        var fullDownloads = plans.Where(p => p.Action == FileAction.FullDownload).ToList();

        FileLogger.Log($"[DOWNLOAD] Starting phased download:");
        FileLogger.Log($"  Full files: {fullDownloads.Count} ({FormatBytes(fullDownloads.Sum(p => p.BytesToDownload))})");
        FileLogger.Log($"  Concurrency: {_maxConcurrentDownloads} connections");

        int completedFiles = 0;
        var lastSpeedLog = DateTime.UtcNow;

        foreach (var plan in plans)
        {
            // Check cancellation before starting each task
            ct.ThrowIfCancellationRequested();

            if (plan.Action == FileAction.FullDownload)
            {
                var cachePath = Path.Combine(cacheDir, Guid.NewGuid().ToString("N"));
                downloadedFiles[plan.File.Path] = cachePath;
                var filePath = plan.File.Path;
                var fileSize = plan.File.Size;

                tasks.Add(Task.Run(async () =>
                {
                    // Check cancellation at start of each task
                    ct.ThrowIfCancellationRequested();

                    var fileSw = Stopwatch.StartNew();
                    FileLogger.Log($"[DOWNLOAD] START: {filePath} ({FormatBytes(fileSize)})");

                    await DownloadAddonFileMultiConnectionAsync(folderId, plan.File, cachePath, semaphore,
                        bytes => {
                            var total = Interlocked.Add(ref downloadedBytes, bytes);
                            UpdateSpeed(total);

                            if ((DateTime.UtcNow - lastSpeedLog).TotalSeconds >= 2)
                            {
                                lastSpeedLog = DateTime.UtcNow;
                                var percent = totalToDownload > 0 ? (total * 100.0 / totalToDownload) : 0;
                                FileLogger.Log($"[SPEED] {FormatBytes((long)_currentSpeed)}/s | {percent:F1}% | {FormatBytes(total)}/{FormatBytes(totalToDownload)}");
                            }

                            ReportProgress(folderId, version, DownloadState.Downloading, total, totalToDownload, _currentSpeed, filePath, 0, plans.Count);
                        }, ct);

                    fileSw.Stop();
                    var completed = Interlocked.Increment(ref completedFiles);
                    var speed = fileSw.Elapsed.TotalSeconds > 0 ? fileSize / fileSw.Elapsed.TotalSeconds : 0;
                    FileLogger.Log($"[DOWNLOAD] DONE: {filePath} in {fileSw.Elapsed.TotalSeconds:F1}s ({FormatBytes((long)speed)}/s) [{completed}/{fullDownloads.Count}]");
                }, ct));
            }
        }

        await Task.WhenAll(tasks);
        semaphore.Dispose();

        sw.Stop();
        FileLogger.Log($"[DOWNLOAD] All downloads completed in {sw.Elapsed.TotalSeconds:F1}s");
        FileLogger.Log($"  Files: {completedFiles}");
        FileLogger.Log($"  Total: {FormatBytes(downloadedBytes)}, Avg speed: {FormatBytes((long)(downloadedBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.1)))}/s");

        return new DownloadedData { TotalBytes = downloadedBytes, Duration = sw.Elapsed, Files = downloadedFiles };
    }

    /// <summary>
    /// Download addon file with multi-connection Range requests for large files
    /// </summary>
    private async Task DownloadAddonFileMultiConnectionAsync(string folderId, ServerFileInfo file, string localPath, SemaphoreSlim semaphore, Action<long> onProgress, CancellationToken ct)
    {
        const long ChunkSize = 4 * 1024 * 1024; // 4MB chunks
        var url = $"{_baseUrl}/addons/{Uri.EscapeDataString(folderId)}/files/{file.Path}";

        // Check cancellation before starting
        ct.ThrowIfCancellationRequested();

        // Small file - single connection
        if (file.Size < 10 * 1024 * 1024)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
                await using var smallFileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await smallFileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    onProgress(read);
                }
            }
            finally { semaphore.Release(); }
            return;
        }

        // Large file - multi-connection with Range requests
        var ranges = new List<(long Start, long End, int Index)>();
        long offset = 0;
        int index = 0;
        while (offset < file.Size)
        {
            var end = Math.Min(offset + ChunkSize - 1, file.Size - 1);
            ranges.Add((offset, end, index++));
            offset = end + 1;
        }

        var parts = new ConcurrentDictionary<int, byte[]>();
        var rangeTasks = ranges.Select(async r =>
        {
            ct.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.Start, r.End);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var data = await response.Content.ReadAsByteArrayAsync(ct);
                ct.ThrowIfCancellationRequested();
                parts[r.Index] = data;
                onProgress(data.Length);
            }
            finally { semaphore.Release(); }
        }).ToList();

        await Task.WhenAll(rangeTasks);

        ct.ThrowIfCancellationRequested();
        await using var largeFileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous);
        for (int i = 0; i < ranges.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (parts.TryGetValue(i, out var data))
                await largeFileStream.WriteAsync(data, ct);
        }
    }

    /// <summary>
    /// Assemble addon files (new installation)
    /// </summary>
    private async Task AssembleAddonFilesAsync(List<FilePlan> plans, DownloadedData data, string tempDir, string? existingDir, CancellationToken ct)
    {
        FileLogger.Log($"[ASSEMBLE] Starting file assembly for {plans.Count} addon files...");
        var sw = Stopwatch.StartNew();
        int assembled = 0;

        foreach (var plan in plans)
        {
            var localPath = Path.Combine(tempDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            assembled++;

            switch (plan.Action)
            {
                case FileAction.Copy:
                    File.Copy(plan.ExistingPath!, localPath, true);
                    FileLogger.Log($"[ASSEMBLE] [{assembled}/{plans.Count}] COPY: {plan.File.Path}");
                    break;
                case FileAction.FullDownload:
                    if (data.Files.TryGetValue(plan.File.Path, out var cachePath))
                    {
                        File.Move(cachePath, localPath);
                        FileLogger.Log($"[ASSEMBLE] [{assembled}/{plans.Count}] MOVE: {plan.File.Path} ({FormatBytes(plan.File.Size)})");
                    }
                    break;
            }
        }

        sw.Stop();
        FileLogger.Log($"[ASSEMBLE] Completed {assembled} files in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Assemble addon files in-place (update existing installation)
    /// </summary>
    private async Task AssembleAddonFilesInPlaceAsync(List<FilePlan> plans, DownloadedData data, string targetDir, CancellationToken ct)
    {
        var fullDownloadPlans = plans.Where(p => p.Action == FileAction.FullDownload).ToList();
        var copyCount = plans.Count(p => p.Action == FileAction.Copy);

        FileLogger.Log($"[INPLACE] Target: {targetDir}");
        FileLogger.Log($"[INPLACE] Full: {fullDownloadPlans.Count}, Skip (unchanged): {copyCount}");

        var sw = Stopwatch.StartNew();
        int processed = 0;

        foreach (var plan in fullDownloadPlans)
        {
            processed++;
            var targetPath = Path.Combine(targetDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (data.Files.TryGetValue(plan.File.Path, out var cachePath))
            {
                // Delete old file if exists
                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                File.Move(cachePath, targetPath);
                FileLogger.Log($"[INPLACE] [{processed}/{fullDownloadPlans.Count}] MOVE: {plan.File.Path} ({FormatBytes(plan.File.Size)})");
            }
        }

        sw.Stop();
        FileLogger.Log($"[INPLACE] Completed {processed} files in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Удаляет из папки аддона файлы, которых больше нет на сервере
    /// </summary>
    private void RemoveObsoleteAddonFiles(string addonDir, List<ServerFileInfo> files)
    {
        try
        {
            if (!Directory.Exists(addonDir)) return;

            var expected = new HashSet<string>(
                files.Select(f => Path.GetFullPath(Path.Combine(addonDir, f.Path.Replace('/', Path.DirectorySeparatorChar)))),
                StringComparer.OrdinalIgnoreCase);

            int removed = 0;
            foreach (var localFile in Directory.GetFiles(addonDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(localFile);

                // Служебные файлы лаунчера не трогаем
                if (IsProtectedAddonFile(fileName))
                    continue;

                if (!expected.Contains(Path.GetFullPath(localFile)))
                {
                    try
                    {
                        File.Delete(localFile);
                        removed++;
                        FileLogger.Log($"[CLEANUP] Removed obsolete file: {Path.GetRelativePath(addonDir, localFile)}");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[CLEANUP] Failed to remove {localFile}: {ex.Message}");
                    }
                }
            }

            // Удаляем опустевшие подпапки (от самых глубоких к корню)
            foreach (var dir in Directory.GetDirectories(addonDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }

            if (removed > 0)
                FileLogger.Log($"[CLEANUP] Removed {removed} obsolete file(s)");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[CLEANUP] Cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Служебные файлы, которые нельзя считать лишними и удалять из папки аддона
    /// </summary>
    private static bool IsProtectedAddonFile(string fileName)
    {
        // .version - служебный файл лаунчера
        // ServerData.json - сервер исключает его из списка файлов, но клиент читает из него версию
        return fileName.Equals(".version", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("ServerData.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Проверяет установку аддона
    /// </summary>
    public async Task<VerifyResult> VerifyAddonAsync(string folderId, CancellationToken ct = default)
    {
        FileLogger.Log($"[ADDON-VERIFY] Verifying addon: {folderId}");

        var addonDir = Path.Combine(_modsDirectory, folderId);

        // Проверяем существует ли папка с файлами
        if (!Directory.Exists(addonDir))
        {
            FileLogger.Log($"[ADDON-VERIFY] Not installed (directory not found)");
            return new VerifyResult { Success = false, Error = "Addon not installed" };
        }

        var details = await GetAddonDetailsAsync(folderId, ct);
        if (details == null)
        {
            FileLogger.Log($"[ADDON-VERIFY] Addon not found on server");
            return new VerifyResult { Success = false, Error = "Addon not found on server" };
        }

        var invalid = new List<string>();
        var invalidDetails = new List<InvalidFileDetails>();
        var files = details.Files ?? [];

        foreach (var f in files)
        {
            var localPath = Path.Combine(addonDir, f.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(localPath))
            {
                invalid.Add(f.Path);
                invalidDetails.Add(new InvalidFileDetails { Path = f.Path, IsMissing = true, LocalSize = 0, ExpectedSize = f.Size });
                FileLogger.Log($"[ADDON-VERIFY] MISSING: {f.Path}");
            }
            else
            {
                var localSize = new FileInfo(localPath).Length;
                if (localSize != f.Size)
                {
                    invalid.Add(f.Path);
                    invalidDetails.Add(new InvalidFileDetails { Path = f.Path, IsMissing = false, LocalSize = localSize, ExpectedSize = f.Size });
                    FileLogger.Log($"[ADDON-VERIFY] SIZE MISMATCH: {f.Path} (local={FormatBytes(localSize)}, server={FormatBytes(f.Size)})");
                }
            }
        }

        // Проверяем лишние файлы, которых нет на сервере
        var expectedPaths = new HashSet<string>(
            files.Select(f => Path.GetFullPath(Path.Combine(addonDir, f.Path.Replace('/', Path.DirectorySeparatorChar)))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var localFile in Directory.GetFiles(addonDir, "*", SearchOption.AllDirectories))
        {
            if (IsProtectedAddonFile(Path.GetFileName(localFile)))
                continue;

            if (!expectedPaths.Contains(Path.GetFullPath(localFile)))
            {
                var relPath = Path.GetRelativePath(addonDir, localFile).Replace('\\', '/');
                invalid.Add(relPath);
                invalidDetails.Add(new InvalidFileDetails { Path = relPath, IsMissing = false, IsExtra = true, LocalSize = new FileInfo(localFile).Length, ExpectedSize = 0 });
                FileLogger.Log($"[ADDON-VERIFY] EXTRA FILE: {relPath}");
            }
        }

        FileLogger.Log($"[ADDON-VERIFY] Result: {(invalid.Count == 0 ? "OK" : $"{invalid.Count} invalid files")}");
        return new VerifyResult { Success = invalid.Count == 0, InvalidFiles = invalid, InvalidFileDetails = invalidDetails };
    }

    /// <summary>
    /// Проверяет, является ли ID аддоном (не игрой)
    /// </summary>
    public bool IsAddon(string id)
    {
        // Аддоны имеют формат Name_ModId (с подчёркиванием и hex ID)
        return id.Contains('_') && !IsGameModel(id);
    }

    /// <summary>
    /// Проверяет, есть ли у процесса права на запись в указанную папку
    /// </summary>
    public static bool HasWriteAccess(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testFile = Path.Combine(directory, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            return false;
        }
        catch
        {
            // Не удалось проверить - не блокируем установку, реальная ошибка проявится позже
            return true;
        }
    }

    public async Task<ServerModelDetails?> GetModelDetailsAsync(string modelId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/models/{Uri.EscapeDataString(modelId)}";
        FileLogger.Log($"[API] GET {url}");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _httpClient.GetFromJsonAsync<ServerModelDetails>(url, ct);
            FileLogger.Log($"[API] GET {url} -> OK ({sw.ElapsedMilliseconds}ms) Files={result?.Files?.Count ?? 0}, Size={FormatBytes(result?.TotalSize ?? 0)}");
            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            FileLogger.Log($"[API] GET {url} -> NOT FOUND ({sw.ElapsedMilliseconds}ms)");
            return null;
        }
    }

    private async Task<List<ServerChunkInfo>> GetFileChunksAsync(string modelId, string filePath, CancellationToken ct)
    {
        var url = $"{_baseUrl}/chunks/{Uri.EscapeDataString(modelId)}/{filePath}";
        FileLogger.Log($"[API] GET chunks: {filePath}");
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.GetFromJsonAsync<ChunksResponse>(url, ct);
            var chunks = response?.Chunks ?? [];
            FileLogger.Log($"[API] GET chunks -> OK ({sw.ElapsedMilliseconds}ms) {chunks.Count} chunks for {filePath}");
            return chunks;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[API] GET chunks -> FAILED ({sw.ElapsedMilliseconds}ms): {ex.Message}");
            return [];
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(string modelId, CancellationToken ct = default)
    {
        FileLogger.Log($"[CHECK] Checking update for {modelId}...");
        var installed = GetInstalledVersion(modelId);
        var details = await GetModelDetailsAsync(modelId, ct);

        // Обновление нужно только если серверная версия НОВЕЕ локальной
        var updateAvailable = false;
        if (installed == null)
        {
            // Не установлено - нужна установка
            updateAvailable = true;
        }
        else if (details?.Version != null && installed != details.Version)
        {
            // Версии разные - сравниваем какая новее
            var comparison = CompareVersions(details.Version, installed);
            updateAvailable = comparison > 0; // Серверная новее локальной
            FileLogger.Log($"[CHECK] Version comparison: server={details.Version} vs local={installed}, result={comparison}");
        }

        FileLogger.Log($"[CHECK] {modelId}: Installed={installed ?? "none"}, Latest={details?.Version ?? "unknown"}, UpdateAvailable={updateAvailable}");
        return new UpdateCheckResult
        {
            ModelId = modelId,
            IsInstalled = installed != null,
            InstalledVersion = installed,
            LatestVersion = details?.Version,
            UpdateAvailable = updateAvailable,
            TotalSize = details?.TotalSize ?? 0,
            FileCount = details?.FileCount ?? 0
        };
    }

    public async Task<InstallResult> InstallOrUpdateAsync(string modelId, string version, bool useDeltaPatching = true, CancellationToken ct = default)
    {
        FileLogger.Log($"");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        FileLogger.Log($"?  INSTALL START: {modelId} v{version}");
        FileLogger.Log($"?  Mode: {Mode}, DeltaPatching: {useDeltaPatching}");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        
        var globalSw = Stopwatch.StartNew();
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _downloadCts.Token;

        FileLogger.Log($"[INSTALL] Fetching model details...");
        var details = await GetModelDetailsAsync(modelId, linkedCt);
        if (details == null)
        {
            FileLogger.Log($"[INSTALL] FAILED: Model not found on server");
            return new InstallResult { Success = false, ModelId = modelId, Error = "Model not found" };
        }

        var modelDir = GetModelDirectory(modelId);
        var tempDir = modelDir + ".downloading";
        var cacheDir = Path.Combine(tempDir, ".cache");
        var existingDir = Directory.Exists(modelDir) ? modelDir : null;

        FileLogger.Log($"[INSTALL] Paths:");
        FileLogger.Log($"  Target:   {modelDir}");
        FileLogger.Log($"  Temp:     {tempDir}");
        FileLogger.Log($"  Existing: {existingDir ?? "none"}");

        try
        {
            if (Directory.Exists(tempDir))
            {
                FileLogger.Log($"[INSTALL] Cleaning old temp directory...");
                Directory.Delete(tempDir, true);
            }
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(cacheDir);

            var files = details.Files ?? [];
            if (files.Count == 0)
            {
                FileLogger.Log($"[INSTALL] FAILED: No files in model");
                return new InstallResult { Success = false, ModelId = modelId, Error = "No files" };
            }

            FileLogger.Log($"[INSTALL] Model has {files.Count} files, total size: {FormatBytes(details.TotalSize)}");
            
            _speedSamples.Clear();
            _currentSpeed = 0;

            // ? INSTANT INSTALL: конвейерный анализ+скачивание.
            // Как только файл проанализирован — он сразу начинает скачиваться, пока анализируется следующий.
            // Применяется только при обновлении (есть существующая папка), т.к. именно chunk-анализ тормозит старт.
            if (InstantInstall && existingDir != null)
            {
                FileLogger.Log($"");
                FileLogger.Log($"??? INSTANT INSTALL MODE — pipelined analyze+download ???");
                await InstallInPlaceInstantAsync(modelId, version, files, existingDir, modelDir, useDeltaPatching, linkedCt);

                try { Directory.Delete(cacheDir, true); } catch { }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

                var instantVersion = version;
                if (IsGameModel(modelId))
                {
                    var exeVersion = GetGameVersionFromExe(modelDir);
                    if (!string.IsNullOrWhiteSpace(exeVersion))
                    {
                        instantVersion = exeVersion;
                        FileLogger.Log($"[INSTANT] Using exe version: {instantVersion}");
                    }
                }

                SaveInstalledVersion(modelId, instantVersion);
                EnsureGameHashFile(modelId, modelDir);
                globalSw.Stop();

                FileLogger.Log($"");
                FileLogger.Log($"????????????????????????????????????????????????????????????????????");
                FileLogger.Log($"?  ? INSTANT INSTALL COMPLETED SUCCESSFULLY");
                FileLogger.Log($"?  Model: {modelId} v{instantVersion}");
                FileLogger.Log($"?  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
                FileLogger.Log($"????????????????????????????????????????????????????????????????????");
                FileLogger.Log($"");

                ReportProgress(modelId, instantVersion, DownloadState.Completed, 1, 1, 0, null, files.Count, files.Count);
                return new InstallResult { Success = true, ModelId = modelId, Version = instantVersion, InstallPath = modelDir };
            }

            // PHASE 1: Analyze
            FileLogger.Log($"");
            FileLogger.Log($"??? PHASE 1: ANALYZING FILES ???");
            var analysisSw = Stopwatch.StartNew();
            var downloadPlan = await BuildDownloadPlanAsync(modelId, files, existingDir, useDeltaPatching, linkedCt);
            analysisSw.Stop();
            
            long totalToDownload = downloadPlan.Sum(p => p.BytesToDownload);
            var copyCount = downloadPlan.Count(p => p.Action == FileAction.Copy);
            var fullDownloadCount = downloadPlan.Count(p => p.Action == FileAction.FullDownload);
            var deltaCount = downloadPlan.Count(p => p.Action == FileAction.Delta);
            
            FileLogger.Log($"[ANALYSIS] Completed in {analysisSw.ElapsedMilliseconds}ms");
            FileLogger.Log($"[ANALYSIS] Plan summary:");
            FileLogger.Log($"  Copy (unchanged):  {copyCount} files");
            FileLogger.Log($"  Full download:     {fullDownloadCount} files");
            FileLogger.Log($"  Delta update:      {deltaCount} files");
            FileLogger.Log($"  Total to download: {FormatBytes(totalToDownload)}");

            // FAST PATH: Если все файлы уже на месте и ничего не нужно качать - просто обновляем версию
            if (fullDownloadCount == 0 && deltaCount == 0 && copyCount == files.Count && existingDir != null)
            {
                FileLogger.Log($"");
                FileLogger.Log($"??? ALL FILES UP TO DATE - FAST PATH ???");
                FileLogger.Log($"[FAST] All {copyCount} files match, skipping copy/assembly");

                // Для игры - читаем версию из exe (она актуальнее чем с сервера)
                var verifiedVersion = version;
                if (IsGameModel(modelId))
                {
                    var exeVersion = GetGameVersionFromExe(modelDir);
                    if (!string.IsNullOrWhiteSpace(exeVersion))
                    {
                        verifiedVersion = exeVersion;
                    }
                }

                // Просто обновляем файл версии
                SaveInstalledVersion(modelId, verifiedVersion);
                globalSw.Stop();

                // Удаляем temp директорию если создали
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

                FileLogger.Log($"");
                FileLogger.Log($"????????????????????????????????????????????????????????????????????");
                FileLogger.Log($"?  VERIFICATION COMPLETED - ALL FILES OK");
                FileLogger.Log($"?  Model: {modelId} v{verifiedVersion}");
                FileLogger.Log($"?  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
                FileLogger.Log($"?  Status: No changes needed");
                FileLogger.Log($"????????????????????????????????????????????????????????????????????");
                FileLogger.Log($"");

                ReportProgress(modelId, verifiedVersion, DownloadState.Completed, 0, 0, 0, null, files.Count, files.Count);
                return new InstallResult { Success = true, ModelId = modelId, Version = verifiedVersion, InstallPath = modelDir };
            }

            // IN-PLACE UPDATE PATH: Есть существующая установка - обновляем только изменённые файлы на месте
            // Работает когда есть delta и/или full download, но большинство файлов не изменилось
            if (existingDir != null && copyCount > 0 && (deltaCount > 0 || fullDownloadCount > 0))
            {
                FileLogger.Log($"");
                FileLogger.Log($"? IN-PLACE UPDATE - NO COPY NEEDED ?");
                FileLogger.Log($"[INPLACE] {deltaCount} delta + {fullDownloadCount} full download, {copyCount} files unchanged (stay in place)");
                
                var sw = Stopwatch.StartNew();
                var downloadedData = await DownloadAllDataPhasedAsync(modelId, version, downloadPlan, cacheDir, totalToDownload, linkedCt);
                
                FileLogger.Log($"");
                FileLogger.Log($"? IN-PLACE ASSEMBLING ?");
                await AssembleFilesInPlaceAsync(downloadPlan, downloadedData, modelDir, linkedCt);
                
                sw.Stop();
                globalSw.Stop();
                
                // Очистка кэша
                try { Directory.Delete(cacheDir, true); } catch { }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }

                // Для игры - читаем версию из exe (она актуальнее чем с сервера)
                var inplaceVersion = version;
                if (IsGameModel(modelId))
                {
                    var exeVersion = GetGameVersionFromExe(modelDir);
                    if (!string.IsNullOrWhiteSpace(exeVersion))
                    {
                        inplaceVersion = exeVersion;
                        FileLogger.Log($"[INPLACE] Using exe version: {inplaceVersion}");
                    }
                }

                SaveInstalledVersion(modelId, inplaceVersion);
                EnsureGameHashFile(modelId, modelDir);

                FileLogger.Log($"");
                FileLogger.Log($"????????????????????????????????????????????????????????????????????");
                FileLogger.Log($"?  IN-PLACE UPDATE COMPLETED SUCCESSFULLY");
                FileLogger.Log($"?  Model: {modelId} v{inplaceVersion}");
                FileLogger.Log($"?  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
                FileLogger.Log($"?  Downloaded: {FormatBytes(totalToDownload)}");
                FileLogger.Log($"?  Files updated: {deltaCount + fullDownloadCount}, unchanged: {copyCount}");
                FileLogger.Log($"????????????????????????????????????????????????????????????????????");
                FileLogger.Log($"");

                ReportProgress(modelId, inplaceVersion, DownloadState.Completed, totalToDownload, totalToDownload, 0, null, files.Count, files.Count);
                return new InstallResult { Success = true, ModelId = modelId, Version = inplaceVersion, InstallPath = modelDir };
            }

            // FULL INSTALL PATH: Новая установка - нужен atomic swap
            FileLogger.Log($"");
            FileLogger.Log($"? PHASE 2: DOWNLOADING (FULL INSTALL) ?");
            var swFull = Stopwatch.StartNew();
            
            if (Mode == DownloadMode.Phased)
            {
                FileLogger.Log($"[DOWNLOAD] Using PHASED mode (download all, then assemble)");
                var downloadedData = await DownloadAllDataPhasedAsync(modelId, version, downloadPlan, cacheDir, totalToDownload, linkedCt);
                
                FileLogger.Log($"");
                FileLogger.Log($"??? PHASE 3: ASSEMBLING ???");
                await AssembleFilesAsync(downloadPlan, downloadedData, tempDir, linkedCt);
            }
            else
            {
                FileLogger.Log($"[DOWNLOAD] Using STREAMING mode (download and write in parallel)");
                await DownloadAndAssembleStreamingAsync(modelId, version, downloadPlan, tempDir, existingDir, totalToDownload, linkedCt);
            }
            
            swFull.Stop();
            FileLogger.Log($"");
            FileLogger.Log($"??? PHASE 4: FINALIZING ???");
            FileLogger.Log($"[FINALIZE] Download completed in {swFull.Elapsed.TotalSeconds:F1}s");
            FileLogger.Log($"[FINALIZE] Average speed: {FormatBytes((long)(totalToDownload / swFull.Elapsed.TotalSeconds))}/s");

            try { Directory.Delete(cacheDir, true); } catch { }

            // Освобождаем файловые дескрипторы перед перемещением
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100, linkedCt); // Даём Windows время освободить дескрипторы

            // Atomic swap с retry логикой
            FileLogger.Log($"[FINALIZE] Performing atomic directory swap...");
            await PerformAtomicSwapAsync(modelDir, tempDir, linkedCt);

            // Для игры - читаем версию из exe (она актуальнее чем с сервера)
            var actualVersion = version;
            if (IsGameModel(modelId))
            {
                var exeVersion = GetGameVersionFromExe(modelDir);
                if (!string.IsNullOrWhiteSpace(exeVersion))
                {
                    actualVersion = exeVersion;
                    FileLogger.Log($"[FINALIZE] Using exe version: {actualVersion}");
                }
            }

            SaveInstalledVersion(modelId, actualVersion);
            EnsureGameHashFile(modelId, modelDir);
            globalSw.Stop();

            FileLogger.Log($"");
            FileLogger.Log($"????????????????????????????????????????????????????????????????????");
            FileLogger.Log($"?  INSTALL COMPLETED SUCCESSFULLY");
            FileLogger.Log($"?  Model: {modelId} v{actualVersion}");
            FileLogger.Log($"?  Total time: {globalSw.Elapsed.TotalSeconds:F1}s");
            FileLogger.Log($"?  Downloaded: {FormatBytes(totalToDownload)}");
            FileLogger.Log($"????????????????????????????????????????????????????????????????????");
            FileLogger.Log($"");

            ReportProgress(modelId, actualVersion, DownloadState.Completed, totalToDownload, totalToDownload, 0, null, files.Count, files.Count);
            return new InstallResult { Success = true, ModelId = modelId, Version = actualVersion, InstallPath = modelDir };
        }
        catch (OperationCanceledException)
        {
            FileLogger.Log($"[INSTALL] CANCELLED by user");
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Error("Install failed", ex);
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { }
            return new InstallResult { Success = false, ModelId = modelId, Error = ex.Message };
        }
        finally
        {
            _downloadCts = null;
        }
    }

    /// <summary>
    /// Выполняет atomic swap директорий с retry логикой для обхода блокировок файлов
    /// </summary>
    private async Task PerformAtomicSwapAsync(string targetDir, string tempDir, CancellationToken ct)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 500;
        
        if (Directory.Exists(targetDir))
        {
            var oldDir = targetDir + ".old";
            
            // Удаляем старую .old папку если есть
            if (Directory.Exists(oldDir))
            {
                FileLogger.Log($"  Deleting old backup...");
                await RetryDeleteDirectoryAsync(oldDir, maxRetries, retryDelayMs, ct);
            }
            
            // Перемещаем существующую в .old
            FileLogger.Log($"  Moving existing -> .old");
            await RetryMoveDirectoryAsync(targetDir, oldDir, maxRetries, retryDelayMs, ct);
            
            // Перемещаем temp в target
            FileLogger.Log($"  Moving temp -> target");
            Directory.Move(tempDir, targetDir);
            
            // Удаляем .old
            FileLogger.Log($"  Deleting .old");
            _ = Task.Run(async () => {
                try { await RetryDeleteDirectoryAsync(oldDir, maxRetries, retryDelayMs, CancellationToken.None); }
                catch { /* Ignore cleanup errors */ }
            });
        }
        else
        {
            FileLogger.Log($"  Moving temp -> target (new install)");
            Directory.Move(tempDir, targetDir);
        }
    }

    private static async Task RetryMoveDirectoryAsync(string source, string dest, int maxRetries, int delayMs, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Directory.Move(source, dest);
                return;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                FileLogger.Log($"    Move attempt {attempt} failed: {ex.Message}, retrying in {delayMs}ms...");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(delayMs, ct);
                delayMs *= 2; // Exponential backoff
            }
        }
    }

    private static async Task RetryDeleteDirectoryAsync(string path, int maxRetries, int delayMs, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                // Попробуем сбросить атрибуты файлов
                try
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }
                catch { }
                await Task.Delay(delayMs, ct);
                delayMs *= 2;
            }
        }
    }

    private async Task<List<FilePlan>> BuildDownloadPlanAsync(string modelId, List<ServerFileInfo> files, string? existingDir, bool useDeltaPatching, CancellationToken ct)
    {
        FileLogger.Log($"[ANALYSIS] Starting file analysis for {files.Count} files...");
        var plans = new List<FilePlan>();
        var serverChunksCache = new ConcurrentDictionary<string, List<ServerChunkInfo>>();
        
        // Сначала проверяем размеры файлов - если размер совпадает, чанки не нужны
        // Ищем только файлы где размер НЕ совпадает для delta-анализа
        var deltaCandiates = files.Where(f => {
            if (!useDeltaPatching || !f.IsPak || !f.ChunksReady || existingDir == null) 
                return false;
            var localPath = Path.Combine(existingDir, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(localPath)) 
                return false;
            var localSize = new FileInfo(localPath).Length;
            // Только если размер ОТЛИЧАЕТСЯ - нужен анализ чанков
            return localSize != f.Size;
        }).ToList();
        
        FileLogger.Log($"[ANALYSIS] Found {deltaCandiates.Count} files with size mismatch (need chunk analysis)");
        
        if (deltaCandiates.Count > 0)
        {
            FileLogger.Log($"[ANALYSIS] Fetching chunk info for delta candidates...");
            var chunkTasks = deltaCandiates.Select(async f => { serverChunksCache[f.Path] = await GetFileChunksAsync(modelId, f.Path, ct); });
            await Task.WhenAll(chunkTasks);
        }

        int fileNum = 0;
        foreach (var file in files)
        {
            fileNum++;
            var existingPath = existingDir != null ? Path.Combine(existingDir, file.Path.Replace('/', Path.DirectorySeparatorChar)) : null;
            var existingFile = existingPath != null && File.Exists(existingPath) ? new FileInfo(existingPath) : null;
            
            var plan = new FilePlan { File = file, ExistingPath = existingPath, ExistingFile = existingFile };

            if (existingFile == null)
            {
                // Файл не существует - полная загрузка
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> FULL DOWNLOAD (new file, {FormatBytes(file.Size)})");
            }
            else if (existingFile.Length == file.Size)
            {
                // Размер совпадает - считаем файл идентичным, копируем без анализа чанков
                plan.Action = FileAction.Copy;
                plan.BytesToDownload = 0;
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> COPY (size match: {FormatBytes(file.Size)})");
            }
            else if (useDeltaPatching && serverChunksCache.TryGetValue(file.Path, out var serverChunks) && serverChunks.Count > 0)
            {
                // Размер отличается и есть чанки - анализируем для delta-патчинга
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> Analyzing chunks (size: local={FormatBytes(existingFile.Length)}, server={FormatBytes(file.Size)})...");
                var chunkSw = Stopwatch.StartNew();
                var localChunks = await FastCdcChunker.ChunkFileAsync(existingPath!, ct);
                var localByHash = localChunks.GroupBy(c => c.Hash).ToDictionary(g => g.Key, g => g.First());
                var chunksToDownload = serverChunks.Where(sc => !localByHash.ContainsKey(sc.Hash)).ToList();
                chunkSw.Stop();
                
                if (chunksToDownload.Count == 0)
                {
                    // Все чанки совпали несмотря на разный размер (редкий случай)
                    plan.Action = FileAction.Copy;
                    FileLogger.Log($"    -> COPY (all {serverChunks.Count} chunks match, analyzed in {chunkSw.ElapsedMilliseconds}ms)");
                }
                else if (chunksToDownload.Count == serverChunks.Count)
                {
                    plan.Action = FileAction.FullDownload;
                    plan.BytesToDownload = file.Size;
                    FileLogger.Log($"    -> FULL DOWNLOAD (0/{serverChunks.Count} chunks match, {FormatBytes(file.Size)})");
                }
                else
                {
                    plan.Action = FileAction.Delta;
                    plan.Chunks = serverChunks;
                    plan.ChunksToDownload = chunksToDownload;
                    plan.LocalChunksByHash = localByHash;
                    plan.BytesToDownload = chunksToDownload.Sum(c => (long)c.Size);
                    var savedBytes = file.Size - plan.BytesToDownload;
                    var savedPercent = file.Size > 0 ? (savedBytes * 100.0 / file.Size) : 0;
                    FileLogger.Log($"    -> DELTA ({chunksToDownload.Count}/{serverChunks.Count} chunks to download, {FormatBytes(plan.BytesToDownload)}, saving {savedPercent:F1}%)");
                }
            }
            else
            {
                // Размер отличается, но нет чанков или delta отключен - полная загрузка
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                var reason = $"size mismatch: local={FormatBytes(existingFile.Length)} server={FormatBytes(file.Size)}";
                FileLogger.Log($"  [{fileNum}/{files.Count}] {file.Path} -> FULL DOWNLOAD ({reason})");
            }
            
            plans.Add(plan);
        }
        
        return plans;
    }

    /// <summary>
    /// ? Мгновенная установка: анализирует файлы по очереди и сразу запускает скачивание+сборку
    /// каждого файла, как только он проанализирован, не дожидаясь окончания анализа всех файлов.
    /// Файлы обновляются in-place (на месте), неизменённые файлы пропускаются.
    /// </summary>
    private async Task InstallInPlaceInstantAsync(string modelId, string version, List<ServerFileInfo> files, string existingDir, string targetDir, bool useDeltaPatching, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var errorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = errorCts.Token;
        var networkSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        var diskSemaphore = new SemaphoreSlim(8);
        // Ограничиваем число одновременно обрабатываемых файлов, чтобы не держать в памяти слишком много чанков
        var fileSemaphore = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        long downloadedBytes = 0;
        long discoveredBytes = 0; // растущий знаменатель прогресса (обнаруженный объём к загрузке)
        int analyzed = 0;
        int processed = 0;
        int downloadStarted = 0;
        var processingTasks = new List<Task>();
        var tempPaths = new ConcurrentBag<string>();

        FileLogger.Log($"[INSTANT] Analyzing {files.Count} files (download starts immediately per file)...");

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            analyzed++;

            var existingPath = Path.Combine(existingDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var existingFile = File.Exists(existingPath) ? new FileInfo(existingPath) : null;
            var plan = new FilePlan { File = file, ExistingPath = existingPath, ExistingFile = existingFile };

            // ---- Классификация файла ----
            if (existingFile == null)
            {
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                FileLogger.Log($"  [{analyzed}/{files.Count}] {file.Path} -> FULL DOWNLOAD (new file, {FormatBytes(file.Size)})");
            }
            else if (existingFile.Length == file.Size)
            {
                // Размер совпадает — файл уже на месте, ничего делать не нужно
                FileLogger.Log($"  [{analyzed}/{files.Count}] {file.Path} -> SKIP (size match: {FormatBytes(file.Size)})");
                continue;
            }
            else if (useDeltaPatching && file.IsPak && file.ChunksReady)
            {
                // Получаем chunk-инфо сервера и анализируем дельту прямо сейчас
                var serverChunks = await GetFileChunksAsync(modelId, file.Path, ct);
                if (serverChunks.Count > 0)
                {
                    FileLogger.Log($"  [{analyzed}/{files.Count}] {file.Path} -> Analyzing chunks (local={FormatBytes(existingFile.Length)}, server={FormatBytes(file.Size)})...");
                    var chunkSw = Stopwatch.StartNew();
                    var localChunks = await FastCdcChunker.ChunkFileAsync(existingPath, ct);
                    var localByHash = localChunks.GroupBy(c => c.Hash).ToDictionary(g => g.Key, g => g.First());
                    var chunksToDownload = serverChunks.Where(scInner => !localByHash.ContainsKey(scInner.Hash)).ToList();
                    chunkSw.Stop();

                    if (chunksToDownload.Count == 0)
                    {
                        FileLogger.Log($"    -> SKIP (all {serverChunks.Count} chunks match, analyzed in {chunkSw.ElapsedMilliseconds}ms)");
                        continue;
                    }
                    else if (chunksToDownload.Count == serverChunks.Count)
                    {
                        plan.Action = FileAction.FullDownload;
                        plan.BytesToDownload = file.Size;
                        FileLogger.Log($"    -> FULL DOWNLOAD (0/{serverChunks.Count} chunks match, {FormatBytes(file.Size)})");
                    }
                    else
                    {
                        plan.Action = FileAction.Delta;
                        plan.Chunks = serverChunks;
                        plan.ChunksToDownload = chunksToDownload;
                        plan.LocalChunksByHash = localByHash;
                        plan.BytesToDownload = chunksToDownload.Sum(c => (long)c.Size);
                        var savedPercent = file.Size > 0 ? ((file.Size - plan.BytesToDownload) * 100.0 / file.Size) : 0;
                        FileLogger.Log($"    -> DELTA ({chunksToDownload.Count}/{serverChunks.Count} chunks, {FormatBytes(plan.BytesToDownload)}, saving {savedPercent:F1}%)");
                    }
                }
                else
                {
                    plan.Action = FileAction.FullDownload;
                    plan.BytesToDownload = file.Size;
                    FileLogger.Log($"  [{analyzed}/{files.Count}] {file.Path} -> FULL DOWNLOAD (no chunks, {FormatBytes(file.Size)})");
                }
            }
            else
            {
                plan.Action = FileAction.FullDownload;
                plan.BytesToDownload = file.Size;
                FileLogger.Log($"  [{analyzed}/{files.Count}] {file.Path} -> FULL DOWNLOAD (size mismatch: local={FormatBytes(existingFile.Length)} server={FormatBytes(file.Size)})");
            }

            // ---- Файл нужно обновить: СРАЗУ запускаем скачивание+сборку in-place в фоне ----
            Interlocked.Add(ref discoveredBytes, plan.BytesToDownload);
            var started = Interlocked.Increment(ref downloadStarted);
            FileLogger.Log($"  ? [START #{started}] Downloading {file.Path} immediately...");

            var capturedPlan = plan;
            var capturedTempPath = Path.Combine(targetDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar)) + ".new";
            tempPaths.Add(capturedTempPath);
            processingTasks.Add(Task.Run(async () =>
            {
                await fileSemaphore.WaitAsync(linkedCt);
                try
                {
                    await ProcessInstantFileAsync(modelId, version, capturedPlan, targetDir, networkSemaphore, diskSemaphore,
                        bytes =>
                        {
                            var total = Interlocked.Add(ref downloadedBytes, bytes);
                            UpdateSpeed(total);
                            var denom = Interlocked.Read(ref discoveredBytes);
                            ReportProgress(modelId, version, DownloadState.Downloading, total, denom, _currentSpeed, capturedPlan.File.Path, 0, files.Count);
                        }, linkedCt);
                    var done = Interlocked.Increment(ref processed);
                    FileLogger.Log($"  ? [DONE #{done}] {capturedPlan.File.Path}");
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                        FileLogger.Log($"  [FAIL] {capturedPlan.File.Path}: {ex.Message}");
                    errorCts.Cancel();
                    throw;
                }
                finally { fileSemaphore.Release(); }
            }, linkedCt));
        }

        FileLogger.Log($"[INSTANT] Analysis done, {downloadStarted} files queued. Waiting for downloads to finish...");
        try
        {
            await Task.WhenAll(processingTasks);
        }
        catch
        {
            // Дожидаемся завершения всех задач (в т.ч. отменённых), чтобы освободить файловые потоки перед очисткой
            try { await Task.WhenAll(processingTasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))); } catch { }

            foreach (var tp in tempPaths)
            {
                try { if (File.Exists(tp)) File.Delete(tp); } catch { }
            }

            networkSemaphore.Dispose();
            diskSemaphore.Dispose();
            fileSemaphore.Dispose();
            throw;
        }

        networkSemaphore.Dispose();
        diskSemaphore.Dispose();
        fileSemaphore.Dispose();

        sw.Stop();
        FileLogger.Log($"[INSTANT] Completed {processed} files in {sw.Elapsed.TotalSeconds:F1}s, total downloaded {FormatBytes(downloadedBytes)}");
    }

    /// <summary>
    /// Скачивает и собирает один файл прямо в целевой каталог (in-place) через временный .new файл.
    /// </summary>
    private async Task ProcessInstantFileAsync(string modelId, string version, FilePlan plan, string targetDir, SemaphoreSlim netSem, SemaphoreSlim diskSem, Action<long> onProgress, CancellationToken ct)
    {
        var targetPath = Path.Combine(targetDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (plan.Action == FileAction.FullDownload)
        {
            if (File.Exists(targetPath))
            {
                // Качаем во временный файл, затем атомарно заменяем
                var tempPath = targetPath + ".new";
                await DownloadFilePipelineAsync(modelId, plan.File, tempPath, netSem, diskSem, onProgress, ct);
                await ReplaceFileWithRetryAsync(targetPath, tempPath, ct);
            }
            else
            {
                // Новый файл — пишем сразу на место
                await DownloadFilePipelineAsync(modelId, plan.File, targetPath, netSem, diskSem, onProgress, ct);
            }
        }
        else if (plan.Action == FileAction.Delta)
        {
            var tempPath = targetPath + ".new";
            await DownloadDeltaPipelineAsync(modelId, plan, tempPath, netSem, diskSem, onProgress, ct);
            await ReplaceFileWithRetryAsync(targetPath, tempPath, ct);
        }
    }

    private async Task<DownloadedData> DownloadAllDataPhasedAsync(string modelId, string version, List<FilePlan> plans, string cacheDir, long totalToDownload, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long downloadedBytes = 0;
        var downloadedFiles = new ConcurrentDictionary<string, string>();
        var downloadedChunks = new ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>>();
        var semaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        var tasks = new List<Task>();
        
        var fullDownloads = plans.Where(p => p.Action == FileAction.FullDownload).ToList();
        var deltaDownloads = plans.Where(p => p.Action == FileAction.Delta).ToList();
        var totalChunks = deltaDownloads.Sum(p => p.ChunksToDownload?.Count ?? 0);
        
        FileLogger.Log($"[DOWNLOAD] Starting phased download:");
        FileLogger.Log($"  Full files: {fullDownloads.Count} ({FormatBytes(fullDownloads.Sum(p => p.BytesToDownload))})");
        FileLogger.Log($"  Delta files: {deltaDownloads.Count} ({totalChunks} chunks, {FormatBytes(deltaDownloads.Sum(p => p.BytesToDownload))})");
        FileLogger.Log($"  Concurrency: {_maxConcurrentDownloads} connections");
        
        int completedFiles = 0;
        int completedChunks = 0;
        var lastSpeedLog = DateTime.UtcNow;

        foreach (var plan in plans)
        {
            if (plan.Action == FileAction.FullDownload)
            {
                var cachePath = Path.Combine(cacheDir, Guid.NewGuid().ToString("N"));
                downloadedFiles[plan.File.Path] = cachePath;
                var filePath = plan.File.Path;
                var fileSize = plan.File.Size;
                
                tasks.Add(Task.Run(async () =>
                {
                    var fileSw = Stopwatch.StartNew();
                    FileLogger.Log($"[DOWNLOAD] START: {filePath} ({FormatBytes(fileSize)})");
                    
                    await DownloadFileMultiConnectionAsync(modelId, plan.File, cachePath, semaphore,
                        bytes => {
                            var total = Interlocked.Add(ref downloadedBytes, bytes);
                            UpdateSpeed(total);
                            
                            // Периодический лог скорости
                            if ((DateTime.UtcNow - lastSpeedLog).TotalSeconds >= 2)
                            {
                                lastSpeedLog = DateTime.UtcNow;
                                var percent = totalToDownload > 0 ? (total * 100.0 / totalToDownload) : 0;
                                FileLogger.Log($"[SPEED] {FormatBytes((long)_currentSpeed)}/s | {percent:F1}% | {FormatBytes(total)}/{FormatBytes(totalToDownload)}");
                            }
                            
                            ReportProgress(modelId, version, DownloadState.Downloading, total, totalToDownload, _currentSpeed, filePath, 0, plans.Count);
                        }, ct);
                    
                    fileSw.Stop();
                    var completed = Interlocked.Increment(ref completedFiles);
                    var speed = fileSw.Elapsed.TotalSeconds > 0 ? fileSize / fileSw.Elapsed.TotalSeconds : 0;
                    FileLogger.Log($"[DOWNLOAD] DONE: {filePath} in {fileSw.Elapsed.TotalSeconds:F1}s ({FormatBytes((long)speed)}/s) [{completed}/{fullDownloads.Count}]");
                }, ct));
            }
            else if (plan.Action == FileAction.Delta)
            {
                var chunks = new ConcurrentDictionary<int, byte[]>();
                downloadedChunks[plan.File.Path] = chunks;
                var filePath = plan.File.Path;
                var chunksCount = plan.ChunksToDownload!.Count;
                
                FileLogger.Log($"[DELTA] START: {filePath} ({chunksCount} chunks to download)");
                
                foreach (var chunk in plan.ChunksToDownload!)
                {
                    var c = chunk;
                    tasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(ct);
                        try
                        {
                            var chunkSw = Stopwatch.StartNew();
                            var bytes = await _httpClient.GetByteArrayAsync($"{_baseUrl}/chunk/{Uri.EscapeDataString(modelId)}/{c.Offset}/{c.Size}/{filePath}", ct);
                            chunks[c.Index] = bytes;
                            chunkSw.Stop();
                            
                            var total = Interlocked.Add(ref downloadedBytes, bytes.Length);
                            var chunkNum = Interlocked.Increment(ref completedChunks);
                            UpdateSpeed(total);
                            
                            if (chunkNum % 10 == 0 || chunkNum == totalChunks)
                            {
                                FileLogger.Log($"[CHUNK] {filePath} chunk#{c.Index} ({FormatBytes(bytes.Length)}) in {chunkSw.ElapsedMilliseconds}ms [{chunkNum}/{totalChunks}]");
                            }
                            
                            ReportProgress(modelId, version, DownloadState.Downloading, total, totalToDownload, _currentSpeed, filePath, 0, plans.Count);
                        }
                        finally { semaphore.Release(); }
                    }, ct));
                }
            }
        }

        await Task.WhenAll(tasks);
        semaphore.Dispose();
        
        sw.Stop();
        FileLogger.Log($"[DOWNLOAD] All downloads completed in {sw.Elapsed.TotalSeconds:F1}s");
        FileLogger.Log($"  Files: {completedFiles}, Chunks: {completedChunks}");
        FileLogger.Log($"  Total: {FormatBytes(downloadedBytes)}, Avg speed: {FormatBytes((long)(downloadedBytes / sw.Elapsed.TotalSeconds))}/s");
        
        return new DownloadedData { TotalBytes = downloadedBytes, Duration = sw.Elapsed, Files = downloadedFiles, Chunks = downloadedChunks };
    }

    private async Task DownloadFileMultiConnectionAsync(string modelId, ServerFileInfo file, string localPath, SemaphoreSlim semaphore, Action<long> onProgress, CancellationToken ct)
    {
        const long ChunkSize = 4 * 1024 * 1024;
        var url = $"{_baseUrl}/files/{Uri.EscapeDataString(modelId)}/{file.Path}";
        
        if (file.Size < 10 * 1024 * 1024) // Small file - single connection
        {
            await semaphore.WaitAsync(ct);
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
                await using var smallFileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await networkStream.ReadAsync(buffer, ct)) > 0)
                {
                    await smallFileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    onProgress(read);
                }
            }
            finally { semaphore.Release(); }
            return;
        }

        // Large file - multi-connection with Range requests
        var ranges = new List<(long Start, long End, int Index)>();
        long offset = 0;
        int index = 0;
        while (offset < file.Size)
        {
            var end = Math.Min(offset + ChunkSize - 1, file.Size - 1);
            ranges.Add((offset, end, index++));
            offset = end + 1;
        }

        var parts = new ConcurrentDictionary<int, byte[]>();
        var rangeTasks = ranges.Select(async r =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.Start, r.End);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var data = await response.Content.ReadAsByteArrayAsync(ct);
                parts[r.Index] = data;
                onProgress(data.Length);
            }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(rangeTasks);

        await using var largeFileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous);
        for (int i = 0; i < ranges.Count; i++)
            if (parts.TryGetValue(i, out var data))
                await largeFileStream.WriteAsync(data, ct);
    }

    private async Task AssembleFilesAsync(List<FilePlan> plans, DownloadedData data, string tempDir, CancellationToken ct)
    {
        FileLogger.Log($"[ASSEMBLE] Starting file assembly for {plans.Count} files...");
        var sw = Stopwatch.StartNew();
        int assembled = 0;
        
        foreach (var plan in plans)
        {
            var localPath = Path.Combine(tempDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            assembled++;

            switch (plan.Action)
            {
                case FileAction.Copy:
                    File.Copy(plan.ExistingPath!, localPath, true);
                    FileLogger.Log($"[ASSEMBLE] [{assembled}/{plans.Count}] COPY: {plan.File.Path}");
                    break;
                case FileAction.FullDownload:
                    if (data.Files.TryGetValue(plan.File.Path, out var cachePath))
                    {
                        File.Move(cachePath, localPath);
                        FileLogger.Log($"[ASSEMBLE] [{assembled}/{plans.Count}] MOVE: {plan.File.Path} ({FormatBytes(plan.File.Size)})");
                    }
                    break;
                case FileAction.Delta:
                    FileLogger.Log($"[ASSEMBLE] [{assembled}/{plans.Count}] DELTA: {plan.File.Path} ({plan.Chunks?.Count} chunks)");
                    await AssembleDeltaAsync(plan, data, localPath, ct);
                    break;
            }
        }
        
        sw.Stop();
        FileLogger.Log($"[ASSEMBLE] Completed {assembled} files in {sw.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Обновляет файлы in-place: delta файлы собираются из чанков, полные загрузки перемещаются из кэша
    /// Файлы Copy остаются на месте - не трогаем их вообще!
    /// </summary>
    private async Task AssembleFilesInPlaceAsync(List<FilePlan> plans, DownloadedData data, string targetDir, CancellationToken ct)
    {
        var deltaPlans = plans.Where(p => p.Action == FileAction.Delta).ToList();
        var fullDownloadPlans = plans.Where(p => p.Action == FileAction.FullDownload).ToList();
        var copyCount = plans.Count(p => p.Action == FileAction.Copy);
        
        FileLogger.Log($"[INPLACE] Target: {targetDir}");
        FileLogger.Log($"[INPLACE] Delta: {deltaPlans.Count}, Full: {fullDownloadPlans.Count}, Skip (unchanged): {copyCount}");
        
        var sw = Stopwatch.StartNew();
        int processed = 0;
        int totalToProcess = deltaPlans.Count + fullDownloadPlans.Count;
        
        // 1. Обрабатываем delta файлы
        foreach (var plan in deltaPlans)
        {
            processed++;
            var targetPath = Path.Combine(targetDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            var tempPath = targetPath + ".new";
            
            FileLogger.Log($"[INPLACE] [{processed}/{totalToProcess}] DELTA: {plan.File.Path}");
            
            // Собираем новый файл из существующего файла + скачанных чанков
            var chunks = data.Chunks.GetValueOrDefault(plan.File.Path) ?? new();
            var downloadedCount = chunks.Count;
            var localCount = (plan.Chunks?.Count ?? 0) - downloadedCount;
            
            FileLogger.Log($"  [DELTA] {downloadedCount} downloaded + {localCount} local chunks");
            
            // Записываем в временный файл
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous))
            await using (var existing = new FileStream(plan.ExistingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous))
            {
                long bytesWritten = 0;
                foreach (var sc in plan.Chunks!)
                {
                    if (chunks.TryRemove(sc.Index, out var chunkData))
                    {
                        await output.WriteAsync(chunkData, ct);
                        bytesWritten += chunkData.Length;
                    }
                    else if (plan.LocalChunksByHash?.TryGetValue(sc.Hash, out var lc) == true)
                    {
                        existing.Seek(lc.Offset, SeekOrigin.Begin);
                        var buf = new byte[lc.Size];
                        await existing.ReadExactlyAsync(buf, ct);
                        await output.WriteAsync(buf, ct);
                        bytesWritten += buf.Length;
                    }
                }
                FileLogger.Log($"  [DELTA] Written {FormatBytes(bytesWritten)}");
            }
            
            // Атомарная замена файла
            await ReplaceFileWithRetryAsync(targetPath, tempPath, ct);
        }
        
        // 2. Обрабатываем полные загрузки - просто перемещаем из кэша
        foreach (var plan in fullDownloadPlans)
        {
            processed++;
            var targetPath = Path.Combine(targetDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            
            FileLogger.Log($"[INPLACE] [{processed}/{totalToProcess}] FULL: {plan.File.Path} ({FormatBytes(plan.File.Size)})");
            
            if (data.Files.TryGetValue(plan.File.Path, out var cachePath) && File.Exists(cachePath))
            {
                // Если файл существует - делаем атомарную замену
                if (File.Exists(targetPath))
                {
                    var tempPath = targetPath + ".new";
                    File.Move(cachePath, tempPath, true);
                    await ReplaceFileWithRetryAsync(targetPath, tempPath, ct);
                }
                else
                {
                    // Новый файл - просто перемещаем
                    File.Move(cachePath, targetPath, true);
                }
                FileLogger.Log($"  [FULL] Replaced");
            }
            else
            {
                FileLogger.Log($"  [FULL] WARNING: Cache file not found!");
            }
        }
        
        sw.Stop();
        FileLogger.Log($"[INPLACE] Completed {processed} files in {sw.Elapsed.TotalSeconds:F1}s (skipped {copyCount} unchanged)");
    }
    
    /// <summary>
    /// Атомарная замена файла с retry
    /// </summary>
    private async Task ReplaceFileWithRetryAsync(string targetPath, string tempPath, CancellationToken ct)
    {
        // Освобождаем файловые дескрипторы
        GC.Collect();
        GC.WaitForPendingFinalizers();
        
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(tempPath, targetPath);
                FileLogger.Log($"  Replaced {Path.GetFileName(targetPath)}");
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
            {
                FileLogger.Log($"  Replace attempt {attempt} failed: {ex.Message}, retrying...");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200 * attempt, ct);
            }
        }
        
        // Последняя попытка - пусть выбросит исключение
        File.Delete(targetPath);
        File.Move(tempPath, targetPath);
    }

    /// <summary>
    /// Собирает только delta файлы прямо в целевую директорию, без копирования неизменённых файлов
    /// </summary>
    private async Task AssembleDeltaFilesInPlaceAsync(List<FilePlan> plans, DownloadedData data, string targetDir, CancellationToken ct)
    {
        var deltaPlans = plans.Where(p => p.Action == FileAction.Delta).ToList();
        FileLogger.Log($"[INPLACE] Assembling {deltaPlans.Count} delta files directly to {targetDir}...");
        var sw = Stopwatch.StartNew();
        int assembled = 0;
        
        foreach (var plan in deltaPlans)
        {
            assembled++;
            var targetPath = Path.Combine(targetDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            var tempPath = targetPath + ".new";
            
            FileLogger.Log($"[INPLACE] [{assembled}/{deltaPlans.Count}] DELTA: {plan.File.Path}");
            
            // Собираем новый файл во временный файл рядом с оригиналом
            var chunks = data.Chunks.GetValueOrDefault(plan.File.Path) ?? new();
            var downloadedCount = chunks.Count;
            var localCount = (plan.Chunks?.Count ?? 0) - downloadedCount;
            
            FileLogger.Log($"  [DELTA-ASSEMBLE] {downloadedCount} downloaded + {localCount} local chunks");
            
            // Записываем в временный файл
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous))
            await using (var existing = new FileStream(plan.ExistingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous))
            {
                long bytesWritten = 0;
                foreach (var sc in plan.Chunks!)
                {
                    if (chunks.TryRemove(sc.Index, out var chunkData))
                    {
                        await output.WriteAsync(chunkData, ct);
                        bytesWritten += chunkData.Length;
                    }
                    else if (plan.LocalChunksByHash?.TryGetValue(sc.Hash, out var lc) == true)
                    {
                        existing.Seek(lc.Offset, SeekOrigin.Begin);
                        var buf = new byte[lc.Size];
                        await existing.ReadExactlyAsync(buf, ct);
                        await output.WriteAsync(buf, ct);
                        bytesWritten += buf.Length;
                    }
                }
                FileLogger.Log($"  [DELTA-ASSEMBLE] Written {FormatBytes(bytesWritten)} to temp file");
            }
            
            // Теперь заменяем оригинальный файл на новый
            // Файловые дескрипторы закрыты благодаря using
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            // Retry логика для замены файла
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    File.Delete(targetPath);
                    File.Move(tempPath, targetPath);
                    FileLogger.Log($"  [DELTA-ASSEMBLE] Replaced {Path.GetFileName(targetPath)}");
                    break;
                }
                catch (IOException ex) when (attempt < 5)
                {
                    FileLogger.Log($"  [DELTA-ASSEMBLE] Replace attempt {attempt} failed: {ex.Message}, retrying...");
                    await Task.Delay(200 * attempt, ct);
                }
            }
        }
        
        sw.Stop();
        FileLogger.Log($"[INPLACE] Completed {assembled} delta files in {sw.Elapsed.TotalSeconds:F1}s");
    }

    private async Task AssembleDeltaAsync(FilePlan plan, DownloadedData data, string localPath, CancellationToken ct)
    {
        var chunks = data.Chunks.GetValueOrDefault(plan.File.Path) ?? new();
        var downloadedCount = chunks.Count;
        var localCount = (plan.Chunks?.Count ?? 0) - downloadedCount;
        
        FileLogger.Log($"  [DELTA-ASSEMBLE] {plan.File.Path}: {downloadedCount} downloaded + {localCount} local chunks");
        
        await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous);
        await using var existing = new FileStream(plan.ExistingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous);

        long bytesWritten = 0;
        foreach (var sc in plan.Chunks!)
        {
            if (chunks.TryRemove(sc.Index, out var chunkData))
            {
                await output.WriteAsync(chunkData, ct);
                bytesWritten += chunkData.Length;
            }
            else if (plan.LocalChunksByHash?.TryGetValue(sc.Hash, out var lc) == true)
            {
                existing.Seek(lc.Offset, SeekOrigin.Begin);
                var buf = new byte[lc.Size];
                await existing.ReadExactlyAsync(buf, ct);
                await output.WriteAsync(buf, ct);
                bytesWritten += buf.Length;
            }
        }
        
        FileLogger.Log($"  [DELTA-ASSEMBLE] Written {FormatBytes(bytesWritten)} to {Path.GetFileName(localPath)}");
    }

    private async Task DownloadAndAssembleStreamingAsync(string modelId, string version, List<FilePlan> plans, string tempDir, string? existingDir, long totalToDownload, CancellationToken ct)
    {
        FileLogger.Log($"[STREAMING] Starting streaming download+assemble...");
        var sw = Stopwatch.StartNew();
        long downloadedBytes = 0;
        var networkSemaphore = new SemaphoreSlim(_maxConcurrentDownloads);
        var diskSemaphore = new SemaphoreSlim(8);
        var tasks = new List<Task>();
        var lastSpeedLog = DateTime.UtcNow;
        int completedFiles = 0;

        foreach (var plan in plans)
        {
            var localPath = Path.Combine(tempDir, plan.File.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            if (plan.Action == FileAction.Copy)
            {
                File.Copy(plan.ExistingPath!, localPath, true);
                FileLogger.Log($"[STREAMING] COPY: {plan.File.Path}");
                continue;
            }

            if (plan.Action == FileAction.FullDownload)
            {
                var filePath = plan.File.Path;
                var fileSize = plan.File.Size;
                tasks.Add(DownloadFilePipelineAsync(modelId, plan.File, localPath, networkSemaphore, diskSemaphore,
                    bytes => {
                        var total = Interlocked.Add(ref downloadedBytes, bytes);
                        UpdateSpeed(total);
                        ReportProgress(modelId, version, DownloadState.Downloading, total, totalToDownload, _currentSpeed, plan.File.Path, 0, plans.Count);
                    }, ct));
            }
            else if (plan.Action == FileAction.Delta)
            {
                var filePath = plan.File.Path;
                tasks.Add(Task.Run(async () =>
                {
                    FileLogger.Log($"[STREAMING] DELTA START: {filePath}");
                    await DownloadDeltaPipelineAsync(modelId, plan, localPath, networkSemaphore, diskSemaphore,
                        bytes => {
                            var total = Interlocked.Add(ref downloadedBytes, bytes);
                            UpdateSpeed(total);
                            ReportProgress(modelId, version, DownloadState.Downloading, total, totalToDownload, _currentSpeed, filePath, 0, plans.Count);
                        }, ct);
                    var done = Interlocked.Increment(ref completedFiles);
                    FileLogger.Log($"[STREAMING] DELTA DONE: {filePath} [{done}/{plans.Count}]");
                }, ct));
            }
        }

        await Task.WhenAll(tasks);
        networkSemaphore.Dispose();
        diskSemaphore.Dispose();
        
        sw.Stop();
        FileLogger.Log($"[STREAMING] Completed in {sw.Elapsed.TotalSeconds:F1}s, avg speed: {FormatBytes((long)(downloadedBytes / sw.Elapsed.TotalSeconds))}/s");
    }

    private async Task DownloadFilePipelineAsync(string modelId, ServerFileInfo file, string localPath, SemaphoreSlim netSem, SemaphoreSlim diskSem, Action<long> onProgress, CancellationToken ct)
    {
        await netSem.WaitAsync(ct);
        var netReleased = false;
        Task? writeTask = null;
        Channel<byte[]>? channel = null;
        try
        {
            var url = $"{_baseUrl}/files/{Uri.EscapeDataString(modelId)}/{file.Path}";
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // Use unbounded channel so network never blocks on disk
            channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            var localChannel = channel;
            writeTask = Task.Run(async () =>
            {
                await diskSem.WaitAsync(ct);
                try
                {
                    await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                    await foreach (var chunk in localChannel.Reader.ReadAllAsync(ct))
                        await fileStream.WriteAsync(chunk, ct);
                }
                finally { diskSem.Release(); }
            }, ct);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[512 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                await localChannel.Writer.WriteAsync(copy, ct);
                onProgress(read);
            }

            localChannel.Writer.Complete();
            netSem.Release();
            netReleased = true;
            await writeTask;
        }
        catch (Exception ex)
        {
            // Завершаем канал (с ошибкой), чтобы writeTask вышел из ReadAllAsync и освободил файловый поток.
            channel?.Writer.TryComplete(ex);
            if (writeTask != null)
            {
                // Дожидаемся завершения записи, чтобы .new файл не остался открытым (иначе "файл используется").
                try { await writeTask; } catch { }
            }
            throw;
        }
        finally
        {
            if (!netReleased)
            {
                try { netSem.Release(); } catch { }
            }
        }
    }

    private async Task DownloadDeltaPipelineAsync(string modelId, FilePlan plan, string localPath, SemaphoreSlim netSem, SemaphoreSlim diskSem, Action<long> onProgress, CancellationToken ct)
    {
        var chunks = new ConcurrentDictionary<int, byte[]>();
        var chunkTasks = plan.ChunksToDownload!.Select(async c =>
        {
            await netSem.WaitAsync(ct);
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync($"{_baseUrl}/chunk/{Uri.EscapeDataString(modelId)}/{c.Offset}/{c.Size}/{plan.File.Path}", ct);
                chunks[c.Index] = bytes;
                onProgress(bytes.Length);
            }
            finally { netSem.Release(); }
        });

        await Task.WhenAll(chunkTasks);

        await diskSem.WaitAsync(ct);
        try
        {
            await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous);
            await using var existing = new FileStream(plan.ExistingPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.Asynchronous);

            foreach (var sc in plan.Chunks!)
            {
                if (chunks.TryRemove(sc.Index, out var data))
                    await output.WriteAsync(data, ct);
                else if (plan.LocalChunksByHash?.TryGetValue(sc.Hash, out var lc) == true)
                {
                    existing.Seek(lc.Offset, SeekOrigin.Begin);
                    var buf = new byte[lc.Size];
                    await existing.ReadExactlyAsync(buf, ct);
                    await output.WriteAsync(buf, ct);
                }
            }
        }
        finally { diskSem.Release(); }
    }

    private void UpdateSpeed(long total)
    {
        lock (_speedLock)
        {
            var now = DateTime.UtcNow;
            _speedSamples.Enqueue((now, total));
            while (_speedSamples.Count > 0 && (now - _speedSamples.Peek().Time).TotalSeconds > 3)
                _speedSamples.Dequeue();
            if (_speedSamples.Count >= 2)
            {
                var first = _speedSamples.Peek();
                var elapsed = (now - first.Time).TotalSeconds;
                if (elapsed > 0.1) _currentSpeed = (total - first.Bytes) / elapsed;
            }
        }
    }

    private void ReportProgress(string modelId, string version, DownloadState state, long downloaded, long total, double speed, string? file, int fileIdx, int fileCount)
    {
        if ((DateTime.UtcNow - _lastProgressUpdate) < _progressThrottle && state == DownloadState.Downloading) return;
        _lastProgressUpdate = DateTime.UtcNow;

        ProgressChanged?.Invoke(new DownloadProgress
        {
            ModelId = modelId,
            Version = version,
            State = state,
            DownloadedBytes = downloaded,
            TotalBytes = total,
            BytesPerSecond = speed,
            CurrentFile = file,
            CurrentFileIndex = fileIdx,
            TotalFiles = fileCount
        });
    }

    /// <summary>
    /// Сравнивает две версии. Возвращает >0 если v1 > v2, less than 0 если v1 less than v2, 0 если равны.
    /// </summary>
    public static int CompareVersions(string v1, string v2)
    {
        // Очищаем от префиксов
        var clean1 = v1.TrimStart('v', 'V').Trim();
        var clean2 = v2.TrimStart('v', 'V').Trim();

        // Пробуем разобрать как Version (поддерживает формат X.X.X.X)
        if (Version.TryParse(clean1, out var ver1) && 
            Version.TryParse(clean2, out var ver2))
        {
            return ver1.CompareTo(ver2);
        }

        // Пробуем сравнить как числа (для простых версий типа "123" vs "456")
        if (long.TryParse(clean1, out var num1) && long.TryParse(clean2, out var num2))
        {
            return num1.CompareTo(num2);
        }

        // Fallback: сравниваем как строки
        return string.Compare(clean1, clean2, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F2} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private string GetModelDirectory(string modelId)
    {
        if (IsGameModel(modelId))
            return _gameDirectory;
        else
            return Path.Combine(_modsDirectory, modelId);
    }
    
    private string? GetInstalledVersion(string modelId)
    {
        var dir = GetModelDirectory(modelId);

        // Для игры - читаем версию из exe файла
        if (IsGameModel(modelId))
        {
            var exeVersion = GetGameVersionFromExe(dir);
            if (exeVersion != null)
                return exeVersion;
        }

        // Fallback на .version файл (для модов и если exe не найден)
        var versionFile = Path.Combine(dir, ".version");
        return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;
    }
    
    private void SaveInstalledVersion(string modelId, string version)
    {
        var dir = GetModelDirectory(modelId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".version"), version);
    }

    private bool EnsureGameHashFile(string modelId, string gameDirectory)
    {
        if (!IsGameModel(modelId))
            return true;

        try
        {
            if (string.IsNullOrWhiteSpace(gameDirectory))
            {
                // Просто закомменчу, мб потом сделаю чтобы только в дебаге показывало , а то может кто-то запутается от этого сообщения в релизе
                //FileLogger.Log("[HASHE] Skipped: game directory is empty");
                return false;
            }

            var normalizedGameDirectory = Path.GetFullPath(gameDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            var expectedBytes = BuildWeeGamesHashBytes(normalizedGameDirectory, out var hardwareGuid);
            var weeGamesDirectory = Path.Combine(normalizedGameDirectory, WeeGamesEmuDirectoryName);
            var hashFilePath = Path.Combine(weeGamesDirectory, WeeGamesHashFileName);

            Directory.CreateDirectory(weeGamesDirectory);

            if (File.Exists(hashFilePath))
            {
                var currentBytes = File.ReadAllBytes(hashFilePath);
                if (currentBytes.AsSpan().SequenceEqual(expectedBytes))
                {
                    //FileLogger.Log($"[HASHE] OK: {hashFilePath} already matches generated hash for {hardwareGuid}");
                    return true;
                }

                //FileLogger.Log($"[HASHE] Mismatch detected, regenerating: {hashFilePath}");
            }
            else
            {
                //FileLogger.Log($"[HASHE] Creating missing hash file: {hashFilePath}");
            }

            using (var stream = new FileStream(hashFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                stream.Write(expectedBytes, 0, expectedBytes.Length);
                stream.Flush(true);
            }

            var writtenBytes = File.ReadAllBytes(hashFilePath);
            if (!writtenBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                //FileLogger.Log($"[HASHE] Verification failed after write: {hashFilePath}");
                return false;
            }

            //FileLogger.Log($"[HASHE] Written: {hashFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            //FileLogger.Log($"[HASHE] Failed to generate .hashe: {ex.Message}");
            return false;
        }
    }

    public bool EnsureCurrentGameHashFile(string? gameDirectory = null)
    {
        return EnsureGameHashFile("fullgame", gameDirectory ?? _gameDirectory);
    }

    private static byte[] BuildWeeGamesHashBytes(string gameDirectory, out string hardwareGuid)
    {
        if (!TryGetCurrentHardwareProfile(out var hardwareProfile))
            throw new InvalidOperationException("GetCurrentHwProfileA failed");

        hardwareGuid = hardwareProfile.HardwareProfileGuid.TrimEnd('\0');
        var pathHash = Djb33(gameDirectory, WeeGamesPathHashSeed);
        var finalHash = Djb33(hardwareGuid, pathHash);
        return BitConverter.GetBytes(finalHash);
    }

    private static uint Djb33(string text, uint seed)
    {
        var hash = seed;
        foreach (var value in Encoding.ASCII.GetBytes(text))
        {
            var signedByte = value >= 128 ? value - 256 : value;
            hash = unchecked((uint)((long)hash * 33 + signedByte));
        }

        return hash;
    }

    private static bool TryGetCurrentHardwareProfile(out HardwareProfileInfo hardwareProfile)
    {
        hardwareProfile = default;
        return GetCurrentHwProfileA(ref hardwareProfile);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "GetCurrentHwProfileA")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentHwProfileA(ref HardwareProfileInfo hardwareProfile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct HardwareProfileInfo
    {
        public uint DockInfo;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 39)]
        public string HardwareProfileGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string HardwareProfileName;
    }



    public async Task<VerifyResult> VerifyInstallationAsync(string modelId, CancellationToken ct = default)
    {
        FileLogger.Log($"");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        FileLogger.Log($"?  VERIFY INSTALLATION: {modelId}");
        FileLogger.Log($"????????????????????????????????????????????????????????????????????");
        
        var sw = Stopwatch.StartNew();
        var modelDir = GetModelDirectory(modelId);
        var installed = GetInstalledVersion(modelId);

        if (installed == null)
        {
            var isGame = IsGameModel(modelId);
            if (isGame)
            {
                FileLogger.Log($"[VERIFY] FAILED: Game not installed (exe not found in {modelDir})");
            }
            else
            {
                FileLogger.Log($"[VERIFY] FAILED: Model not installed (no .version file in {modelDir})");
            }
            return new VerifyResult { Success = false, Error = "Not installed" };
        }

        FileLogger.Log($"[VERIFY] Installed version: {installed}");
        FileLogger.Log($"[VERIFY] Fetching server file list...");

        var details = await GetModelDetailsAsync(modelId, ct);
        if (details == null)
        {
            FileLogger.Log($"[VERIFY] FAILED: Model not found on server");
            return new VerifyResult { Success = false, Error = "Model not found" };
        }

        var invalid = new List<string>();
        var invalidDetails = new List<InvalidFileDetails>();
        var files = details.Files ?? [];

        FileLogger.Log($"[VERIFY] Checking {files.Count} files in {modelDir}");
        FileLogger.Log($"[VERIFY] Expected total size: {FormatBytes(details.TotalSize)}");

        int checkedCount = 0;
        int validCount = 0;
        int missingCount = 0;
        int sizeMismatchCount = 0;
        long totalLocalSize = 0;

        foreach (var f in files)
        {
            checkedCount++;
            var path = Path.Combine(modelDir, f.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                missingCount++;
                invalid.Add(f.Path);
                invalidDetails.Add(new InvalidFileDetails { Path = f.Path, IsMissing = true, LocalSize = 0, ExpectedSize = f.Size });
                FileLogger.Log($"  [{checkedCount}/{files.Count}] MISSING: {f.Path}");
            }
            else
            {
                var localSize = new FileInfo(path).Length;
                totalLocalSize += localSize;

                if (localSize != f.Size)
                {
                    sizeMismatchCount++;
                    invalid.Add(f.Path);
                    invalidDetails.Add(new InvalidFileDetails { Path = f.Path, IsMissing = false, LocalSize = localSize, ExpectedSize = f.Size });
                    FileLogger.Log($"  [{checkedCount}/{files.Count}] SIZE MISMATCH: {f.Path} (local={FormatBytes(localSize)}, server={FormatBytes(f.Size)})");
                }
                else
                {
                    validCount++;
                    if (checkedCount % 50 == 0)
                    {
                        FileLogger.Log($"  [{checkedCount}/{files.Count}] Verified {validCount} files OK...");
                    }
                }
            }
        }
        
        sw.Stop();
        
        FileLogger.Log($"");
        FileLogger.Log($"[VERIFY] ??? RESULTS ???");
        FileLogger.Log($"  Total files:    {files.Count}");
        FileLogger.Log($"  Valid:          {validCount}");
        FileLogger.Log($"  Missing:        {missingCount}");
        FileLogger.Log($"  Size mismatch:  {sizeMismatchCount}");
        FileLogger.Log($"  Local size:     {FormatBytes(totalLocalSize)}");
        FileLogger.Log($"  Expected size:  {FormatBytes(details.TotalSize)}");
        FileLogger.Log($"  Time:           {sw.Elapsed.TotalSeconds:F1}s");
        
        if (invalid.Count == 0)
        {
            FileLogger.Log($"[VERIFY] ? VERIFICATION PASSED");
        }
        else
        {
            FileLogger.Log($"[VERIFY] ? VERIFICATION FAILED ({invalid.Count} invalid files)");
            foreach (var inv in invalid.Take(10))
                FileLogger.Log($"  - {inv}");
            if (invalid.Count > 10)
                FileLogger.Log($"  ... and {invalid.Count - 10} more");
        }
        
        FileLogger.Log($"");
        EnsureGameHashFile(modelId, modelDir);
        return new VerifyResult { Success = invalid.Count == 0, InvalidFiles = invalid, InvalidFileDetails = invalidDetails };
    }

    public async Task<ServerModelInfo?> GetGameSummaryAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/game";
        FileLogger.Log($"[API] GET {url}");
        try
        {
            var result = await _httpClient.GetFromJsonAsync<ServerModelInfo>(url, ct);
            FileLogger.Log($"[API] GET {url} -> OK: {result?.Name} v{result?.Version}");
            return result;
        }
        catch (HttpRequestException ex)
        {
            FileLogger.Log($"[API] GET {url} -> FAILED: {ex.Message}");
            return null;
        }
    }

    public void Cancel()
    {
        FileLogger.Log($"[CANCEL] Download cancelled by user");
        try
        {
            _downloadCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        FileLogger.Log($"[DISPOSE] UpdateManager disposing...");
        try
        {
            _downloadCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _downloadCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        return ValueTask.CompletedTask;
    }

    // Helper types
    private enum FileAction { Copy, FullDownload, Delta }
    private class FilePlan
    {
        public required ServerFileInfo File { get; init; }
        public string? ExistingPath { get; init; }
        public FileInfo? ExistingFile { get; init; }
        public FileAction Action { get; set; }
        public long BytesToDownload { get; set; }
        public List<ServerChunkInfo>? Chunks { get; set; }
        public List<ServerChunkInfo>? ChunksToDownload { get; set; }
        public Dictionary<string, LocalChunk>? LocalChunksByHash { get; set; }
    }
    private record DownloadedData
    {
        public long TotalBytes { get; init; }
        public TimeSpan Duration { get; init; }
        public ConcurrentDictionary<string, string> Files { get; init; } = new();
        public ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>> Chunks { get; init; } = new();
    }
}

// Settings & Config Models
public class LauncherSettings
{
    [JsonPropertyName("gamePath")]
    public string? GamePath { get; set; }
    
    [JsonPropertyName("modsPath")]
    public string? ModsPath { get; set; }
}

public class ExistingGameInfo
{
    public string Path { get; init; } = "";
    public string? InstalledVersion { get; init; }
    public string? LatestVersion { get; init; }
    public bool NeedsUpdate { get; init; }
    public bool HasGameFiles { get; init; }
    public int FileCount { get; init; }
}

// API Models
public record ModelsResponse { [JsonPropertyName("models")] public List<ServerModelInfo>? Models { get; init; } }
public record ServerModelInfo { [JsonPropertyName("id")] public string Id { get; init; } = ""; [JsonPropertyName("name")] public string Name { get; init; } = ""; [JsonPropertyName("version")] public string Version { get; init; } = ""; [JsonPropertyName("fileCount")] public int FileCount { get; init; } [JsonPropertyName("totalSize")] public long TotalSize { get; init; } [JsonPropertyName("totalSizeFormatted")] public string TotalSizeFormatted { get; init; } = ""; }
public record ServerModelDetails { [JsonPropertyName("id")] public string Id { get; init; } = ""; [JsonPropertyName("name")] public string Name { get; init; } = ""; [JsonPropertyName("version")] public string Version { get; init; } = ""; [JsonPropertyName("totalSize")] public long TotalSize { get; init; } [JsonPropertyName("fileCount")] public int FileCount { get; init; } [JsonPropertyName("files")] public List<ServerFileInfo>? Files { get; init; }}
public record ServerFileInfo { [JsonPropertyName("path")] public string Path { get; init; } = ""; [JsonPropertyName("size")] public long Size { get; init; } [JsonPropertyName("hash")] public string? Hash { get; init; } [JsonPropertyName("isPak")] public bool IsPak { get; init; } [JsonPropertyName("chunksReady")] public bool ChunksReady { get; init; } [JsonPropertyName("chunkCount")] public int ChunkCount { get; init; } }
public record ChunksResponse { [JsonPropertyName("chunks")] public List<ServerChunkInfo>? Chunks { get; init; } }
public record ServerChunkInfo { [JsonPropertyName("index")] public int Index { get; init; } [JsonPropertyName("offset")] public long Offset { get; init; } [JsonPropertyName("size")] public int Size { get; init; } [JsonPropertyName("hash")] public string Hash { get; init; } = ""; }
public record UpdateCheckResult { public string ModelId { get; init; } = ""; public bool IsInstalled { get; init; } public string? InstalledVersion { get; init; } public string? LatestVersion { get; init; } public bool UpdateAvailable { get; init; } public long TotalSize { get; init; } public int FileCount { get; init; } }
public record InstallResult { public bool Success { get; init; } public string ModelId { get; init; } = ""; public string? Version { get; init; } public string? InstallPath { get; init; } public string? Error { get; init; } }
public record VerifyResult { public bool Success { get; init; } public string? Error { get; init; } public List<string>? InvalidFiles { get; init; } public List<InvalidFileDetails>? InvalidFileDetails { get; init; } }
public record InvalidFileDetails { public string Path { get; init; } = ""; public bool IsMissing { get; init; } public bool IsExtra { get; init; } public long LocalSize { get; init; } public long ExpectedSize { get; init; } }
public record ServerInfoResponse { [JsonPropertyName("version")] public string Version { get; init; } = ""; [JsonPropertyName("game")] public ServerGameInfo? Game { get; init; } }
public record ServerGameInfo { [JsonPropertyName("id")] public string Id { get; init; } = ""; [JsonPropertyName("name")] public string Name { get; init; } = ""; [JsonPropertyName("version")] public string Version { get; init; } = ""; [JsonPropertyName("totalSize")] public long TotalSize { get; init; } [JsonPropertyName("totalSizeFormatted")] public string TotalSizeFormatted { get; init; } = ""; [JsonPropertyName("fileCount")] public int FileCount { get; init; } }

// Addons API Models
public record AddonsResponse { [JsonPropertyName("addons")] public List<ServerAddonInfo>? Addons { get; init; } }
public record ServerAddonInfo 
{ 
    [JsonPropertyName("id")] public string Id { get; init; } = "";              // FolderName для скачивания
    [JsonPropertyName("modId")] public string ModId { get; init; } = "";        // Уникальный ID мода
    [JsonPropertyName("name")] public string Name { get; init; } = ""; 
    [JsonPropertyName("version")] public string Version { get; init; } = ""; 
    [JsonPropertyName("fileCount")] public int FileCount { get; init; } 
    [JsonPropertyName("totalSize")] public long TotalSize { get; init; } 
    [JsonPropertyName("totalSizeFormatted")] public string TotalSizeFormatted { get; init; } = "";
    [JsonPropertyName("changelog")] public string? Changelog { get; init; }
    [JsonPropertyName("dependencies")] public List<AddonDependencyDto>? Dependencies { get; init; }
}
public record ServerAddonDetails 
{ 
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("modId")] public string ModId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = ""; 
    [JsonPropertyName("version")] public string Version { get; init; } = ""; 
    [JsonPropertyName("fileCount")] public int FileCount { get; init; } 
    [JsonPropertyName("totalSize")] public long TotalSize { get; init; } 
    [JsonPropertyName("totalSizeFormatted")] public string TotalSizeFormatted { get; init; } = "";
    [JsonPropertyName("changelog")] public string? Changelog { get; init; }
    [JsonPropertyName("files")] public List<ServerFileInfo>? Files { get; init; }
}
public record AddonDependencyDto
{
    [JsonPropertyName("modId")] public string ModId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
}


// Дорогой дневник, мне не подобрать слов, чтобы описать боль и унижение, которое я испытал
