using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using ArmaLauncherClient.Models;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private UpdateManager _updateManager;
    private readonly DeduplicationCache _cache;
    private readonly Dispatcher _dispatcher;
    private readonly NewsService _newsService;
    private readonly ServerMonitorService _serverMonitorService;
    private readonly DispatcherTimer _gameProcessMonitorTimer;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _autoRefreshCts;
    private Process? _trackedGameProcess;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ModelItemViewModel> Models { get; } = [];
    public ObservableCollection<NewsItem> News { get; } = [];
    public ObservableCollection<GameServerInfo> GameServers { get; } = [];

    /// <summary>
    /// Сервера только с модами для фильтра в каталоге модов
    /// </summary>
    public ObservableCollection<GameServerInfo> ServersForModsFilter { get; } = [];

    // Фильтруем серверы — показываем только те, у которых HasGame подтверждён.
    // Текущий выбранный сервер показываем всегда, даже если статус ещё не пришёл,
    // чтобы селектор не оставался пустым на короткое время фоновой проверки.
    public IEnumerable<ServerInfo> AvailableServers =>
        App.AvailableServers.Where(s => s.HasGame || s == App.CurrentServer);

    public ServerInfo? SelectedServer
    {
        get => App.CurrentServer;
        set
        {
            if (value == null || value == App.CurrentServer)
                return;
            _ = SwitchServerAsync(value);
        }
    }

    public string ServerUrl => _updateManager.ServerUrl;

    public UpdateManager GetUpdateManager() => _updateManager;

    private ModelItemViewModel? _mainModel;
    public ModelItemViewModel? MainModel
    {
        get => _mainModel;
        private set => SetMainModel(value);
    }

    private LauncherSection _activeSection = LauncherSection.Game;
    public LauncherSection ActiveSection
    {
        get => _activeSection;
        set
        {
            if (_activeSection == value) return;
            _activeSection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGameSection));
            OnPropertyChanged(nameof(IsNewsSection));
            OnPropertyChanged(nameof(IsModsSection));
            OnPropertyChanged(nameof(IsSettingsSection));
            OnPropertyChanged(nameof(IsServersSection));
        }
    }

    public bool IsGameSection => ActiveSection == LauncherSection.Game;
    public bool IsNewsSection => ActiveSection == LauncherSection.News;
    public bool IsModsSection => ActiveSection == LauncherSection.Mods;
    public bool IsSettingsSection => ActiveSection == LauncherSection.Settings;
    public bool IsServersSection => ActiveSection == LauncherSection.Servers;

    // Тема оформления
    private ThemeColors _theme = new();
    public ThemeColors Theme
    {
        get => _theme;
        set { _theme = value; OnPropertyChanged(); }
    }

    // Язык интерфейса
    private string _language = "ru";
    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            LocalizationManager.Instance.CurrentLanguage = value;
            OnPropertyChanged();
            // Refresh all computed properties that use localized strings
            OnPropertyChanged(nameof(GamePrimaryActionLabel));
            OnPropertyChanged(nameof(GameStatusText));
            SaveSettings();
        }
    }

    public string[] AvailableLanguages => LocalizationManager.SupportedLanguages;
    public string[] LanguageDisplayNames => LocalizationManager.LanguageDisplayNames;

    public int SelectedLanguageIndex
    {
        get => Array.IndexOf(LocalizationManager.SupportedLanguages, _language);
        set
        {
            if (value >= 0 && value < LocalizationManager.SupportedLanguages.Length)
                Language = LocalizationManager.SupportedLanguages[value];
        }
    }

    public string GamePrimaryActionLabel => EvaluateGameAction() switch
    {
        GameAction.Install => LocalizationManager.S("game_action_install"),
        GameAction.Update => LocalizationManager.S("game_action_update"),
        GameAction.Play => LocalizationManager.S("game_action_play"),
        _ => LocalizationManager.S("btn_refresh_catalog")
    };

    public bool IsPlayAction => EvaluateGameAction() == GameAction.Play;
    public bool IsGameRunning => CheckIsGameRunning();
    public bool HasMainModel => MainModel != null;
    public bool CanExecuteGameAction => !IsDownloading && MainModel != null && !(IsPlayAction && IsGameRunning);

    public string GameStatusText
    {
        get
        {
            if (_allServersUnavailable)
            {
                if (MainModel?.InstalledVersion != null)
                    return _noInternetConnection
                        ? "Нет интернета. Игра доступна в офлайн-запуске."
                        : "Все серверы недоступны. Игра доступна в офлайн-запуске.";

                return _noInternetConnection
                    ? "Нет интернета. Серверы недоступны."
                    : "Все серверы недоступны.";
            }

            if (MainModel == null)
                return LocalizationManager.S("game_not_selected");
            if (MainModel.InstalledVersion == null)
                return LocalizationManager.S("game_not_installed");
            if (MainModel.UpdateAvailable)
                return LocalizationManager.F("status_update_available", MainModel.LatestVersion);
            return LocalizationManager.F("status_installed_version", MainModel.InstalledVersion);
        }
    }

    public string GameInstallPath => _updateManager.GameInstallRoot;
    public string ModsInstallPath => _updateManager.ModsInstallRoot;

    // Путь к игре
    public string GamePath
    {
        get => _updateManager.GameInstallRoot;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _updateManager.GameInstallRoot)
                return;
            _updateManager.SetGamePath(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameInstallPath));
            InitializeLocalGameState();
            FileLogger.Log($"Game path changed to: {value}");
        }
    }

    // Путь к модам
    public string ModsPath
    {
        get => _updateManager.ModsInstallRoot;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _updateManager.ModsInstallRoot)
                return;
            _updateManager.SetModsPath(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModsInstallPath));
            OnPropertyChanged(nameof(LaunchParametersPreview));
            FileLogger.Log($"Mods path changed to: {value}");
        }
    }

    // Для совместимости
    public string CustomInstallPath
    {
        get => _updateManager.GameInstallRoot;
        set => GamePath = value;
    }

    public void ResetGamePath()
    {
        _updateManager.ResetGamePath();
        OnPropertyChanged(nameof(GamePath));
        OnPropertyChanged(nameof(GameInstallPath));
        InitializeLocalGameState();
        FileLogger.Log("Game path reset to default");
    }

    public void ResetModsPath()
    {
        _updateManager.ResetModsPath();
        OnPropertyChanged(nameof(ModsPath));
        OnPropertyChanged(nameof(ModsInstallPath));
        OnPropertyChanged(nameof(LaunchParametersPreview));
        FileLogger.Log("Mods path reset to default");
    }

    public void ResetInstallPath() => ResetGamePath();

    // Настройка параметров запуска игры
    private bool _useCustomLaunchParams = true;
    public bool UseCustomLaunchParams
    {
        get => _useCustomLaunchParams;
        set
        {
            _useCustomLaunchParams = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchParametersPreview));
            FileLogger.Log($"UseCustomLaunchParams changed to: {value}");
        }
    }

    public string LaunchParametersPreview
    {
        get
        {
            var modsPath = _updateManager.ModsInstallRoot;
            var tempAddonsDir = Path.Combine(Path.GetTempPath(), "ArmaLauncher", "TempDir");
            return $"-addonsDir \"{modsPath}\" -addonDownloadDir \"{tempAddonsDir}\"";
        }
    }

    #region Background Slideshow

    private readonly List<string> _backgroundImages = 
    [
        "/Images/ImagesPatch/1.jpg",
        "/Images/ImagesPatch/2.jpg",
        "/Images/ImagesPatch/3.jpg",
        "/Images/ImagesPatch/4.jpg",
        "/Images/ImagesPatch/5.jpg",
        "/Images/ImagesPatch/6.jpg",
        "/Images/ImagesPatch/7.jpg",
        "/Images/ImagesPatch/8.jpg",
        "/Images/ImagesPatch/9.jpg",
        "/Images/ImagesPatch/10.jpg",
        "/Images/ImagesPatch/11.jpg",
        "/Images/ImagesPatch/12.jpg",
        "/Images/ImagesPatch/13.jpg",
        "/Images/ImagesPatch/14.jpg",
        "/Images/ImagesPatch/15.jpg",
        "/Images/ImagesPatch/16.jpg",
        "/Images/ImagesPatch/17.jpg",
        "/Images/ImagesPatch/18.jpg",
        "/Images/ImagesPatch/19.jpg"
    ];

    private int _currentBackgroundIndex = 0;
    private System.Timers.Timer? _slideshowTimer;

    private string _currentBackgroundImage = "/Images/ImagesPatch/1.jpg";
    public string CurrentBackgroundImage
    {
        get => _currentBackgroundImage;
        set { _currentBackgroundImage = value; OnPropertyChanged(); }
    }

    private int _slideshowInterval = 5;
    public int SlideshowInterval
    {
        get => _slideshowInterval;
        set
        {
            if (value < 1) value = 1;
            if (value > 60) value = 60;
            _slideshowInterval = value;
            OnPropertyChanged();
            RestartSlideshowTimer();
            FileLogger.Log($"Slideshow interval changed to: {value}s");
        }
    }

    private bool _slideshowEnabled = true;
    public bool SlideshowEnabled
    {
        get => _slideshowEnabled;
        set
        {
            _slideshowEnabled = value;
            OnPropertyChanged();
            if (value)
                StartSlideshow();
            else
                StopSlideshow();
            FileLogger.Log($"Slideshow enabled: {value}");
        }
    }

    public void StartSlideshow()
    {
        StopSlideshow();

        _slideshowTimer = new System.Timers.Timer(_slideshowInterval * 1000);
        _slideshowTimer.Elapsed += (s, e) =>
        {
            _currentBackgroundIndex = (_currentBackgroundIndex + 1) % _backgroundImages.Count;
            _dispatcher.BeginInvoke(() =>
            {
                CurrentBackgroundImage = _backgroundImages[_currentBackgroundIndex];
            });
        };
        _slideshowTimer.AutoReset = true;
        _slideshowTimer.Start();
    }

    private void StopSlideshow()
    {
        _slideshowTimer?.Stop();
        _slideshowTimer?.Dispose();
        _slideshowTimer = null;
    }

    private void RestartSlideshowTimer()
    {
        if (_slideshowEnabled && _slideshowTimer != null)
        {
            _slideshowTimer.Interval = _slideshowInterval * 1000;
        }
    }

    #endregion

    #region Character Overlay Settings

    private bool _characterOverlayEnabled = true;
    public bool CharacterOverlayEnabled
    {
        get => _characterOverlayEnabled;
        set { _characterOverlayEnabled = value; OnPropertyChanged(); }
    }

    private double _characterOpacity = 0.7;
    public double CharacterOpacity
    {
        get => _characterOpacity;
        set 
        { 
            if (value < 0) value = 0;
            if (value > 1) value = 1;
            _characterOpacity = value; 
            OnPropertyChanged(); 
        }
    }

    // Горизонтальный отступ (отрицательный = влево от центра, положительный = вправо)
    private int _characterOffsetX = 350;
    public int CharacterOffsetX
    {
        get => _characterOffsetX;
        set 
        { 
            if (value < -500) value = -500;
            if (value > 500) value = 500;
            _characterOffsetX = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(CharacterMargin));
        }
    }

    // Вертикальный отступ (отрицательный = вверх от центра, положительный = вниз)
    private int _characterOffsetY = 150;
    public int CharacterOffsetY
    {
        get => _characterOffsetY;
        set 
        { 
            if (value < -300) value = -300;
            if (value > 300) value = 300;
            _characterOffsetY = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(CharacterMargin));
        }
    }

    // Вычисляемый Margin для позиционирования (Left, Top, Right, Bottom)
    public System.Windows.Thickness CharacterMargin
    {
        get
        {
            // Базовое позиционирование от центра
            // Положительный X = сдвиг вправо (добавляем в Left)
            // Положительный Y = сдвиг вниз (добавляем в Top)
            double left = Math.Max(0, _characterOffsetX);
            double right = Math.Max(0, -_characterOffsetX);
            double top = Math.Max(0, -_characterOffsetY);
            double bottom = Math.Max(0, _characterOffsetY);

            return new System.Windows.Thickness(left, top, right, bottom);
        }
    }

    private int _characterMaxHeight = 600;
    public int CharacterMaxHeight
    {
        get => _characterMaxHeight;
        set 
        { 
            if (value < 100) value = 100;
            if (value > 1000) value = 1000;
            _characterMaxHeight = value; 
            OnPropertyChanged(); 
        }
    }

    private string _characterImagePath = "/Images/Back/ar3zka.png";
    public string CharacterImagePath
    {
        get => _characterImagePath;
        set { _characterImagePath = value; OnPropertyChanged(); }
    }

    #endregion

    #region Settings Save/Load

    /// <summary>
    /// Сохраняет все настройки (тема, персонаж и т.д.)
    /// </summary>
    public void SaveSettings()
    {
        try
        {
            var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmaLauncher");
            Directory.CreateDirectory(settingsDir);
            var settingsPath = Path.Combine(settingsDir, "ui_settings.json");

            var settings = new UiSettings
            {
                // Язык
                Language = Language,

                // Персонаж
                CharacterEnabled = CharacterOverlayEnabled,
                CharacterOpacity = CharacterOpacity,
                CharacterMaxHeight = CharacterMaxHeight,
                CharacterOffsetX = CharacterOffsetX,
                CharacterOffsetY = CharacterOffsetY,
                CharacterImagePath = CharacterImagePath,

                // Слайдшоу
                SlideshowEnabled = SlideshowEnabled,
                SlideshowInterval = SlideshowInterval,

                // Установка
                InstantInstall = InstantInstall,

                // Тема
                ThemeBackgroundMain = ColorToHex(Theme.BackgroundMain),
                ThemeBackgroundPanel = ColorToHex(Theme.BackgroundPanel),
                ThemeBackgroundCard = ColorToHex(Theme.BackgroundCard),
                ThemeBackgroundInput = ColorToHex(Theme.BackgroundInput),
                ThemeBackgroundHover = ColorToHex(Theme.BackgroundHover),
                ThemeBorder = ColorToHex(Theme.Border),
                ThemeAccent = ColorToHex(Theme.Accent),
                ThemeAccentHover = ColorToHex(Theme.AccentHover),
                ThemeAccentLight = ColorToHex(Theme.AccentLight),
                ThemeButtonSecondary = ColorToHex(Theme.ButtonSecondary),
                ThemeSuccess = ColorToHex(Theme.Success),
                ThemeWarning = ColorToHex(Theme.Warning),
                ThemeError = ColorToHex(Theme.Error),
                ThemeInfo = ColorToHex(Theme.Info),
                ThemeTextPrimary = ColorToHex(Theme.TextPrimary),
                ThemeTextSecondary = ColorToHex(Theme.TextSecondary),
                ThemeTextMuted = ColorToHex(Theme.TextMuted),
                ThemeTextDark = ColorToHex(Theme.TextDark)
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
            FileLogger.Log($"[UI] Settings saved to {settingsPath}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[UI] Failed to save settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Загружает настройки при старте
    /// </summary>
    public void LoadSettings()
    {
        try
        {
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmaLauncher", "ui_settings.json");
            if (!File.Exists(settingsPath)) return;

            var json = File.ReadAllText(settingsPath);
            var settings = System.Text.Json.JsonSerializer.Deserialize<UiSettings>(json);
            if (settings == null) return;

            // Язык
            if (!string.IsNullOrEmpty(settings.Language))
            {
                _language = settings.Language;
                LocalizationManager.Instance.CurrentLanguage = settings.Language;
                OnPropertyChanged(nameof(Language));
                OnPropertyChanged(nameof(SelectedLanguageIndex));
            }

            // Персонаж
            CharacterOverlayEnabled = settings.CharacterEnabled;
            CharacterOpacity = settings.CharacterOpacity;
            CharacterMaxHeight = settings.CharacterMaxHeight;
            CharacterOffsetX = settings.CharacterOffsetX;
            CharacterOffsetY = settings.CharacterOffsetY;
            CharacterImagePath = settings.CharacterImagePath ?? "/Images/Back/ar3zka.png";

            // Слайдшоу
            SlideshowEnabled = settings.SlideshowEnabled;
            SlideshowInterval = settings.SlideshowInterval > 0 ? settings.SlideshowInterval : 5;

            // Установка
            InstantInstall = settings.InstantInstall;
            OnPropertyChanged(nameof(InstantInstall));

            // Тема
            if (!string.IsNullOrEmpty(settings.ThemeBackgroundMain)) Theme.BackgroundMain = ParseColor(settings.ThemeBackgroundMain);
            if (!string.IsNullOrEmpty(settings.ThemeBackgroundPanel)) Theme.BackgroundPanel = ParseColor(settings.ThemeBackgroundPanel);
            if (!string.IsNullOrEmpty(settings.ThemeBackgroundCard)) Theme.BackgroundCard = ParseColor(settings.ThemeBackgroundCard);
            if (!string.IsNullOrEmpty(settings.ThemeBackgroundInput)) Theme.BackgroundInput = ParseColor(settings.ThemeBackgroundInput);
            if (!string.IsNullOrEmpty(settings.ThemeBackgroundHover)) Theme.BackgroundHover = ParseColor(settings.ThemeBackgroundHover);
            if (!string.IsNullOrEmpty(settings.ThemeBorder)) Theme.Border = ParseColor(settings.ThemeBorder);
            if (!string.IsNullOrEmpty(settings.ThemeAccent)) Theme.Accent = ParseColor(settings.ThemeAccent);
            if (!string.IsNullOrEmpty(settings.ThemeAccentHover)) Theme.AccentHover = ParseColor(settings.ThemeAccentHover);
            if (!string.IsNullOrEmpty(settings.ThemeAccentLight)) Theme.AccentLight = ParseColor(settings.ThemeAccentLight);
            if (!string.IsNullOrEmpty(settings.ThemeButtonSecondary)) Theme.ButtonSecondary = ParseColor(settings.ThemeButtonSecondary);
            if (!string.IsNullOrEmpty(settings.ThemeSuccess)) Theme.Success = ParseColor(settings.ThemeSuccess);
            if (!string.IsNullOrEmpty(settings.ThemeWarning)) Theme.Warning = ParseColor(settings.ThemeWarning);
            if (!string.IsNullOrEmpty(settings.ThemeError)) Theme.Error = ParseColor(settings.ThemeError);
            if (!string.IsNullOrEmpty(settings.ThemeInfo)) Theme.Info = ParseColor(settings.ThemeInfo);
            if (!string.IsNullOrEmpty(settings.ThemeTextPrimary)) Theme.TextPrimary = ParseColor(settings.ThemeTextPrimary);
            if (!string.IsNullOrEmpty(settings.ThemeTextSecondary)) Theme.TextSecondary = ParseColor(settings.ThemeTextSecondary);
            if (!string.IsNullOrEmpty(settings.ThemeTextMuted)) Theme.TextMuted = ParseColor(settings.ThemeTextMuted);
            if (!string.IsNullOrEmpty(settings.ThemeTextDark)) Theme.TextDark = ParseColor(settings.ThemeTextDark);

            FileLogger.Log("[UI] Settings loaded");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[UI] Failed to load settings: {ex.Message}");
        }
    }

    private static string ColorToHex(System.Windows.Media.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static System.Windows.Media.Color ParseColor(string hex)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        }
        catch
        {
            return System.Windows.Media.Colors.White;
        }
    }

    #endregion

    // Информация об обнаруженной игре
    private ExistingGameInfo? _detectedGame;
    public ExistingGameInfo? DetectedGame
    {
        get => _detectedGame;
        set { _detectedGame = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDetectedGame)); OnPropertyChanged(nameof(DetectedGameStatus)); }
    }
    
    public bool HasDetectedGame => _detectedGame != null && _detectedGame.HasGameFiles;
    
    public string DetectedGameStatus
    {
        get
        {
            if (_detectedGame == null || !_detectedGame.HasGameFiles) return "";
            
            if (_detectedGame.InstalledVersion == null)
            {
                return LocalizationManager.S("status_game_unknown_ver");
            }

            if (_detectedGame.NeedsUpdate)
                return LocalizationManager.F("status_game_update", _detectedGame.InstalledVersion, _detectedGame.LatestVersion);

            return LocalizationManager.F("status_game_current", _detectedGame.InstalledVersion);
        }
    }

    public async Task DetectExistingGameAsync(string path)
    {
        StatusMessage = LocalizationManager.S("status_checking_folder");
        DetectedGame = await _updateManager.DetectExistingGameAsync(path);

        if (DetectedGame != null && DetectedGame.HasGameFiles)
        {
            if (DetectedGame.InstalledVersion == null)
            {
                StatusMessage = LocalizationManager.S("status_game_found_no_ver");
            }
            else if (DetectedGame.NeedsUpdate)
            {
                StatusMessage = LocalizationManager.F("status_game_update_needed", DetectedGame.InstalledVersion);
            }
            else
            {
                StatusMessage = LocalizationManager.F("status_game_up_to_date", DetectedGame.InstalledVersion);
            }
        }
        else
        {
            StatusMessage = LocalizationManager.S("status_game_not_found");
        }
    }

    private string _statusMessage = "Ready - Click 'Refresh' to load models";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _allServersUnavailable;
    private bool _noInternetConnection;

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
            UpdateGameBindings();
        }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    // Состояние «идёт фоновая проверка серверов» — для плашки в UI на старте.
    private bool _isCheckingServers;
    public bool IsCheckingServers
    {
        get => _isCheckingServers;
        private set
        {
            if (_isCheckingServers == value) return;
            _isCheckingServers = value;
            OnPropertyChanged();
        }
    }

    // Текст рядом со спиннером в плашке "Проверяем серверы..."
    private string _serverCheckStatusText = LocalizationManager.S("status_checking_servers");
    public string ServerCheckStatusText
    {
        get => _serverCheckStatusText;
        private set
        {
            if (_serverCheckStatusText == value) return;
            _serverCheckStatusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Включает/выключает индикатор фоновой проверки серверов.
    /// Вызывается из App при старте до и после фонового опроса.
    /// </summary>
    public void SetCheckingServersState(bool isChecking)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (isChecking)
            {
                ServerCheckStatusText = LocalizationManager.S("status_checking_servers");
            }
            IsCheckingServers = isChecking;
        });
    }

    private double _downloadPercent;
    public double DownloadPercent
    {
        get => _downloadPercent;
        set { _downloadPercent = value; OnPropertyChanged(); }
    }

    private string _downloadSpeed = "";
    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set { _downloadSpeed = value; OnPropertyChanged(); }
    }

    private string _downloadedSize = "";
    public string DownloadedSize
    {
        get => _downloadedSize;
        set { _downloadedSize = value; OnPropertyChanged(); }
    }

    private string _totalSize = "";
    public string TotalSize
    {
        get => _totalSize;
        set { _totalSize = value; OnPropertyChanged(); }
    }

    private string _remainingSize = "";
    public string RemainingSize
    {
        get => _remainingSize;
        set { _remainingSize = value; OnPropertyChanged(); }
    }

    private string _eta = "";
    public string Eta
    {
        get => _eta;
        set { _eta = value; OnPropertyChanged(); }
    }

    private string _currentFileName = "";
    public string CurrentFileName
    {
        get => _currentFileName;
        set { _currentFileName = value; OnPropertyChanged(); }
    }

    private string _fileProgress = "";
    public string FileProgress
    {
        get => _fileProgress;
        set { _fileProgress = value; OnPropertyChanged(); }
    }

    // Прогресс установки всех модов
    private string _modsInstallProgress = "";
    public string ModsInstallProgress
    {
        get => _modsInstallProgress;
        set { _modsInstallProgress = value; OnPropertyChanged(); }
    }

    private int _modsInstallCurrent;
    public int ModsInstallCurrent
    {
        get => _modsInstallCurrent;
        set { _modsInstallCurrent = value; OnPropertyChanged(); }
    }

    private int _modsInstallTotal;
    public int ModsInstallTotal
    {
        get => _modsInstallTotal;
        set { _modsInstallTotal = value; OnPropertyChanged(); }
    }

    private string _modsInstallCurrentName = "";
    public string ModsInstallCurrentName
    {
        get => _modsInstallCurrentName;
        set { _modsInstallCurrentName = value; OnPropertyChanged(); }
    }

    private string _speedTestResult = "";
    public string SpeedTestResult
    {
        get => _speedTestResult;
        set { _speedTestResult = value; OnPropertyChanged(); }
    }
    
    private bool _isSpeedTesting;
    public bool IsSpeedTesting
    {
        get => _isSpeedTesting;
        set { _isSpeedTesting = value; OnPropertyChanged(); }
    }

    public DownloadMode DownloadMode
    {
        get => _updateManager.Mode;
        set 
        { 
            _updateManager.Mode = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadModeDescription));
        }
    }
    
    public string DownloadModeDescription => DownloadMode switch
    {
        DownloadMode.Phased => "Phased (Max Speed, More RAM)",
        DownloadMode.Streaming => "Streaming (Less RAM)",
        _ => "Unknown"
    };

    public List<DownloadMode> AvailableDownloadModes { get; } = [DownloadMode.Phased, DownloadMode.Streaming];

    public bool InstantInstall
    {
        get => _updateManager.InstantInstall;
        set
        {
            _updateManager.InstantInstall = value;
            OnPropertyChanged();
            FileLogger.Log($"Instant install: {value}");
        }
    }

    #region Server Mods Filter

    private bool _isServerFilterExpanded;
    public bool IsServerFilterExpanded
    {
        get => _isServerFilterExpanded;
        set 
        { 
            _isServerFilterExpanded = value; 
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerFilterToggleIcon));
            OnPropertyChanged(nameof(ShowServerModsList));
            OnPropertyChanged(nameof(ShowFullModsCatalog));
        }
    }

    public string ServerFilterToggleIcon => IsServerFilterExpanded ? "▼" : "▶"; // "▼" : "▶"; "v" : ">";

    /// <summary>
    /// Показывать список модов сервера: только если фильтр развёрнут И выбран сервер
    /// </summary>
    public bool ShowServerModsList => IsServerFilterExpanded && HasSelectedModsServer;

    /// <summary>
    /// Показывать полный каталог модов: только если фильтр НЕ развёрнут
    /// </summary>
    public bool ShowFullModsCatalog => !IsServerFilterExpanded;

    public void ToggleServerFilter()
    {
        IsServerFilterExpanded = !IsServerFilterExpanded;
    }

    private GameServerInfo? _selectedModsServer;
    public GameServerInfo? SelectedModsServer
    {
        get => _selectedModsServer;
        set
        {
            if (_selectedModsServer == value) return;
            _selectedModsServer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedModsServer));
            OnPropertyChanged(nameof(ShowServerModsList));
            UpdateServerModsStatus();
        }
    }

    public bool HasSelectedModsServer => _selectedModsServer != null;

    public ObservableCollection<ServerModStatusViewModel> ServerModsStatus { get; } = [];

    private int _serverModsInstalledCount;
    public int ServerModsInstalledCount
    {
        get => _serverModsInstalledCount;
        set { _serverModsInstalledCount = value; OnPropertyChanged(); }
    }

    private int _serverModsMissingCount;
    public int ServerModsMissingCount
    {
        get => _serverModsMissingCount;
        set { _serverModsMissingCount = value; OnPropertyChanged(); }
    }

    private int _serverModsMismatchCount;
    public int ServerModsMismatchCount
    {
        get => _serverModsMismatchCount;
        set { _serverModsMismatchCount = value; OnPropertyChanged(); }
    }

    private bool _hasDownloadableServerMods;
    public bool HasDownloadableServerMods
    {
        get => _hasDownloadableServerMods;
        set { _hasDownloadableServerMods = value; OnPropertyChanged(); }
    }

    private string _serverModsStatusText = "";
    public string ServerModsStatusText
    {
        get => _serverModsStatusText;
        set { _serverModsStatusText = value; OnPropertyChanged(); }
    }

    // Статистика размеров для фильтра по серверу
    private string _serverModsTotalSize = "";
    public string ServerModsTotalSize
    {
        get => _serverModsTotalSize;
        set { _serverModsTotalSize = value; OnPropertyChanged(); }
    }

    private string _serverModsDownloadedSize = "";
    public string ServerModsDownloadedSize
    {
        get => _serverModsDownloadedSize;
        set { _serverModsDownloadedSize = value; OnPropertyChanged(); }
    }

    private string _serverModsToDownloadSize = "";
    public string ServerModsToDownloadSize
    {
        get => _serverModsToDownloadSize;
        set { _serverModsToDownloadSize = value; OnPropertyChanged(); }
    }

    // Статистика размеров для полного каталога модов
    public string CatalogTotalSize => FormatSize(Models.Sum(m => m.TotalSize));
    public string CatalogDownloadedSize => FormatSize(Models.Where(m => m.InstalledVersion != null).Sum(m => m.TotalSize));
    public string CatalogToDownloadSize => FormatSize(Models.Where(m => m.InstalledVersion == null).Sum(m => m.TotalSize));

    public void UpdateCatalogStats()
    {
        OnPropertyChanged(nameof(CatalogTotalSize));
        OnPropertyChanged(nameof(CatalogDownloadedSize));
        OnPropertyChanged(nameof(CatalogToDownloadSize));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public void ClearModsServerFilter()
    {
        SelectedModsServer = null;
        ServerModsStatus.Clear();
        ServerModsInstalledCount = 0;
        ServerModsMissingCount = 0;
        ServerModsMismatchCount = 0;
        HasDownloadableServerMods = false;
        ServerModsStatusText = "";
    }

    private void UpdateServerModsStatus()
    {
        ServerModsStatus.Clear();

        if (_selectedModsServer == null)
        {
            ServerModsInstalledCount = 0;
            ServerModsMissingCount = 0;
            ServerModsMismatchCount = 0;
            HasDownloadableServerMods = false;
            ServerModsStatusText = "";
            return;
        }

        // Словарь для быстрого поиска модели по ModId (для скачивания).
        // В каталоге лаунчера могут встречаться дубли одного ModId, поэтому
        // сначала дедуплицируем так же, как в ServerModsStatusDialog.
        var catalogMods = Models
            .Where(m => !string.IsNullOrWhiteSpace(m.ModId))
            .GroupBy(m => m.ModId!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateCatalogMods = catalogMods.Where(g => g.Count() > 1).ToList();
        foreach (var duplicate in duplicateCatalogMods)
        {
            FileLogger.Log($"[SERVER-FILTER] Duplicate catalog modId detected: {duplicate.Key} ({duplicate.Count()} entries)");
        }

        var downloadableMods = catalogMods
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var uniqueServerMods = _selectedModsServer.Mods
            .Where(m => !string.IsNullOrWhiteSpace(m.ModId))
            .GroupBy(m => m.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        int installed = 0, missing = 0, mismatch = 0;
        var items = new List<ServerModStatusViewModel>();

        foreach (var serverMod in uniqueServerMods)
        {
            var item = new ServerModStatusViewModel
            {
                ModId = serverMod.ModId,
                Name = serverMod.Name,
                ServerVersion = serverMod.Version
            };

            // Ищем мод в каталоге для скачивания
            ModelItemViewModel? catalogModel = null;
            downloadableMods.TryGetValue(serverMod.ModId, out catalogModel);

            // Проверяем установку через UpdateManager (читает версию из meta/ServerData.json)
            string? folderId = catalogModel?.Id;
            UpdateCheckResult? localCheck = null;

            if (folderId != null)
            {
                localCheck = _updateManager.CheckAddonInstallation(folderId, serverMod.Version);
            }

            if (localCheck?.IsInstalled == true)
            {
                item.IsInstalled = true;
                item.LocalVersion = localCheck.InstalledVersion ?? "unknown";
                item.FolderId = folderId;
                item.Model = catalogModel;

                if (item.LocalVersion == serverMod.Version || item.LocalVersion == "unknown")
                {
                    // Версия совпадает или неизвестна - считаем OK
                    item.Status = ServerModStatus.Installed;
                    installed++;
                }
                else
                {
                    // Сравниваем версии: LocalVersion vs ServerVersion
                    var comparison = CompareVersions(item.LocalVersion, serverMod.Version);

                    if (comparison != 0)
                    {
                        FileLogger.Log($"[VERSION] {serverMod.Name}: local={item.LocalVersion} vs server={serverMod.Version}, comparison={comparison}");
                    }

                    if (comparison >= 0)
                    {
                        // Локальная новее или равна серверной - OK
                        item.Status = comparison > 0 ? ServerModStatus.VersionNewer : ServerModStatus.Installed;
                        installed++;
                    }
                    else
                    {
                        // Локальная старше (устарела), нужно обновление
                        item.Status = ServerModStatus.VersionOlder;
                        mismatch++;
                    }
                }
            }
            else if (catalogModel != null)
            {
                // Мод не установлен, но доступен для скачивания
                item.Status = ServerModStatus.MissingCanDownload;
                item.CanDownload = true;
                item.FolderId = catalogModel.Id;
                item.LatestVersion = catalogModel.LatestVersion;
                item.Model = catalogModel;
                missing++;
            }
            else
            {
                // Мод не установлен и недоступен для скачивания
                item.Status = ServerModStatus.Missing;
                missing++;
            }

            items.Add(item);
        }

        // Сортируем: сначала проблемные (отсутствующие, потом устаревшие, потом новее, потом OK)
        items.Sort((a, b) =>
        {
            int GetPriority(ServerModStatus s) => s switch
            {
                ServerModStatus.Missing => 0,
                ServerModStatus.MissingCanDownload => 1,
                ServerModStatus.VersionOlder => 2,
                ServerModStatus.VersionNewer => 3,
                ServerModStatus.Installed => 4,
                _ => 5
            };
            return GetPriority(a.Status).CompareTo(GetPriority(b.Status));
        });

        foreach (var item in items)
            ServerModsStatus.Add(item);

        ServerModsInstalledCount = installed;
        ServerModsMissingCount = missing;
        ServerModsMismatchCount = mismatch;
        HasDownloadableServerMods = items.Any(i => i.CanDownload);

        if (missing == 0 && mismatch == 0)
            ServerModsStatusText = LocalizationManager.S("status_all_compatible");
        else if (missing > 0)
            ServerModsStatusText = LocalizationManager.F("status_need_mods", missing);
        else
            ServerModsStatusText = LocalizationManager.F("status_version_mismatch", mismatch);

        // Вычисляем размеры
        long totalSize = 0;
        long downloadedSize = 0;
        long toDownloadSize = 0;

        foreach (var item in items)
        {
            if (item.Model != null)
            {
                totalSize += item.Model.TotalSize;
                if (item.IsInstalled)
                    downloadedSize += item.Model.TotalSize;
                else if (item.CanDownload)
                    toDownloadSize += item.Model.TotalSize;
            }
        }

        ServerModsTotalSize = FormatSize(totalSize);
        ServerModsDownloadedSize = FormatSize(downloadedSize);
        ServerModsToDownloadSize = FormatSize(toDownloadSize);
    }

    /// <summary>
    /// Сравнивает две версии. Возвращает >0 если v1 > v2, <0 если v1 < v2, 0 если равны.
    /// </summary>
    private static int CompareVersions(string v1, string v2)
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

    public async Task InstallMissingServerModsAsync()
    {
        var missingMods = ServerModsStatus
            .Where(m => m.CanDownload && m.Model != null)
            .Select(m => m.Model!)
            .ToList();

        if (missingMods.Count == 0)
        {
            StatusMessage = LocalizationManager.S("status_no_mods_download");
            return;
        }

        // Создаём CTS для всей операции
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;

        var total = missingMods.Count;
        var current = 0;
        var installed = 0;
        var cancelled = false;

        foreach (var model in missingMods)
        {
            // Проверяем отмену в начале каждой итерации
            if (cancellationToken.IsCancellationRequested)
            {
                FileLogger.Log($"[INSTALL-MISSING] Cancelled by user at {current}/{total}");
                cancelled = true;
                break;
            }

            current++;
            StatusMessage = LocalizationManager.F("status_downloading", current, total, model.DisplayName);

            try
            {
                await InstallAsync(model);

                // Проверяем отмену после установки
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

                installed++;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // Проверяем не была ли это скрытая отмена
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                FileLogger.Error($"[INSTALL-MISSING] Failed: {model.Id}", ex);
            }
        }

        // Обновляем статусы после установки
        UpdateServerModsStatus();

        if (cancelled)
        {
            StatusMessage = LocalizationManager.F("status_cancelled_mods", installed, total);
        }
        else
        {
            StatusMessage = LocalizationManager.F("status_installed_mods", installed);
        }

        // Очищаем CTS после завершения массовой операции
        _cts?.Dispose();
        _cts = null;
        IsDownloading = false;
    }

    #endregion

    public MainViewModel(UpdateManager updateManager, DeduplicationCache cache, HttpClient httpClient)
    {
        _updateManager = updateManager;
        _cache = cache;
        _dispatcher = Application.Current.Dispatcher;
        _newsService = new NewsService(httpClient);
        _serverMonitorService = new ServerMonitorService(httpClient);
        _gameProcessMonitorTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _gameProcessMonitorTimer.Tick += (_, _) => RefreshGameRunningState();
        _updateManager.ProgressChanged += OnProgressChanged;

        FileLogger.Log("MainViewModel created");

        // Сразу инициализируем локальное состояние игры без ожидания проверки серверов.
        // Лаунчер всегда должен знать: игра установлена или нет, и какую локальную версию он видит.
        InitializeLocalGameState();

        // Запускаем слайдшоу фона
        StartSlideshow();

        // Предзагрузка всех данных в фоне при старте
        _ = PreloadAllDataAsync();

        RefreshGameRunningState();
        _gameProcessMonitorTimer.Start();
    }

    private void InitializeLocalGameState()
    {
        try
        {
            var installDir = _updateManager.GameInstallRoot;
            var exePath = Path.Combine(installDir, "ArmaReforgerSteam.exe");
            var installedVersion = UpdateManager.GetGameVersionFromExe(installDir);
            var isInstalled = File.Exists(exePath) || !string.IsNullOrWhiteSpace(installedVersion);
            var currentGameModel = _mainModel;
            var latestVersion = currentGameModel?.LatestVersion;
            var updateAvailable = isInstalled
                && !string.IsNullOrWhiteSpace(installedVersion)
                && !string.IsNullOrWhiteSpace(latestVersion)
                && UpdateManager.CompareVersions(latestVersion!, installedVersion!) > 0;

            SetMainModel(new ModelItemViewModel
            {
                Id = "fullgame",
                Name = currentGameModel?.Name ?? "Arma Reforger",
                InstalledVersion = isInstalled ? installedVersion : null,
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                TotalSize = currentGameModel?.TotalSize ?? 0,
                FileCount = currentGameModel?.FileCount ?? 0,
                SizeFormatted = currentGameModel?.SizeFormatted ?? ""
            });

            FileLogger.Log($"[GAME-INIT] Local game state initialized. Installed={isInstalled}, Version={installedVersion ?? "none"}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[GAME-INIT] Failed to initialize local game state: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Предзагружает все данные (модели, новости, серверы) в фоне при старте приложения
    /// </summary>
    private async Task PreloadAllDataAsync()
    {
        // Загружаем всё параллельно для максимальной скорости
        var modelsTask = PreloadModelsAsync();
        var newsTask = LoadNewsInBackgroundAsync();
        var serversTask = LoadServersInBackgroundAsync();

        await Task.WhenAll(modelsTask, newsTask, serversTask);

        // Запускаем периодическое обновление серверов каждые 30 секунд
        _ = StartServersAutoRefreshAsync();
    }
    
    private async Task PreloadModelsAsync()
    {
        try
        {
            // Загружаем модели в фоне
            await RefreshModelsAsync();
            FileLogger.Log($"[PRELOAD] Models loaded");

            // После загрузки уведомляем пользователя если есть обновления
            NotifyAvailableUpdates();
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[PRELOAD] Models failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Проверяет загруженные модели и показывает уведомление если доступны обновления
    /// </summary>
    private void NotifyAvailableUpdates()
    {
        _dispatcher.Invoke(() =>
        {
            // Обновление игры: установлена, но доступна новая версия
            bool gameUpdate = MainModel != null
                && MainModel.InstalledVersion != null
                && MainModel.UpdateAvailable;

            // Обновления модов: установлены, но доступна новая версия
            int addonUpdates = Models.Count(m =>
                m.InstalledVersion != null && m.UpdateAvailable);

            if (!gameUpdate && addonUpdates == 0)
                return;

            FileLogger.Log($"[UPDATE-NOTIFY] gameUpdate={gameUpdate}, addonUpdates={addonUpdates}");

            // Формируем сообщение
            string message;
            if (gameUpdate && addonUpdates > 0)
                message = LocalizationManager.F("update_notify_game_and_addons", addonUpdates);
            else if (gameUpdate)
                message = LocalizationManager.S("update_notify_game");
            else
                message = LocalizationManager.F("update_notify_addons", addonUpdates);

            // Показываем в статусной строке
            int totalUpdates = (gameUpdate ? 1 : 0) + addonUpdates;
            StatusMessage = LocalizationManager.F("status_updates_available", totalUpdates);

            // Показываем всплывающее уведомление
            var owner = Application.Current.MainWindow;
            if (owner != null)
            {
                UC.NotificationDialog.Show(
                    owner,
                    LocalizationManager.S("update_notify_title"),
                    message,
                    UC.NotificationDialogType.Info);
            }
        });
    }
    
    private async Task StartServersAutoRefreshAsync()
    {
        _autoRefreshCts = new CancellationTokenSource();
        var token = _autoRefreshCts.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);

                var servers = await _serverMonitorService.GetServersAsync();

                _dispatcher.Invoke(() =>
                {
                    // Сохраняем состояние перед обновлением
                    var selectedServerId = _selectedModsServer?.Id;
                    var wasExpanded = _isServerFilterExpanded;

                    // Обновляем список серверов для фильтра модов (только с модами)
                    ServersForModsFilter.Clear();
                    foreach (var server in servers.Where(s => s.Mods.Count > 0))
                    {
                        ServersForModsFilter.Add(server);
                    }

                    // Обновляем основной список серверов для мониторинга (ВСЕ сервера)
                    GameServers.Clear();
                    foreach (var server in servers)
                    {
                        GameServers.Add(server);
                    }

                    // Восстанавливаем состояние фильтра модов
                    if (selectedServerId != null)
                    {
                        var restoredServer = ServersForModsFilter.FirstOrDefault(s => s.Id == selectedServerId);
                        if (restoredServer != null)
                        {
                            _selectedModsServer = restoredServer;
                            OnPropertyChanged(nameof(SelectedModsServer));
                            OnPropertyChanged(nameof(HasSelectedModsServer));
                            OnPropertyChanged(nameof(ShowServerModsList));
                            UpdateServerModsStatus();
                        }
                        else if (ServersForModsFilter.Count > 0)
                        {
                            // Если выбранный сервер исчез, выбираем первый
                            SelectedModsServer = ServersForModsFilter.First();
                        }
                    }

                    // Восстанавливаем состояние развёрнутости
                    if (_isServerFilterExpanded != wasExpanded)
                    {
                        _isServerFilterExpanded = wasExpanded;
                        OnPropertyChanged(nameof(IsServerFilterExpanded));
                        OnPropertyChanged(nameof(ServerFilterToggleIcon));
                        OnPropertyChanged(nameof(ShowServerModsList));
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Игнорируем ошибки автообновления
            }
        }
    }
    
    private async Task LoadNewsInBackgroundAsync()
    {
        try
        {
            var news = await _newsService.GetNewsAsync();
            
            _dispatcher.Invoke(() =>
            {
                if (News.Count == 0) // Только если ещё не загружены
                {
                    foreach (var item in news)
                    {
                        News.Add(item);
                    }
                }
            });
            
            FileLogger.Log($"[PRELOAD] News loaded: {news.Count} items");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[PRELOAD] News failed: {ex.Message}");
        }
    }
    
    private async Task LoadServersInBackgroundAsync()
    {
        try
        {
            var servers = await _serverMonitorService.GetServersAsync();

            _dispatcher.Invoke(() =>
            {
                if (ServersForModsFilter.Count == 0) // Только если ещё не загружены
                {
                    // Добавляем серверы с модами в фильтр
                    foreach (var server in servers.Where(s => s.Mods.Count > 0))
                    {
                        ServersForModsFilter.Add(server);
                    }

                    // Автоматически выбираем первый сервер для фильтра модов
                    if (ServersForModsFilter.Count > 0 && _selectedModsServer == null)
                    {
                        SelectedModsServer = ServersForModsFilter.First();
                    }
                }

                if (GameServers.Count == 0)
                {
                    // Добавляем ВСЕ серверы в мониторинг
                    foreach (var server in servers)
                    {
                        GameServers.Add(server);
                    }
                }
            });

            FileLogger.Log($"[PRELOAD] Servers loaded: {servers.Count} total, {ServersForModsFilter.Count} with mods");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[PRELOAD] Servers failed: {ex.Message}");
        }
    }
    
    public void SetActiveSection(LauncherSection section)
    {
        ActiveSection = section;
        
        // Загружаем новости при переходе на вкладку
        if (section == LauncherSection.News && News.Count == 0)
        {
            _ = LoadNewsAsync();
        }
        
        // Загружаем серверы при переходе на вкладку
        if (section == LauncherSection.Servers && GameServers.Count == 0)
        {
            _ = LoadServersAsync();
        }
    }
    
    public async Task LoadNewsAsync()
    {
        try
        {
            StatusMessage = LocalizationManager.S("status_loading_news");
            var news = await _newsService.GetNewsAsync();
            
            _dispatcher.Invoke(() =>
            {
                News.Clear();
                foreach (var item in news)
                {
                    News.Add(item);
                }
            });
            
            StatusMessage = news.Count > 0 ? LocalizationManager.F("status_loaded_news", news.Count) : LocalizationManager.S("status_no_news");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[NEWS] Error: {ex.Message}");
            StatusMessage = LocalizationManager.S("status_news_error");
        }
    }
    
    public async Task LoadServersAsync()
    {
        try
        {
            StatusMessage = LocalizationManager.S("status_loading_servers");
            var servers = await _serverMonitorService.GetServersAsync();
            
            _dispatcher.Invoke(() =>
            {
                GameServers.Clear();
                foreach (var server in servers)
                {
                    GameServers.Add(server);
                }
            });
            
            var totalPlayers = servers.Sum(s => s.PlayerCount);
            StatusMessage = servers.Count > 0 
                ? LocalizationManager.F("status_loaded_servers", servers.Count, totalPlayers) 
                : LocalizationManager.S("status_no_servers");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[MONITOR] Error: {ex.Message}");
            StatusMessage = LocalizationManager.S("status_servers_error");
        }
    }
    
    /// <summary>
    /// Уведомляет UI о том, что список доступных серверов изменился
    /// (например, после фонового опроса при старте).
    /// </summary>
    public Task NotifyAvailableServersChangedAsync()
    {
        return _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(AvailableServers));
            OnPropertyChanged(nameof(SelectedServer));
        }).Task;
    }

    /// <summary>
    /// Публичная точка входа для запроса переключения сервера из внешнего кода
    /// (например, фонового опроса при старте). Переключение пропускается,
    /// если идёт скачивание или уже выбран этот же сервер.
    /// </summary>
    public async Task RequestServerSwitchAsync(ServerInfo newServer)
    {
        if (newServer == null || newServer == App.CurrentServer)
            return;
        if (IsDownloading)
            return;

        await SwitchServerAsync(newServer);

        // После автопереключения сразу перезагружаем модели, чтобы UI не остался пустым
        try
        {
            await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[AUTO-SWITCH] RefreshModelsAsync failed: {ex.Message}");
        }
    }

    private async Task SwitchServerAsync(ServerInfo newServer)
    {
        if (IsDownloading)
        {
            MessageBox.Show("Cannot switch server while downloading!", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        
        IsLoading = true;
        StatusMessage = $"Switching to {newServer.Name}...";
        _dispatcher.Invoke(() =>
        {
            Models.Clear();
        });
        
        try
        {
            await Task.Run(async () =>
            {
                var app = (App)Application.Current;
                await app.SwitchServerAsync(newServer);
            });
            
            var app = (App)Application.Current;
            _updateManager.ProgressChanged -= OnProgressChanged;
            _updateManager = app.GetService<UpdateManager>();
            _updateManager.ProgressChanged += OnProgressChanged;
            
            OnPropertyChanged(nameof(SelectedServer));
            OnPropertyChanged(nameof(ServerUrl));
            
            StatusMessage = $"Switched to {newServer.Name}. Click Refresh to load models.";
            FileLogger.Log($"Switched to server: {newServer.Url}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("Failed to switch server", ex);
            StatusMessage = $"Failed to switch: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RefreshModelsAsync()
    {
        FileLogger.Log("RefreshModelsAsync called");
        IsLoading = true;
        StatusMessage = "Connecting to server...";

        try
        {
            // Сначала получаем информацию о игре
            var gameInfo = await Task.Run(() => _updateManager.GetGameInfoAsync());
            
            _dispatcher.Invoke(() =>
            {
                Models.Clear();
                SetMainModel(null);
            });
            
            // Если есть Game на сервере - добавляем как MainModel
            if (gameInfo != null)
            {
                var check = await Task.Run(() => _updateManager.CheckForUpdateAsync(gameInfo.Id));
                
                var gameModel = new ModelItemViewModel
                {
                    Id = gameInfo.Id,
                    Name = gameInfo.Name,
                    InstalledVersion = check.InstalledVersion,
                    LatestVersion = gameInfo.Version,
                    UpdateAvailable = check.UpdateAvailable,
                    TotalSize = gameInfo.TotalSize,
                    FileCount = gameInfo.FileCount,
                    SizeFormatted = gameInfo.TotalSizeFormatted
                };
                
                _dispatcher.Invoke(() => SetMainModel(gameModel));
            }
            else if (MainModel == null)
            {
                // Если сервер пока не ответил по игре, но локальное состояние ещё не было создано,
                // подстрахуемся и инициализируем его сейчас.
                _dispatcher.Invoke(InitializeLocalGameState);
            }
            
            // Получаем аддоны (моды) вместо models
            var serverAddons = await Task.Run(() => _updateManager.GetAddonsAsync());

            if (gameInfo == null && serverAddons.Count == 0 && await TryEnableOfflineModeIfAllServersUnavailableAsync())
            {
                return;
            }

            foreach (var addon in serverAddons)
            {
                // Проверяем установлен ли аддон локально
                var addonCheck = _updateManager.CheckAddonInstallation(addon.Id, addon.Version);
                
                _dispatcher.Invoke(() => Models.Add(new ModelItemViewModel
                {
                    Id = addon.Id,                    // FolderName для скачивания
                    ModId = addon.ModId,              // Уникальный ID мода
                    Name = addon.Name,
                    InstalledVersion = addonCheck.InstalledVersion,
                    LatestVersion = addon.Version,
                    UpdateAvailable = addonCheck.UpdateAvailable,
                    TotalSize = addon.TotalSize,
                    FileCount = addon.FileCount,
                    SizeFormatted = addon.TotalSizeFormatted,
                    Changelog = addon.Changelog
                }));
            }

            SetServersAvailabilityState(allServersUnavailable: false, noInternetConnection: false);

            var gameStatus = MainModel != null ? $"Game: {MainModel.Name}" : "No game";
            StatusMessage = $"{gameStatus} | {Models.Count} addons";

            // Обновляем статистику размеров
            UpdateCatalogStats();

            // Если в разделе модов выбран серверный фильтр, пересчитываем его сразу
            // после загрузки каталога аддонов, чтобы стали видны доступные к скачиванию моды.
            UpdateServerModsStatus();
        }
        catch (Exception ex)
        {
            if (await TryEnableOfflineModeIfAllServersUnavailableAsync())
            {
                return;
            }

            FileLogger.Error("RefreshModelsAsync failed", ex);
            StatusMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task InstallAsync(ModelItemViewModel? model)
    {
        if (model?.LatestVersion == null || IsLoading) return;

        // Проверяем, не запущена ли уже массовая операция (если _cts уже создан)
        var isBatchOperation = _cts != null && !_cts.IsCancellationRequested;

        // Если это не часть массовой операции, проверяем IsDownloading
        if (!isBatchOperation && IsDownloading) return;

        FileLogger.Log($"InstallAsync called for {model.Id} v{model.LatestVersion}");

        // Создаём CTS только если это одиночная операция
        var ownsToken = false;
        if (_cts == null)
        {
            _cts = new CancellationTokenSource();
            ownsToken = true;
        }

        var token = _cts.Token;

        // Проверяем отмену сразу
        if (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }

        IsDownloading = true;
        DownloadPercent = 0;
        DownloadSpeed = "";
        DownloadedSize = "";
        TotalSize = "";
        Eta = "";
        CurrentFileName = "";
        FileProgress = "";

        var modelId = model.Id;
        var version = model.LatestVersion;
        var isAddon = _updateManager.IsAddon(modelId);

        try
        {
            StatusMessage = $"Starting download: {model.DisplayName}...";

            InstallResult result;
            if (isAddon)
            {
                // Используем методы для аддонов
                FileLogger.Log($"[INSTALL] Installing as ADDON: {modelId}");
                result = await Task.Run(async () => 
                    await _updateManager.InstallAddonAsync(modelId, version, token), 
                    token);
            }
            else
            {
                // Используем методы для игры/моделей
                FileLogger.Log($"[INSTALL] Installing as MODEL: {modelId}");
                result = await Task.Run(async () => 
                    await _updateManager.InstallOrUpdateAsync(modelId, version, true, token), 
                    token);
            }

            if (result.Success)
            {
                FileLogger.Log($"Install successful: {modelId}");
                _dispatcher.Invoke(() =>
                {
                    model.InstalledVersion = result.Version;
                    model.UpdateAvailable = false;
                    UpdateCatalogStats();
                });
                StatusMessage = $"Successfully installed {model.DisplayName} v{result.Version}";
            }
            else
            {
                FileLogger.Log($"Install failed: {result.Error}");
                StatusMessage = $"Failed: {result.Error}";
                _dispatcher.Invoke(() =>
                {
                    UC.NotificationDialog.Show(
                        Application.Current.MainWindow,
                        LocalizationManager.S("error_install_title"),
                        LocalizationManager.F("error_install_msg", model.DisplayName, result.Error ?? "Unknown error"),
                        UC.NotificationDialogType.Error);
                });
            }
        }
        catch (OperationCanceledException)
        {
            FileLogger.Log("Install cancelled by user");
            StatusMessage = "Download cancelled";
            // Пробрасываем исключение только если это часть массовой операции (наш токен не наш).
            // Для одиночной установки гасим, иначе async void обработчики UI крашат приложение.
            if (!ownsToken)
                throw;
        }
        catch (IOException ioEx) when (IsDiskFullException(ioEx))
        {
            FileLogger.Error("Install failed - disk full", ioEx);
            StatusMessage = LocalizationManager.S("status_disk_error");

            // Отменяем всю очередь загрузки
            _cts?.Cancel();

            // Показываем диалог нехватки места
            var openSettings = _dispatcher.Invoke(() =>
            {
                var installPath = _updateManager.IsAddon(modelId) 
                    ? _updateManager.ModsInstallRoot 
                    : _updateManager.GameInstallRoot;
                return UC.DiskSpaceDialog.Show(
                    Application.Current.MainWindow,
                    model.DisplayName,
                    installPath,
                    model.TotalSize);
            });

            // Если пользователь выбрал открыть настройки
            if (openSettings)
            {
                _dispatcher.Invoke(() => SetActiveSection(LauncherSection.Settings));
            }

            throw; // Пробрасываем для остановки массовой операции
        }
        catch (Exception ex)
        {
            // Проверяем внутренние исключения на нехватку места
            if (IsDiskFullException(ex))
            {
                FileLogger.Error("Install failed - disk full (inner)", ex);
                StatusMessage = LocalizationManager.S("status_disk_error");

                _cts?.Cancel();

                var openSettings = _dispatcher.Invoke(() =>
                {
                    var installPath = _updateManager.IsAddon(modelId) 
                        ? _updateManager.ModsInstallRoot 
                        : _updateManager.GameInstallRoot;
                    return UC.DiskSpaceDialog.Show(
                        Application.Current.MainWindow,
                        model.DisplayName,
                        installPath,
                        model.TotalSize);
                });

                if (openSettings)
                {
                    _dispatcher.Invoke(() => SetActiveSection(LauncherSection.Settings));
                }

                throw; // Пробрасываем для остановки массовой операции
            }
            else
            {
                FileLogger.Error("InstallAsync exception", ex);
                StatusMessage = $"Error: {ex.Message}";
                _dispatcher.Invoke(() =>
                {
                    UC.NotificationDialog.Show(
                        Application.Current.MainWindow,
                        LocalizationManager.S("error_install_title"),
                        LocalizationManager.F("error_install_msg", model.DisplayName, ex.Message),
                        UC.NotificationDialogType.Error);
                });
            }
        }
        finally
        {
            IsDownloading = false;
            DownloadPercent = 0;
            DownloadSpeed = "";

            // Освобождаем CTS только если мы его создали (одиночная операция)
            if (ownsToken)
            {
                _cts?.Dispose();
                _cts = null;
            }
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
        }
    }

    public void CancelDownload()
    {
        if (!IsDownloading) return;
        FileLogger.Log("CancelDownload called");
        _cts?.Cancel();
        _updateManager.Cancel();
        StatusMessage = "Cancelling...";
    }

    public async Task<VerifyResult?> VerifyAsync(ModelItemViewModel? model)
    {
        if (model == null) return null;


        FileLogger.Log($"VerifyAsync called for {model.Id}");
        StatusMessage = $"Verifying {model.DisplayName}...";

        var isAddon = _updateManager.IsAddon(model.Id);
        VerifyResult result;

        if (isAddon)
        {
            result = await Task.Run(() => _updateManager.VerifyAddonAsync(model.Id));
        }
        else
        {
            result = await Task.Run(() => _updateManager.VerifyInstallationAsync(model.Id));
        }

        StatusMessage = result.Success 
            ? $"{model.DisplayName} verification passed"
            : $"Verification failed: {result.Error ?? $"{result.InvalidFiles?.Count ?? 0} files invalid"}";


        FileLogger.Log($"Verify result: {StatusMessage}");
        return result;
    }

    /// <summary>
    /// Устанавливает все аддоны последовательно, пропуская уже установленные и актуальные
    /// </summary>
    public async Task InstallAllAddonsAsync()
    {
        if (IsDownloading || IsLoading || Models.Count == 0) return;

        FileLogger.Log($"InstallAllAddonsAsync called for {Models.Count} addons");

        var addonsToProcess = Models.Where(m => _updateManager.IsAddon(m.Id)).ToList();
        if (addonsToProcess.Count == 0)
        {
            StatusMessage = LocalizationManager.S("status_no_addons");
            return;
        }

        // Создаём CTS для всей операции
        _cts = new CancellationTokenSource();
        var cancellationToken = _cts.Token;

        var total = addonsToProcess.Count;
        var current = 0;
        var installed = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = false;

        // Инициализируем прогресс модов
        ModsInstallTotal = total;
        ModsInstallCurrent = 0;
        ModsInstallProgress = $"0 / {total}";
        ModsInstallCurrentName = "";

        FileLogger.Log($"[INSTALL-ALL] Checking {total} addons...");

        foreach (var addon in addonsToProcess)
        {
            // Проверяем отмену в начале каждой итерации
            if (cancellationToken.IsCancellationRequested)
            {
                FileLogger.Log($"[INSTALL-ALL] Cancelled by user at {current}/{total}");
                cancelled = true;
                break;
            }

            current++;

            // Проверяем установлен ли аддон и актуальна ли версия
            var check = _updateManager.CheckAddonInstallation(addon.Id, addon.LatestVersion ?? "");

            if (check.IsInstalled && !check.UpdateAvailable)
            {
                // Аддон уже установлен и актуален - пропускаем
                FileLogger.Log($"[INSTALL-ALL] [{current}/{total}] SKIP: {addon.Id} v{check.InstalledVersion} - already up to date");
                skipped++;
                StatusMessage = LocalizationManager.F("status_addon_current", current, total, addon.DisplayName);
                continue;
            }

            // Дополнительно проверяем целостность файлов если версия совпадает
            if (check.IsInstalled && check.InstalledVersion == addon.LatestVersion)
            {
                StatusMessage = LocalizationManager.F("status_check_addon", current, total, addon.DisplayName);
                var verifyResult = await Task.Run(() => _updateManager.VerifyAddonAsync(addon.Id), cancellationToken);

                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

                if (verifyResult.Success)
                {
                    FileLogger.Log($"[INSTALL-ALL] [{current}/{total}] SKIP: {addon.Id} - verified OK");
                    skipped++;

                    // Обновляем UI
                    _dispatcher.Invoke(() =>
                    {
                        addon.InstalledVersion = check.InstalledVersion;
                        addon.UpdateAvailable = false;
                    });
                    continue;
                }
                else
                {
                    FileLogger.Log($"[INSTALL-ALL] [{current}/{total}] REPAIR: {addon.Id} - {verifyResult.InvalidFiles?.Count ?? 0} invalid files");
                }
            }

            // Нужна установка или обновление
            var action = check.IsInstalled ? LocalizationManager.S("action_update") : LocalizationManager.S("action_install");
            StatusMessage = $"{action} {current}/{total}: {addon.DisplayName}...";

            // Обновляем прогресс модов
            ModsInstallCurrent = current;
            ModsInstallProgress = $"{current} / {total}";
            ModsInstallCurrentName = addon.DisplayName ?? addon.Id;

            FileLogger.Log($"[INSTALL-ALL] [{current}/{total}] INSTALL: {addon.Id} (installed={check.InstalledVersion ?? "none"}, latest={addon.LatestVersion})");

            try
            {
                await InstallAsync(addon);

                // Проверяем отмену после установки
                if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }

                installed++;
            }
            catch (OperationCanceledException)
            {
                FileLogger.Log($"[INSTALL-ALL] Installation cancelled at {addon.Id}");
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // Проверяем не была ли это скрытая отмена (нехватка места тоже отменяет)
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                FileLogger.Error($"[INSTALL-ALL] Failed to install {addon.Id}", ex);
                failed++;
            }

            // Небольшая пауза между установками
            await Task.Delay(200, CancellationToken.None);
        }

        // Формируем финальный статус
        var parts = new List<string>();
        if (installed > 0) parts.Add(LocalizationManager.F("status_install_count", installed));
        if (skipped > 0) parts.Add(LocalizationManager.F("status_skip_count", skipped));
        if (failed > 0) parts.Add(LocalizationManager.F("status_fail_count", failed));

        if (cancelled)
        {
            StatusMessage = LocalizationManager.F("status_cancelled_processed", current, total, string.Join(", ", parts));
            FileLogger.Log($"[INSTALL-ALL] Cancelled: installed={installed}, skipped={skipped}, failed={failed}");
        }
        else
        {
            StatusMessage = LocalizationManager.F("status_processed", total, string.Join(", ", parts));
            FileLogger.Log($"[INSTALL-ALL] Completed: installed={installed}, skipped={skipped}, failed={failed}");
        }

        // Сбрасываем прогресс модов
        ModsInstallProgress = "";
        ModsInstallCurrentName = "";
        ModsInstallCurrent = 0;
        ModsInstallTotal = 0;

        // Очищаем CTS после завершения массовой операции
        _cts?.Dispose();
        _cts = null;
        IsDownloading = false;
    }

    /// <summary>
    /// Проверяет все аддоны
    /// </summary>
    public async Task VerifyAllAddonsAsync()
    {
        if (IsDownloading || IsLoading || Models.Count == 0) return;

        FileLogger.Log($"VerifyAllAddonsAsync called for {Models.Count} addons");

        var addonsToVerify = Models.Where(m => _updateManager.IsAddon(m.Id)).ToList();
        if (addonsToVerify.Count == 0)
        {
            StatusMessage = LocalizationManager.S("status_no_addons_verify");
            return;
        }

        var total = addonsToVerify.Count;
        var current = 0;

        var results = new List<UC.ModVerifyResultItem>();

        foreach (var addon in addonsToVerify)
        {
            current++;
            StatusMessage = LocalizationManager.F("status_check_addon", current, total, addon.DisplayName);

            var resultItem = new UC.ModVerifyResultItem
            {
                FolderId = addon.Id,
                Name = addon.DisplayName,
                Version = addon.InstalledVersion
            };

            // Сначала проверяем установлен ли вообще
            var check = _updateManager.CheckAddonInstallation(addon.Id, addon.LatestVersion ?? "");

            if (!check.IsInstalled)
            {
                resultItem.Status = UC.ModVerifyStatus.NotInstalled;
                FileLogger.Log($"[VERIFY-ALL] {addon.Id}: Not installed");

                // Обновляем UI
                _dispatcher.Invoke(() =>
                {
                    addon.InstalledVersion = null;
                    addon.UpdateAvailable = true;
                });
            }
            else
            {
                resultItem.Version = check.InstalledVersion;

                // Проверяем целостность
                var result = await Task.Run(() => _updateManager.VerifyAddonAsync(addon.Id));

                if (result.Success)
                {
                    resultItem.Status = UC.ModVerifyStatus.Valid;
                    FileLogger.Log($"[VERIFY-ALL] {addon.Id}: OK (v{check.InstalledVersion})");

                    // Обновляем UI
                    _dispatcher.Invoke(() =>
                    {
                        addon.InstalledVersion = check.InstalledVersion;
                        addon.UpdateAvailable = check.UpdateAvailable;
                    });
                }
                else
                {
                    resultItem.Status = UC.ModVerifyStatus.Invalid;
                    resultItem.InvalidFiles = (result.InvalidFileDetails ?? [])
                        .Select(f => new UC.InvalidFileDisplayItem
                        {
                            FileName = f.Path,
                            IsMissing = f.IsMissing
                        })
                        .ToList();
                    FileLogger.Log($"[VERIFY-ALL] {addon.Id}: Invalid ({result.InvalidFiles?.Count ?? 0} files)");

                    // Помечаем как требующий обновления
                    _dispatcher.Invoke(() =>
                    {
                        addon.UpdateAvailable = true;
                    });
                }
            }

            results.Add(resultItem);
        }

        int valid = results.Count(r => r.Status == UC.ModVerifyStatus.Valid);
        int invalid = results.Count(r => r.Status == UC.ModVerifyStatus.Invalid);
        int notInstalled = results.Count(r => r.Status == UC.ModVerifyStatus.NotInstalled);

        var parts = new List<string>();
        if (valid > 0) parts.Add($"✓ {valid} OK");
        if (notInstalled > 0) parts.Add(LocalizationManager.F("status_not_installed_count", notInstalled));
        if (invalid > 0) parts.Add(LocalizationManager.F("status_invalid_count", invalid));

        StatusMessage = LocalizationManager.F("status_verified", total, string.Join(", ", parts));
        FileLogger.Log($"[VERIFY-ALL] Completed: {valid} OK, {notInstalled} not installed, {invalid} invalid");

        // Показываем диалог с результатами
        var modsToRepair = _dispatcher.Invoke(() => 
            UC.ModsVerifyResultDialog.Show(Application.Current.MainWindow, results));

        if (modsToRepair != null && modsToRepair.Count > 0)
        {
            StatusMessage = LocalizationManager.F("status_repairing", modsToRepair.Count);

            foreach (var folderId in modsToRepair)
            {
                var model = Models.FirstOrDefault(m => m.Id == folderId);
                if (model != null)
                {
                    await InstallAsync(model);
                }
            }
        }
    }

    /// <summary>
    /// Проверяет все установленные моды из текущего фильтра по серверу
    /// </summary>
    public async Task VerifyAllServerModsAsync()
    {
        if (IsDownloading || _selectedModsServer == null) return;

        var modsToVerify = ServerModsStatus.ToList();

        if (modsToVerify.Count == 0)
        {
            StatusMessage = LocalizationManager.S("status_no_mods_verify");
            return;
        }

        FileLogger.Log($"[VERIFY-SERVER] Starting verification of {modsToVerify.Count} mods for server {_selectedModsServer.Name}");
        StatusMessage = LocalizationManager.F("status_verify_mods", modsToVerify.Count);

        var results = new List<UC.ModVerifyResultItem>();

        foreach (var modStatus in modsToVerify)
        {
            StatusMessage = LocalizationManager.F("status_verify_mod", modStatus.Name);

            var resultItem = new UC.ModVerifyResultItem
            {
                FolderId = modStatus.FolderId ?? "",
                Name = modStatus.Name,
                Version = modStatus.LocalVersion
            };

            if (!modStatus.IsInstalled || modStatus.FolderId == null)
            {
                resultItem.Status = UC.ModVerifyStatus.NotInstalled;
                FileLogger.Log($"[VERIFY-SERVER] {modStatus.Name}: Not installed");
            }
            else
            {
                var result = await Task.Run(() => _updateManager.VerifyAddonAsync(modStatus.FolderId));

                if (result.Success)
                {
                    resultItem.Status = UC.ModVerifyStatus.Valid;
                    FileLogger.Log($"[VERIFY-SERVER] {modStatus.Name}: OK");
                }
                else
                {
                    resultItem.Status = UC.ModVerifyStatus.Invalid;
                    resultItem.InvalidFiles = (result.InvalidFileDetails ?? [])
                        .Select(f => new UC.InvalidFileDisplayItem
                        {
                            FileName = f.Path,
                            IsMissing = f.IsMissing
                        })
                        .ToList();
                    FileLogger.Log($"[VERIFY-SERVER] {modStatus.Name}: Invalid ({result.InvalidFiles?.Count ?? 0} files)");
                }
            }

            results.Add(resultItem);
        }

        int valid = results.Count(r => r.Status == UC.ModVerifyStatus.Valid);
        int invalid = results.Count(r => r.Status == UC.ModVerifyStatus.Invalid);

        StatusMessage = invalid == 0 
            ? LocalizationManager.F("status_verify_all_ok", valid) 
            : LocalizationManager.F("status_verify_result", valid, invalid);

        FileLogger.Log($"[VERIFY-SERVER] Completed: {valid} OK, {invalid} invalid");

        // Показываем диалог с результатами
        var modsToRepair = _dispatcher.Invoke(() => 
            UC.ModsVerifyResultDialog.Show(Application.Current.MainWindow, results));

        if (modsToRepair != null && modsToRepair.Count > 0)
        {
            StatusMessage = LocalizationManager.F("status_repairing", modsToRepair.Count);

            foreach (var folderId in modsToRepair)
            {
                var model = Models.FirstOrDefault(m => m.Id == folderId);
                if (model != null)
                {
                    await InstallAsync(model);
                }
            }

            // Обновляем статусы после восстановления
            SelectedModsServer = SelectedModsServer;
        }
    }

    public async Task RunSpeedTestAsync()
    {
        if (IsSpeedTesting || IsDownloading) return;
        
        IsSpeedTesting = true;
        SpeedTestResult = "Testing...";
        
        try
        {
            var app = (App)Application.Current;
            var factory = app.GetService<IHttpClientFactory>();
            var httpClient = factory.CreateClient("LauncherApi");
            var speedTest = new SpeedTestService(httpClient);
            
            var progress = new Progress<string>(msg => SpeedTestResult = msg);
            
            var result = await Task.Run(async () => 
                await speedTest.RunSpeedTestAsync(_updateManager.ServerUrl, progress));
            
            SpeedTestResult = result.Summary;
            StatusMessage = $"Speed Test: {result.Summary}";
            
            FileLogger.Log($"SpeedTest result: {result.Summary}");
        }
        catch (Exception ex)
        {
            SpeedTestResult = $"Error: {ex.Message}";
            FileLogger.Error("SpeedTest failed", ex);
        }
        finally
        {
            IsSpeedTesting = false;
        }
    }

    public async Task ExecuteGamePrimaryActionAsync()
    {
        try
        {
            var action = EvaluateGameAction();
            switch (action)
            {
                case GameAction.Install:
                case GameAction.Update:
                    if (MainModel != null)
                        await InstallAsync(MainModel);
                    break;
                case GameAction.Play:
                    await LaunchGameAsync();
                    break;
                default:
                    await RefreshModelsAsync();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена загрузки — нормальный сценарий, не крашим приложение
            FileLogger.Log("[GAME-ACTION] Operation cancelled by user");
            StatusMessage = "Download cancelled";
        }
    }

    public Task LaunchGameAsync()
    {
        if (MainModel == null || MainModel.InstalledVersion == null)
            return Task.CompletedTask;
        try
        {
            var installDir = _updateManager.GetInstallPath(MainModel.Id);
            var exePath = Path.Combine(installDir, "ArmaReforgerSteam.exe");
            if (!File.Exists(exePath))
            {
                StatusMessage = LocalizationManager.S("status_game_exe_missing");
                return Task.CompletedTask;
            }

            if (!_updateManager.EnsureCurrentGameHashFile(installDir))
            {
                FileLogger.Log("[LAUNCH] Cancelled: failed to prepare .hashe before process start");
                StatusMessage = "Не удалось подготовить .hashe файл перед запуском игры";
                return Task.CompletedTask;
            }

            ProcessStartInfo startInfo;

            // Добавляем параметры запуска если включено
            if (UseCustomLaunchParams)
            {
                var modsPath = _updateManager.ModsInstallRoot;
                var tempAddonsDir = Path.Combine(Path.GetTempPath(), "ArmaLauncher", "TempDir");
                Directory.CreateDirectory(tempAddonsDir);
                var arguments = $"-addonsDir \"{modsPath}\" -addonDownloadDir \"{tempAddonsDir}\"";

                FileLogger.Log($"[LAUNCH] Exe: {exePath}");
                FileLogger.Log($"[LAUNCH] Arguments: {arguments}");
                FileLogger.Log($"[LAUNCH] WorkingDir: {installDir}");

                // Используем UseShellExecute = false для гарантированной передачи аргументов
                startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = installDir,
                    UseShellExecute = false
                };
            }
            else
            {
                // Без параметров можно использовать ShellExecute
                startInfo = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = installDir
                };
                FileLogger.Log($"[LAUNCH] Starting without params: {exePath}");
            }

            var process = Process.Start(startInfo);

            if (process != null)
            {
                TrackGameProcess(process);
                FileLogger.Log($"[LAUNCH] Process started: PID={process.Id}");
                StatusMessage = UseCustomLaunchParams 
                    ? LocalizationManager.F("status_game_launched_params", process.Id) 
                    : LocalizationManager.S("status_game_launched");
            }
            else
            {
                FileLogger.Log("[LAUNCH] WARNING: Process.Start returned null");
                StatusMessage = LocalizationManager.S("status_game_starting");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Error("LaunchGame failed", ex);
            StatusMessage = LocalizationManager.F("status_game_launch_error", ex.Message);

            _dispatcher.Invoke(() =>
            {
                UC.NotificationDialog.Show(
                    Application.Current.MainWindow,
                    LocalizationManager.S("error_launch_title"),
                    LocalizationManager.F("error_launch_msg", ex.Message),
                    UC.NotificationDialogType.Error);
            });
        }

        return Task.CompletedTask;
    }

    public void OpenGameFolder()
    {
        var path = MainModel != null ? _updateManager.GetInstallPath(MainModel.Id) : _updateManager.InstallRoot;
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileLogger.Error("OpenGameFolder failed", ex);
            StatusMessage = LocalizationManager.F("status_folder_error", ex.Message);
        }
    }

    private void SetMainModel(ModelItemViewModel? model)
    {
        if (ReferenceEquals(_mainModel, model))
            return;

        if (_mainModel != null)
            _mainModel.PropertyChanged -= OnMainModelPropertyChanged;

        _mainModel = model;

        if (_mainModel != null)
            _mainModel.PropertyChanged += OnMainModelPropertyChanged;

        OnPropertyChanged(nameof(MainModel));
        UpdateGameBindings();
    }

    private void OnMainModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModelItemViewModel.InstalledVersion) or nameof(ModelItemViewModel.UpdateAvailable))
        {
            UpdateGameBindings();
        }
    }

    private void UpdateGameBindings()
    {
        OnPropertyChanged(nameof(GamePrimaryActionLabel));
        OnPropertyChanged(nameof(GameStatusText));
        OnPropertyChanged(nameof(GameInstallPath));
        OnPropertyChanged(nameof(CanExecuteGameAction));
        OnPropertyChanged(nameof(IsGameRunning));
        OnPropertyChanged(nameof(IsPlayAction));
        OnPropertyChanged(nameof(HasMainModel));
    }

    private void TrackGameProcess(Process process)
    {
        try
        {
            if (_trackedGameProcess != null)
                _trackedGameProcess.Exited -= OnTrackedGameProcessExited;

            process.EnableRaisingEvents = true;
            process.Exited += OnTrackedGameProcessExited;
            _trackedGameProcess = process;
            RefreshGameRunningState();
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[LAUNCH] Failed to track game process: {ex.Message}");
        }
    }

    private void OnTrackedGameProcessExited(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(RefreshGameRunningState);
    }

    private void RefreshGameRunningState()
    {
        UpdateGameBindings();
    }

    private bool CheckIsGameRunning()
    {
        if (IsTrackedProcessAlive())
            return true;

        var installDir = _updateManager.GameInstallRoot;
        var expectedExePath = Path.Combine(installDir, "ArmaReforgerSteam.exe");
        if (!File.Exists(expectedExePath))
            return false;

        try
        {
            foreach (var process in Process.GetProcessesByName("ArmaReforgerSteam"))
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, expectedExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ReferenceEquals(_trackedGameProcess, process))
                            TrackGameProcess(process);

                        return true;
                    }
                }
                catch
                {
                    if (!process.HasExited)
                        return true;
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[LAUNCH] Failed to inspect running game processes: {ex.Message}");
        }

        return false;
    }

    private bool IsTrackedProcessAlive()
    {
        if (_trackedGameProcess == null)
            return false;

        try
        {
            if (_trackedGameProcess.HasExited)
            {
                _trackedGameProcess.Exited -= OnTrackedGameProcessExited;
                _trackedGameProcess.Dispose();
                _trackedGameProcess = null;
                return false;
            }

            return true;
        }
        catch
        {
            _trackedGameProcess = null;
            return false;
        }
    }

    private async Task<bool> TryEnableOfflineModeIfAllServersUnavailableAsync()
    {
        var anyServerAvailable = await CheckAnyServerWithGameAvailableAsync();
        if (anyServerAvailable)
        {
            SetServersAvailabilityState(allServersUnavailable: false, noInternetConnection: false);
            return false;
        }

        var noInternetConnection = !NetworkInterface.GetIsNetworkAvailable();
        SetServersAvailabilityState(allServersUnavailable: true, noInternetConnection: noInternetConnection);

        var installDir = _updateManager.GameInstallRoot;
        var exePath = Path.Combine(installDir, "ArmaReforgerSteam.exe");
        if (!File.Exists(exePath))
        {
            StatusMessage = noInternetConnection
                ? "Нет интернета, а локальная игра не найдена."
                : "Все серверы недоступны, а локальная игра не найдена.";
            return true;
        }

        var installedVersion = UpdateManager.GetGameVersionFromExe(installDir) ?? "installed";
        _dispatcher.Invoke(() =>
        {
            SetMainModel(new ModelItemViewModel
            {
                Id = "arma-reforger",
                Name = "Arma Reforger",
                InstalledVersion = installedVersion,
                LatestVersion = installedVersion,
                UpdateAvailable = false,
                TotalSize = 0,
                FileCount = 0,
                SizeFormatted = ""
            });
        });

        StatusMessage = noInternetConnection
            ? "Нет интернета. Можно запустить уже установленную игру."
            : "Все серверы недоступны. Можно запустить уже установленную игру.";

        FileLogger.Log($"[OFFLINE] Offline launch mode enabled. NoInternet={noInternetConnection}");
        return true;
    }

    private void SetServersAvailabilityState(bool allServersUnavailable, bool noInternetConnection)
    {
        if (_allServersUnavailable == allServersUnavailable && _noInternetConnection == noInternetConnection)
            return;

        _allServersUnavailable = allServersUnavailable;
        _noInternetConnection = noInternetConnection;
        OnPropertyChanged(nameof(GameStatusText));
        UpdateGameBindings();
    }

    private async Task<bool> CheckAnyServerWithGameAvailableAsync()
    {
        using var probeClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        var checks = App.AvailableServers.Select(async server =>
        {
            try
            {
                using var response = await probeClient.GetAsync($"{server.Url}/info");
                if (!response.IsSuccessStatusCode)
                {
                    server.HasGame = false;
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var hasGame = json.Contains("\"game\"") && !json.Contains("\"game\":null");
                server.HasGame = hasGame;
                return hasGame;
            }
            catch
            {
                server.HasGame = false;
                return false;
            }
        });

        var results = await Task.WhenAll(checks);
        OnPropertyChanged(nameof(AvailableServers));
        return results.Any(static result => result);
    }

    private GameAction EvaluateGameAction()
    {
        if (MainModel == null)
            return GameAction.None;
        if (MainModel.InstalledVersion == null)
            return GameAction.Install;
        if (MainModel.UpdateAvailable)
            return GameAction.Update;
        return GameAction.Play;
    }

    private enum GameAction
    {
        None,
        Install,
        Update,
        Play
    }

    private void OnProgressChanged(DownloadProgress progress)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            DownloadPercent = progress.PercentComplete;
            DownloadSpeed = progress.SpeedFormatted;
            DownloadedSize = progress.DownloadedFormatted;
            TotalSize = progress.TotalFormatted;
            RemainingSize = progress.RemainingFormatted;
            Eta = progress.EtaFormatted;
            CurrentFileName = progress.CurrentFile ?? "";
            FileProgress = progress.FileProgressText;

            if (progress.State == DownloadState.Downloading)
            {
                StatusMessage = $"{progress.PercentComplete:F1}% | {progress.SpeedFormatted} | ETA: {progress.EtaFormatted} | {progress.FileProgressText}";
            }
            else if (progress.State == DownloadState.Assembling)
            {
                StatusMessage = "Finalizing installation...";
                DownloadPercent = 100;
            }
            else if (progress.State == DownloadState.Completed)
            {
                DownloadPercent = 100;
            }
            else if (progress.State == DownloadState.Failed)
            {
                StatusMessage = $"Failed: {progress.CurrentFile}";
            }
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (_dispatcher.CheckAccess())
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        else
            _dispatcher.BeginInvoke(DispatcherPriority.Background, 
                () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }

    /// <summary>
    /// Проверяет, является ли исключение ошибкой нехватки места на диске
    /// </summary>
    private static bool IsDiskFullException(Exception ex)
    {
        // Проверяем текущее исключение
        if (ex is IOException ioEx)
        {
            // ERROR_DISK_FULL = 112, ERROR_HANDLE_DISK_FULL = 39
            const int ERROR_DISK_FULL = unchecked((int)0x80070070);
            const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);

            if (ioEx.HResult == ERROR_DISK_FULL || ioEx.HResult == ERROR_HANDLE_DISK_FULL)
                return true;

            // Проверяем сообщение на разных языках
            var msg = ioEx.Message.ToLowerInvariant();
            if (msg.Contains("disk full") || msg.Contains("недостаточно места") || 
                msg.Contains("not enough space") || msg.Contains("no space left"))
                return true;
        }

        // Проверяем внутренние исключения
        if (ex.InnerException != null)
            return IsDiskFullException(ex.InnerException);

        // Проверяем AggregateException
        if (ex is AggregateException aggEx)
        {
            foreach (var inner in aggEx.InnerExceptions)
            {
                if (IsDiskFullException(inner))
                    return true;
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        FileLogger.Log("MainViewModel disposing");

        // Останавливаем слайдшоу
        StopSlideshow();

        // Безопасно отменяем автообновление серверов
        try
        {
            _autoRefreshCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _autoRefreshCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        // Безопасно отменяем текущую загрузку
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _cts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        _updateManager.ProgressChanged -= OnProgressChanged;
        if (_mainModel != null)
            _mainModel.PropertyChanged -= OnMainModelPropertyChanged;
        await _updateManager.DisposeAsync();
        _cache.Dispose();
    }
}

public sealed class ModelItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    
    private string? _installedVersion;
    public string? InstalledVersion
    {
        get => _installedVersion;
        set { _installedVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }
    
    public string? LatestVersion { get; set; }
    
    
    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set { _updateAvailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }
    
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public string SizeFormatted { get; set; } = "";
    
    /// <summary>
    /// Уникальный ID мода (из ServerData.json)
    /// </summary>
    public string ModId { get; set; } = "";
    
    /// <summary>
    /// Changelog мода
    /// </summary>
    public string? Changelog { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;
    
    public string StatusText => InstalledVersion != null 
        ? (UpdateAvailable ? LocalizationManager.F("status_update_version", LatestVersion) : $"v{InstalledVersion}")
        : $"v{LatestVersion}";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


public enum LauncherSection
{
    Game,
    News,
    Mods,
    Servers,
    Settings
}

/// <summary>
/// Статус мода сервера
/// </summary>
public enum ServerModStatus
{
    Installed,
    Missing,
    MissingCanDownload,
    VersionNewer,      // Локальная версия новее серверной
    VersionOlder       // Локальная версия старше серверной (нужно обновить)
}

/// <summary>
/// ViewModel для отображения статуса мода сервера
/// </summary>
public class ServerModStatusViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public string LocalVersion { get; set; } = "";
    public string? LatestVersion { get; set; }
    public bool IsInstalled { get; set; }
    public bool CanDownload { get; set; }
    public string? FolderId { get; set; }
    public ServerModStatus Status { get; set; } = ServerModStatus.Missing;
    public ModelItemViewModel? Model { get; set; }

    public string StatusIcon => Status switch
    {
        ServerModStatus.Installed => "✓",
        ServerModStatus.VersionNewer => "▲",
        ServerModStatus.VersionOlder => "▼",
        ServerModStatus.MissingCanDownload => "📥",
        _ => "✕"
    };

    public string StatusText => Status switch
    {
        ServerModStatus.Installed => "OK",
        ServerModStatus.VersionNewer => LocalizationManager.S("mod_status_newer"),
        ServerModStatus.VersionOlder => LocalizationManager.S("mod_status_outdated"),
        ServerModStatus.MissingCanDownload => LocalizationManager.S("mod_status_download"),
        _ => LocalizationManager.S("mod_status_missing")
    };

    public string VersionCompareText
    {
        get
        {
            if (!IsInstalled) return "";
            return Status switch
            {
                ServerModStatus.VersionNewer => $"v{LocalVersion} > v{ServerVersion}",
                ServerModStatus.VersionOlder => $"v{LocalVersion} < v{ServerVersion}",
                _ => $"v{LocalVersion}"
            };
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
