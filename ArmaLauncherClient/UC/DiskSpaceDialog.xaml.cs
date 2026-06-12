using System.IO;
using System.Windows;
using System.Windows.Input;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class DiskSpaceDialog : Window
{
    public bool OpenSettings { get; private set; } = false;
    
    public DiskSpaceDialog()
    {
        InitializeComponent();
    }
    
    /// <summary>
    /// Показывает диалог о нехватке места
    /// </summary>
    /// <param name="owner">Родительское окно</param>
    /// <param name="modName">Название мода который не удалось установить</param>
    /// <param name="installPath">Путь установки</param>
    /// <param name="requiredBytes">Требуемый размер (если известен)</param>
    /// <returns>true если пользователь выбрал открыть настройки</returns>
    public static bool Show(Window owner, string modName, string installPath, long requiredBytes = 0)
    {
        var dialog = new DiskSpaceDialog
        {
            Owner = owner
        };
        
        dialog.MessageText.Text = LocalizationManager.F("disk_install_failed", modName);

        // Получаем информацию о диске
        var driveLetter = GetDriveLetter(installPath);
        var freeSpace = GetDiskFreeSpace(installPath);

        dialog.DriveText.Text = driveLetter ?? LocalizationManager.S("disk_unknown");
        dialog.FreeSpaceText.Text = freeSpace.HasValue ? FormatSize(freeSpace.Value) : LocalizationManager.S("disk_unknown");

        if (requiredBytes > 0)
        {
            dialog.RequiredSpaceText.Text = FormatSize(requiredBytes);
        }
        else
        {
            dialog.RequiredSpaceText.Text = LocalizationManager.S("disk_more_space");
        }
        
        dialog.ShowDialog();
        
        return dialog.OpenSettings;
    }
    
    /// <summary>
    /// Показывает общий диалог о нехватке места (без конкретного мода)
    /// </summary>
    public static bool ShowGeneric(Window owner, string installPath)
    {
        var dialog = new DiskSpaceDialog
        {
            Owner = owner
        };
        
        dialog.MessageText.Text = LocalizationManager.S("disk_not_enough_space");

        var driveLetter = GetDriveLetter(installPath);
        var freeSpace = GetDiskFreeSpace(installPath);

        dialog.DriveText.Text = driveLetter ?? LocalizationManager.S("disk_unknown");
        dialog.FreeSpaceText.Text = freeSpace.HasValue ? FormatSize(freeSpace.Value) : LocalizationManager.S("disk_unknown");
        dialog.RequiredSpaceText.Text = LocalizationManager.S("disk_free_up");
        
        dialog.ShowDialog();
        
        return dialog.OpenSettings;
    }
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
    
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings = true;
        Close();
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
