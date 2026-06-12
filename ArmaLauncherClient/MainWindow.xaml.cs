using System;
using System.Windows;
using ArmaLauncherClient.Services;
using ArmaLauncherClient.ViewModels;

namespace ArmaLauncherClient
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private MainViewModel? ViewModel => DataContext as MainViewModel;

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.RefreshModelsAsync();
            }
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button &&
                button.Tag is ModelItemViewModel model &&
                ViewModel != null)
            {
                await ViewModel.InstallAsync(model);
            }
        }

        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button &&
                button.Tag is ModelItemViewModel model &&
                ViewModel != null)
            {
                await ViewModel.VerifyAsync(model);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.CancelDownload();
        }

        private async void SpeedTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                await ViewModel.RunSpeedTestAsync();
            }
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var diagnostics = StartupDiagnostics.RunFullDiagnostics();
                StartupDiagnostics.SaveDiagnosticsToFile(diagnostics);
                
                MessageBox.Show(
                    "Диагностика системы сохранена на рабочий стол.\n\n" +
                    "Отправьте этот файл разработчику для анализа проблемы.",
                    "Диагностика", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при создании диагностики: {ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override async void OnClosed(EventArgs e)
        {
            if (DataContext is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            base.OnClosed(e);
        }

        private void ComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {

        }
    }
}