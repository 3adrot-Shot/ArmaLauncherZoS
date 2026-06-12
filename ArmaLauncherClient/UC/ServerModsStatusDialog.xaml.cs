using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArmaLauncherClient.Services;
using ArmaLauncherClient.ViewModels;

namespace ArmaLauncherClient.UC;

public partial class ServerModsStatusDialog : Window
{
    private MainViewModel? _viewModel;
    private UpdateManager? _updateManager;
    private readonly List<ModStatusItem> _downloadableMods = [];

    public ServerModsStatusDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Показывает диалог с модами сервера и их статусами совместимости
    /// </summary>
    public static void Show(Window owner, GameServerInfo server, MainViewModel viewModel)
    {
        var dialog = new ServerModsStatusDialog
        {
            Owner = owner
        };
        dialog._viewModel = viewModel;
        dialog._updateManager = viewModel.GetUpdateManager();

        dialog.ServerNameText.Text = server.Name;
        dialog.TitleText.Text = $"🔍 Моды на сервере ({server.Mods.Count})";

        if (server.Mods.Count == 0)
        {
            dialog.ModsList.Visibility = Visibility.Collapsed;
            dialog.EmptyState.Visibility = Visibility.Visible;
            dialog.ShowDialog();
            return;
        }

        // Получаем список доступных для скачивания модов (из каталога сервера)
        var downloadableMods = viewModel.Models
            .Where(m => !string.IsNullOrEmpty(m.ModId))
            .ToDictionary(m => m.ModId, m => m);

        var modItems = new List<ModStatusItem>();
        int installedCount = 0;
        int missingCount = 0;
        int mismatchCount = 0;

        foreach (var serverMod in server.Mods)
        {
            var item = new ModStatusItem
            {
                ModId = serverMod.ModId,
                Name = serverMod.Name,
                ServerVersion = serverMod.Version
            };

            // Ищем мод в каталоге
            ModelItemViewModel? catalogModel = null;
            downloadableMods.TryGetValue(serverMod.ModId, out catalogModel);

            // Проверяем установку через UpdateManager (читает версию из meta/ServerData.json)
            UpdateCheckResult? localCheck = null;
            if (catalogModel != null && dialog._updateManager != null)
            {
                localCheck = dialog._updateManager.CheckAddonInstallation(catalogModel.Id, serverMod.Version);
            }

            if (localCheck?.IsInstalled == true)
            {
                item.IsInstalled = true;
                item.LocalVersion = localCheck.InstalledVersion ?? "unknown";
                item.FolderId = catalogModel?.Id;

                if (item.LocalVersion == serverMod.Version || item.LocalVersion == "unknown")
                {
                    item.Status = ModCompatibilityStatus.Installed;
                    installedCount++;
                }
                else
                {
                    // Версии не совпадают!
                    item.Status = ModCompatibilityStatus.VersionMismatch;
                    mismatchCount++;
                }
            }
            else if (catalogModel != null)
            {
                // Не установлен, но доступен для скачивания
                item.Status = ModCompatibilityStatus.MissingCanDownload;
                item.CanDownload = true;
                item.FolderId = catalogModel.Id;
                item.LatestVersion = catalogModel.LatestVersion;
                dialog._downloadableMods.Add(item);
                missingCount++;
            }
            else
            {
                // Не установлен и недоступен
                item.Status = ModCompatibilityStatus.Missing;
                missingCount++;
            }

            modItems.Add(item);
        }

        // Сортируем: сначала проблемные
        modItems.Sort((a, b) => 
        {
            int GetPriority(ModCompatibilityStatus s) => s switch
            {
                ModCompatibilityStatus.Missing => 0,
                ModCompatibilityStatus.MissingCanDownload => 1,
                ModCompatibilityStatus.VersionMismatch => 2,
                ModCompatibilityStatus.Installed => 3,
                _ => 4
            };
            return GetPriority(a.Status).CompareTo(GetPriority(b.Status));
        });

        dialog.ModsList.ItemsSource = modItems;
        dialog.InstalledCountText.Text = installedCount.ToString();
        dialog.MissingCountText.Text = missingCount.ToString();
        dialog.MismatchCountText.Text = mismatchCount.ToString();

        // Показываем кнопку "Скачать все" если есть моды для скачивания
        if (dialog._downloadableMods.Count > 0)
        {
            dialog.DownloadAllButton.Visibility = Visibility.Visible;
            dialog.DownloadAllButton.Content = $"📥 Скачать недостающие ({dialog._downloadableMods.Count})";
        }

        // Статус
        if (missingCount == 0 && mismatchCount == 0)
        {
            dialog.StatusText.Text = "✓ Все моды совместимы с сервером";
            dialog.StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x22, 0xc5, 0x5e));
        }
        else if (missingCount > 0)
        {
            dialog.StatusText.Text = $"⚠ Для игры на сервере нужно установить {missingCount} мод(ов)";
            dialog.StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xef, 0x44, 0x44));
        }
        else
        {
            dialog.StatusText.Text = $"⚠ Версии {mismatchCount} мод(ов) отличаются от серверных";
            dialog.StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xf5, 0x9e, 0x0b));
        }

        dialog.ShowDialog();
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

    private async void DownloadModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        if (sender is Button button && button.Tag is ModStatusItem item && item.CanDownload)
        {
            Close();

            // Найти модель и запустить установку
            var model = _viewModel.Models.FirstOrDefault(m => m.Id == item.FolderId);
            if (model != null)
            {
                await _viewModel.InstallAsync(model);
            }
        }
    }

    private async void VerifyModButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        if (sender is Button button && button.Tag is ModStatusItem item && item.IsInstalled && item.FolderId != null)
        {
            var model = _viewModel.Models.FirstOrDefault(m => m.Id == item.FolderId);
            if (model != null)
            {
                Close();

                var result = await _viewModel.VerifyAsync(model);

                // Показываем диалог если есть ошибки
                if (result != null && !result.Success && result.InvalidFileDetails?.Count > 0)
                {
                    var invalidFiles = result.InvalidFileDetails
                        .Select(f => new InvalidFileInfo
                        {
                            FilePath = f.Path,
                            IsMissing = f.IsMissing,
                            LocalSize = f.LocalSize,
                            ExpectedSize = f.ExpectedSize
                        })
                        .ToList();

                    var dialogResult = VerifyResultDialog.Show(Owner, model.Id, invalidFiles);

                    if (dialogResult == true)
                    {
                        await _viewModel.InstallAsync(model);
                    }
                }
            }
        }
    }

    private async void DownloadAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || _downloadableMods.Count == 0) return;

        Close();

        // Устанавливаем все недостающие моды
        foreach (var item in _downloadableMods)
        {
            var model = _viewModel.Models.FirstOrDefault(m => m.Id == item.FolderId);
            if (model != null)
            {
                await _viewModel.InstallAsync(model);
            }
        }
    }
}

/// <summary>
/// Статус совместимости мода
/// </summary>
public enum ModCompatibilityStatus
{
    Installed,
    Missing,
    MissingCanDownload,
    VersionMismatch
}

/// <summary>
/// Модель мода с информацией о статусе
/// </summary>
public class ModStatusItem
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ServerVersion { get; set; } = "";
    public string LocalVersion { get; set; } = "";
    public string? LatestVersion { get; set; }
    public bool IsInstalled { get; set; }
    public bool CanDownload { get; set; }
    public string? FolderId { get; set; }
    public ModCompatibilityStatus Status { get; set; } = ModCompatibilityStatus.Missing;
}
