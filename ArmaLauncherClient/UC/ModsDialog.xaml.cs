using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class ModsDialog : Window
{
    public ModsDialog()
    {
        InitializeComponent();
    }
    
    /// <summary>
    /// Показывает диалог с модами сервера (из мониторинга игровых серверов)
    /// </summary>
    public static void Show(Window owner, GameServerInfo server)
    {
        var dialog = new ModsDialog
        {
            Owner = owner
        };
        
        dialog.ServerNameText.Text = server.Name;
        dialog.ModsCountText.Text = LocalizationManager.F("server_mods_count", server.Mods.Count);
        dialog.TitleText.Text = LocalizationManager.F("mods_on_server_title", server.Mods.Count);
        
        if (server.Mods.Count > 0)
        {
            // Конвертируем ModInfo в AddonDisplayInfo
            var displayMods = server.Mods.Select(m => new AddonDisplayInfo
            {
                ModId = m.ModId,
                Name = m.Name,
                Version = m.Version,
                TotalSizeFormatted = "",
                FileCount = 0
            }).ToList();
            
            dialog.ModsList.ItemsSource = displayMods;
            dialog.EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.ModsList.Visibility = Visibility.Collapsed;
            dialog.EmptyState.Visibility = Visibility.Visible;
        }
        
        dialog.ShowDialog();
    }
    
    /// <summary>
    /// Показывает диалог с аддонами с сервера обновлений (полная информация)
    /// </summary>
    public static void ShowAddons(Window owner, List<AddonDisplayInfo> addons, string serverName)
    {
        var dialog = new ModsDialog
        {
            Owner = owner
        };
        
        dialog.ServerNameText.Text = serverName;
        dialog.ModsCountText.Text = LocalizationManager.F("addons_count", addons.Count);
        dialog.TitleText.Text = LocalizationManager.F("addons_on_server_title", addons.Count);
        
        if (addons.Count > 0)
        {
            dialog.ModsList.ItemsSource = addons;
            dialog.EmptyState.Visibility = Visibility.Collapsed;
            
            // Считаем общий размер
            var totalSize = addons.Sum(a => a.TotalSize);
            dialog.TotalSizeText.Text = LocalizationManager.F("total_size", FormatBytes(totalSize));
        }
        else
        {
            dialog.ModsList.Visibility = Visibility.Collapsed;
            dialog.EmptyState.Visibility = Visibility.Visible;
        }
        
        dialog.ShowDialog();
    }
    
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F2} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }
    
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void WorkshopButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string modId && !string.IsNullOrEmpty(modId))
        {
            var url = $"https://reforger.armaplatform.com/workshop/{modId}";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore errors opening browser
            }
        }
    }
}

/// <summary>
/// Модель для отображения аддона в UI
/// </summary>
public class AddonDisplayInfo
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public long TotalSize { get; set; }
    public string TotalSizeFormatted { get; set; } = "";
    public int FileCount { get; set; }
    public string? Changelog { get; set; }
    public List<AddonDependencyInfo>? Dependencies { get; set; }
    
    public bool HasDependencies => Dependencies != null && Dependencies.Count > 0;
}

public class AddonDependencyInfo
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}
