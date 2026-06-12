using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class FirstSetupDialog : Window
{
    public string GamePath { get; private set; } = "";
    public string ModsPath { get; private set; } = "";
    public bool Confirmed { get; private set; } = false;
    
    private readonly string _defaultGamePath;
    private readonly string _defaultModsPath;
    
    public FirstSetupDialog(string defaultGamePath, string defaultModsPath)
    {
        InitializeComponent();
        
        _defaultGamePath = defaultGamePath;
        _defaultModsPath = defaultModsPath;
        
        GamePathTextBox.Text = defaultGamePath;
        ModsPathTextBox.Text = defaultModsPath;
        
        GamePath = defaultGamePath;
        ModsPath = defaultModsPath;
        
        UpdateDiskSpaceInfo();
    }
    
    /// <summary>
    /// Показывает диалог и возвращает результат
    /// </summary>
    public static (bool confirmed, string gamePath, string modsPath) Show(string defaultGamePath, string defaultModsPath)
    {
        var dialog = new FirstSetupDialog(defaultGamePath, defaultModsPath);
        dialog.ShowDialog();
        
        return (dialog.Confirmed, dialog.GamePath, dialog.ModsPath);
    }
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
    
    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.S("folder_game_title"),
            InitialDirectory = Path.GetDirectoryName(GamePathTextBox.Text) ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog() == true)
        {
            GamePathTextBox.Text = dialog.FolderName;
            
            // Если включена опция одной папки, обновляем путь к модам
            if (UseSameFolderCheckbox.IsChecked == true)
            {
                ModsPathTextBox.Text = Path.Combine(dialog.FolderName, "Addons");
            }
            
            UpdateDiskSpaceInfo();
        }
    }
    
    private void BrowseModsPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationManager.S("folder_mods_title"),
            InitialDirectory = Path.GetDirectoryName(ModsPathTextBox.Text) ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        
        if (dialog.ShowDialog() == true)
        {
            ModsPathTextBox.Text = dialog.FolderName;
            UseSameFolderCheckbox.IsChecked = false;
            UpdateDiskSpaceInfo();
        }
    }
    
    private void UseSameFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (UseSameFolderCheckbox.IsChecked == true)
        {
            var gamePath = GamePathTextBox.Text;
            if (!string.IsNullOrEmpty(gamePath))
            {
                var parentDir = Path.GetDirectoryName(gamePath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    ModsPathTextBox.Text = Path.Combine(parentDir, "Addons");
                }
            }
        }
        
        UpdateDiskSpaceInfo();
    }
    
    private void UseDefault_Click(object sender, RoutedEventArgs e)
    {
        GamePathTextBox.Text = _defaultGamePath;
        ModsPathTextBox.Text = _defaultModsPath;
        UseSameFolderCheckbox.IsChecked = false;
        UpdateDiskSpaceInfo();
    }
    
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        GamePath = GamePathTextBox.Text;
        ModsPath = ModsPathTextBox.Text;
        Confirmed = true;
        
        // Создаём папки если не существуют
        try
        {
            if (!Directory.Exists(GamePath))
                Directory.CreateDirectory(GamePath);
            
            if (!Directory.Exists(ModsPath))
                Directory.CreateDirectory(ModsPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(LocalizationManager.F("error_create_folders", ex.Message), LocalizationManager.S("error_title"), 
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        
        Close();
    }
    
    private void UpdateDiskSpaceInfo()
    {
        var gamePath = GamePathTextBox.Text;
        var modsPath = ModsPathTextBox.Text;
        
        // Получаем информацию о дисках
        var gameSpace = GetDiskFreeSpace(gamePath);
        var modsSpace = GetDiskFreeSpace(modsPath);
        
        if (gameSpace.HasValue)
        {
            GameDiskSpaceText.Text = LocalizationManager.F("disk_free_space", FormatSize(gameSpace.Value));
            GameDiskSpaceText.Foreground = gameSpace.Value > 30L * 1024 * 1024 * 1024 
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xb9, 0x81))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0x9e, 0x0b));
        }
        else
        {
            GameDiskSpaceText.Text = "";
        }
        
        if (modsSpace.HasValue)
        {
            ModsDiskSpaceText.Text = $"Свободно: {FormatSize(modsSpace.Value)}";
            ModsDiskSpaceText.Foreground = modsSpace.Value > 50L * 1024 * 1024 * 1024
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xb9, 0x81))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0x9e, 0x0b));
        }
        else
        {
            ModsDiskSpaceText.Text = "";
        }
        
        // Проверяем достаточно ли места
        var gameDrive = GetDriveLetter(gamePath);
        var modsDrive = GetDriveLetter(modsPath);
        
        if (gameDrive == modsDrive && gameSpace.HasValue)
        {
            // Одинаковый диск - нужно минимум 80 ГБ (26 игра + ~50 моды + запас)
            if (gameSpace.Value < 80L * 1024 * 1024 * 1024)
            {
                ShowWarning($"На диске {gameDrive}: недостаточно места. Рекомендуется минимум 80 ГБ для игры и модов.");
            }
            else
            {
                HideWarning();
            }
            TotalSpaceText.Text = $"Диск {gameDrive}: {FormatSize(gameSpace.Value)} свободно";
        }
        else
        {
            // Разные диски
            bool hasWarning = false;
            
            if (gameSpace.HasValue && gameSpace.Value < 30L * 1024 * 1024 * 1024)
            {
                ShowWarning($"На диске для игры недостаточно места. Требуется минимум 30 ГБ.");
                hasWarning = true;
            }
            
            if (modsSpace.HasValue && modsSpace.Value < 50L * 1024 * 1024 * 1024 && !hasWarning)
            {
                ShowWarning($"На диске для модов рекомендуется иметь минимум 50 ГБ свободного места.");
                hasWarning = true;
            }
            
            if (!hasWarning)
            {
                HideWarning();
            }
            
            TotalSpaceText.Text = "";
        }
    }
    
    private void ShowWarning(string text)
    {
        WarningText.Text = text;
        WarningBorder.Visibility = Visibility.Visible;
    }
    
    private void HideWarning()
    {
        WarningBorder.Visibility = Visibility.Collapsed;
    }
    
    private static long? GetDiskFreeSpace(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;
            
            var driveInfo = new DriveInfo(root);
            if (driveInfo.IsReady)
            {
                return driveInfo.AvailableFreeSpace;
            }
        }
        catch
        {
            // Игнорируем ошибки
        }
        
        return null;
    }
    
    private static string? GetDriveLetter(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Path.GetPathRoot(path)?.TrimEnd('\\');
        }
        catch
        {
            return null;
        }
    }
    
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
