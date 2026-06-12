using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class ConsoleLogDialog : Window
{
    private readonly DispatcherTimer _updateTimer;
    private int _lineCount = 0;
    
    public ConsoleLogDialog()
    {
        InitializeComponent();
        
        // Показываем путь к файлу логов
        LogFilePathText.Text = FileLogger.LogFilePath;
        
        // Загружаем существующие логи
        LoadExistingLogs();
        
        // Подписываемся на новые логи
        FileLogger.OnLogEntry += OnNewLogEntry;
        
        // Таймер для периодической проверки и прокрутки
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
        
        Closed += (_, _) => 
        {
            FileLogger.OnLogEntry -= OnNewLogEntry;
            _updateTimer.Stop();
        };
    }
    
    private void LoadExistingLogs()
    {
        try
        {
            var logPath = FileLogger.LogFilePath;
            if (File.Exists(logPath))
            {
                // Читаем последние 500 строк чтобы не перегружать UI
                var lines = File.ReadAllLines(logPath);
                var startIndex = Math.Max(0, lines.Length - 500);
                var relevantLines = lines.Skip(startIndex).ToArray();
                
                if (startIndex > 0)
                {
                    LogTextBox.Text = $"... (пропущено {startIndex} строк) ...\n\n" + string.Join("\n", relevantLines);
                }
                else
                {
                    LogTextBox.Text = string.Join("\n", relevantLines);
                }
                
                _lineCount = lines.Length;
                UpdateLineCount();
                ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            LogTextBox.Text = $"Ошибка загрузки логов: {ex.Message}";
        }
    }
    
    private void OnNewLogEntry(string logLine)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // Добавляем новую строку
            if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                LogTextBox.AppendText("\n");
            }
            LogTextBox.AppendText(logLine);
            _lineCount++;
            
            // Ограничиваем размер текста чтобы не съедало память
            if (_lineCount > 2000)
            {
                var text = LogTextBox.Text;
                var firstNewLine = text.IndexOf('\n');
                if (firstNewLine > 0)
                {
                    LogTextBox.Text = text.Substring(firstNewLine + 1);
                    _lineCount--;
                }
            }
            
            UpdateLineCount();
        });
    }
    
    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (AutoScrollCheckBox.IsChecked == true)
        {
            ScrollToEnd();
        }
    }
    
    private void ScrollToEnd()
    {
        LogScrollViewer.ScrollToEnd();
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
    }
    
    private void UpdateLineCount()
    {
        LogCountText.Text = $" ({_lineCount} строк)";
    }
    
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        _lineCount = 0;
        UpdateLineCount();
    }
    
    private void OpenLogFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = FileLogger.LogFilePath;
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
            }
            else
            {
                MessageBox.Show("Файл логов не найден", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть файл: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
