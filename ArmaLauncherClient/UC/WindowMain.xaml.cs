using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArmaLauncherClient.Services;
using ArmaLauncherClient.UC;
using ArmaLauncherClient.ViewModels;

namespace ArmaLauncherClient
{
    /// <summary>
    /// Логика взаимодействия для WindowMain.xaml
    /// </summary>
    public partial class WindowMain : Window
    {
        public WindowMain()
        {
            InitializeComponent();
        }

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.RefreshModelsAsync();
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ModelItemViewModel model && ViewModel != null)
                await ViewModel.InstallAsync(model);
        }

        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ModelItemViewModel model && ViewModel != null)
            {
                var result = await ViewModel.VerifyAsync(model);

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

                    var dialogResult = UC.VerifyResultDialog.Show(this, model.Id, invalidFiles);

                    if (dialogResult == true)
                    {
                        // Пользователь выбрал "Исправить" - переустанавливаем
                        await ViewModel.InstallAsync(model);
                    }
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CancelDownload();
        }

        private async void SpeedTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.RunSpeedTestAsync();
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var diagnostics = StartupDiagnostics.RunFullDiagnostics();
                StartupDiagnostics.SaveDiagnosticsToFile(diagnostics);

                MessageBox.Show(
                    LocalizationManager.S("diag_saved_msg"),
                    LocalizationManager.S("diag_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(LocalizationManager.F("diag_error", ex.Message),
                    LocalizationManager.S("error_title"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        #region News Handlers
        
        private async void RefreshNews_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.LoadNewsAsync();
        }

        private void NewsItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string url && !string.IsNullOrEmpty(url))
            {
                OpenUrl(url);
            }
        }
        
        #endregion
        
        #region Website
        
        private void OpenWebsite_Click(object sender, MouseButtonEventArgs e)
        {
            OpenUrl("https://zos.strikearena.ru/");
        }
        
        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                FileLogger.Log($"Error opening URL: {ex.Message}");
            }
        }
        
        #endregion
        
        
        
        
        #region Server Monitor Handlers
        
        private async void RefreshServers_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.LoadServersAsync();
        }
        
        private void ServerPlayers_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is ArmaLauncherClient.Services.GameServerInfo server)
            {
                UC.PlayersDialog.Show(this, server);
            }
        }
        
        private void ServerStats_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is ArmaLauncherClient.Services.GameServerInfo server)
            {
                UC.ServerStatsDialog.Show(this, server, App.HttpClient);
            }
        }
        
        private void ServerMods_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is ArmaLauncherClient.Services.GameServerInfo server && ViewModel != null)
            {
                UC.ServerModsStatusDialog.Show(this, server, ViewModel);
            }
        }
        
        #endregion

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is LauncherSection section)
                ViewModel?.SetActiveSection(section);
        }


        private async void GamePrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.ExecuteGamePrimaryActionAsync();
        }

        private async void VerifyMainButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.MainModel == null) return;

            var result = await ViewModel.VerifyAsync(ViewModel.MainModel);

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

                var dialogResult = VerifyResultDialog.Show(this, ViewModel.MainModel.Id, invalidFiles);

                if (dialogResult == true)
                {
                    // Пользователь выбрал "Исправить" - переустанавливаем
                    await ViewModel.InstallAsync(ViewModel.MainModel);
                }
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.OpenGameFolder();
        }

        private void BrowseInstallPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = LocalizationManager.S("folder_install_title"),
                InitialDirectory = ViewModel?.CustomInstallPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };


            if (dialog.ShowDialog() == true)
            {
                if (ViewModel != null)
                    ViewModel.CustomInstallPath = dialog.FolderName;
            }
        }

        private void ResetInstallPath_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ResetInstallPath();
        }

        private void OpenThemeCustomizer_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            UC.AdvancedSettingsDialog.Open(ViewModel);
        }

        private void OpenAdvancedSettings_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;

            UC.AdvancedSettingsDialog.Open(ViewModel);
        }

        #region Game Path Handlers
        
        private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = LocalizationManager.S("folder_game_title"),
                InitialDirectory = ViewModel?.GamePath ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog() == true && ViewModel != null)
            {
                ViewModel.GamePath = dialog.FolderName;
                // Автоматически проверяем существующую установку
                _ = ViewModel.DetectExistingGameAsync(dialog.FolderName);
            }
        }

        private async void DetectExistingGame_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            await ViewModel.DetectExistingGameAsync(ViewModel.GamePath);
        }

        private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var path = ViewModel.GamePath;
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
        }

        private void ResetGamePath_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ResetGamePath();
        }
        
        #endregion

        #region Mods Path Handlers
        
        private void BrowseModsPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = LocalizationManager.S("folder_mods_title"),
                InitialDirectory = ViewModel?.ModsPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            if (dialog.ShowDialog() == true && ViewModel != null)
            {
                ViewModel.ModsPath = dialog.FolderName;
            }
        }

        private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            var path = ViewModel.ModsPath;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start("explorer.exe", path);
        }

        private void ResetModsPath_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ResetModsPath();
        }
        
        #endregion

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override async void OnClosed(EventArgs e)
        {
            if (DataContext is IAsyncDisposable disposable)
                await disposable.DisposeAsync();

            base.OnClosed(e);
        }
        
        private void ConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            var consoleDialog = new UC.ConsoleLogDialog
            {
                Owner = this
            };
            consoleDialog.Show();
        }
        
        private async void InstallAllAddons_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.InstallAllAddonsAsync();
        }

        private async void VerifyAllAddons_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.VerifyAllAddonsAsync();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            UC.AboutDialog.ShowAbout(this);
        }

        private void ToggleServerFilter_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ToggleServerFilter();
        }

        private void ClearModsServerFilter_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearModsServerFilter();
        }

        private async void InstallMissingServerMods_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.InstallMissingServerModsAsync();
        }

        private async void InstallServerMod_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ServerModStatusViewModel modStatus && ViewModel != null)
            {
                if (modStatus.Model != null)
                {
                    await ViewModel.InstallAsync(modStatus.Model);
                    // Обновляем статусы после установки
                    ViewModel.SelectedModsServer = ViewModel.SelectedModsServer; // Trigger update
                }
            }
        }

        private async void VerifyAllServerMods_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null) return;
            await ViewModel.VerifyAllServerModsAsync();
        }

        private async void AllServersSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            var selectedServer = UC.AllServersSpeedTestDialog.Show(this);

            // Если пользователь выбрал сервер - переключаемся на него
            if (selectedServer != null && ViewModel != null)
            {
                ViewModel.SelectedServer = selectedServer;
            }
        }
    }
}
