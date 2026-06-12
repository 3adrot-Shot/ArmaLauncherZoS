using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class AllServersSpeedTestDialog : Window
{
    private readonly SpeedTestService _speedTestService;
    private CancellationTokenSource? _cts;
    private bool _isTesting;
    private ServerInfo? _bestServer;
    private SpeedTestService.SpeedTestResult? _bestResult;

    public AllServersSpeedTestDialog()
    {
        InitializeComponent();
        _speedTestService = new SpeedTestService(App.HttpClient);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }

    private async void StartTest_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting)
        {
            _cts?.Cancel();
            return;
        }

        _isTesting = true;
        _cts = new CancellationTokenSource();
        StartButton.Content = "⏹ Стоп";
        CloseButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        BestServerBorder.Visibility = Visibility.Collapsed;
        ResultsPanel.Children.Clear();

        _bestServer = null;
        _bestResult = null;

        // Используем только доступные серверы (у которых есть Game)
        var servers = App.AvailableServers.Where(s => s.HasGame).ToList();
        var results = new List<(ServerInfo Server, SpeedTestService.SpeedTestResult Result)>();

        try
        {
            for (int i = 0; i < servers.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested)
                    break;

                var server = servers[i];
                StatusText.Text = $"Тестирование {i + 1}/{servers.Count}: {server.Name}...";
                ProgressBar.Value = (i * 100.0) / servers.Count;

                // Add placeholder for this server
                var serverPanel = CreateServerResultPanel(server.Name, "Тестирование...", null);
                ResultsPanel.Children.Add(serverPanel);

                // Run the speed test
                var progress = new Progress<string>(msg =>
                {
                    StatusText.Text = $"{server.Name}: {msg}";
                });
                
                var result = await _speedTestService.RunSpeedTestAsync(server.Url, progress, _cts.Token);
                results.Add((server, result));
                
                // Update the panel with results
                ResultsPanel.Children.Remove(serverPanel);
                var updatedPanel = CreateServerResultPanel(server.Name, GetResultSummary(result), result);
                ResultsPanel.Children.Add(updatedPanel);
                
                // Track best server
                if (result.Success)
                {
                    if (_bestResult == null || result.DownloadSpeedMBps > _bestResult.DownloadSpeedMBps)
                    {
                        _bestServer = server;
                        _bestResult = result;
                    }
                }
            }
            
            // Show best server
            if (_bestServer != null && _bestResult != null)
            {
                BestServerName.Text = _bestServer.Name;
                BestServerDetails.Text = $"Ping: {_bestResult.PingMs:F0}ms | Скорость: {_bestResult.DownloadSpeedMBps:F1} MB/s ({_bestResult.DownloadSpeedMbps:F0} Mbps)";
                BestServerBorder.Visibility = Visibility.Visible;
                ButtonsPanel.Visibility = Visibility.Collapsed;
            }
            
            StatusText.Text = _cts.Token.IsCancellationRequested 
                ? "Тест отменён" 
                : $"Тест завершён. Проверено серверов: {results.Count}";
            ProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Тест отменён";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            _isTesting = false;
            StartButton.Content = "▶ Начать";
            CloseButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private Border CreateServerResultPanel(string serverName, string status, SpeedTestService.SpeedTestResult? result)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a2744")!),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoPanel = new StackPanel();

        var nameText = new TextBlock
        {
            Text = serverName,
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };
        infoPanel.Children.Add(nameText);

        var statusText = new TextBlock
        {
            Text = status,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };

        if (result == null)
        {
            statusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9ca3af")!);
        }
        else if (result.Success)
        {
            statusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22c55e")!);
        }
        else
        {
            statusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ef4444")!);
        }

        infoPanel.Children.Add(statusText);
        Grid.SetColumn(infoPanel, 0);
        grid.Children.Add(infoPanel);

        // Speed indicator
        if (result != null && result.Success)
        {
            var speedPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var speedText = new TextBlock
            {
                Text = $"{result.DownloadSpeedMBps:F1} MB/s",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366f1")!),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            speedPanel.Children.Add(speedText);

            Grid.SetColumn(speedPanel, 1);
            grid.Children.Add(speedPanel);
        }

        border.Child = grid;
        return border;
    }

    private static string GetResultSummary(SpeedTestService.SpeedTestResult result)
    {
        if (!result.Success)
            return $"Ошибка: {result.Error}";

        return $"Ping: {result.PingMs:F0}ms | {result.DownloadSpeedMbps:F0} Mbps";
    }

    private void UseBestServer_Click(object sender, RoutedEventArgs e)
    {
        if (_bestServer == null) return;

        SelectedServer = _bestServer;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Выбранный сервер для переключения (если пользователь нажал "Использовать")
    /// </summary>
    public ServerInfo? SelectedServer { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isTesting)
        {
            _cts?.Cancel();
        }
        base.OnClosing(e);
    }

    public static ServerInfo? Show(Window owner)
    {
        var dialog = new AllServersSpeedTestDialog
        {
            Owner = owner
        };

        if (dialog.ShowDialog() == true && dialog.SelectedServer != null)
        {
            return dialog.SelectedServer;
        }
        return null;
    }
}
