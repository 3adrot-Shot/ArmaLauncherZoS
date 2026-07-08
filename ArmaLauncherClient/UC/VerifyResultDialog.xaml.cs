using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class VerifyResultDialog : Window
{
    private readonly List<InvalidFileInfo> _invalidFiles;
    private readonly string _modelId;
    
    public bool ShouldFix { get; private set; }
    
    public VerifyResultDialog(string modelId, List<InvalidFileInfo> invalidFiles)
    {
        InitializeComponent();
        _modelId = modelId;
        _invalidFiles = invalidFiles;
        
        PopulateData();
    }
    
    private void PopulateData()
    {
        var missing = _invalidFiles.Count(f => f.IsMissing);
        var extra = _invalidFiles.Count(f => f.IsExtra);
        var sizeMismatch = _invalidFiles.Count(f => !f.IsMissing && !f.IsExtra);

        SummaryText.Text = $"Обнаружено {_invalidFiles.Count} файлов с несоответствиями";

        var details = new List<string>();
        if (missing > 0) details.Add($"{missing} отсутствуют");
        if (sizeMismatch > 0) details.Add($"{sizeMismatch} с неверным размером");
        if (extra > 0) details.Add($"{extra} лишних");
        SummaryDetails.Text = string.Join(", ", details);

        // Calculate total download size (лишние файлы не скачиваются - они удаляются)
        long totalSize = _invalidFiles.Where(f => !f.IsExtra).Sum(f => f.ExpectedSize);
        DownloadSizeText.Text = extra > 0
            ? $"Нужно скачать: {FormatBytes(totalSize)}, удалить файлов: {extra}"
            : $"Нужно скачать: {FormatBytes(totalSize)}";

        // Populate list
        var items = _invalidFiles.Select(f => new FileListItem
        {
            FileName = f.FilePath,
            Icon = f.IsMissing ? "❌" : f.IsExtra ? "🗑" : "⚠",
            IconColor = new SolidColorBrush(f.IsMissing 
                ? (Color)ColorConverter.ConvertFromString("#ef4444")! 
                : f.IsExtra
                    ? (Color)ColorConverter.ConvertFromString("#3b82f6")!
                    : (Color)ColorConverter.ConvertFromString("#f59e0b")!),
            Details = f.IsMissing 
                ? $"Отсутствует ({FormatBytes(f.ExpectedSize)})"
                : f.IsExtra
                    ? $"Лишний файл ({FormatBytes(f.LocalSize)}) — будет удалён"
                    : $"Размер: {FormatBytes(f.LocalSize)} → {FormatBytes(f.ExpectedSize)}",
            StatusText = f.IsMissing ? "отсутствует" : f.IsExtra ? "лишний" : "неверный размер",
            StatusColor = new SolidColorBrush(f.IsMissing 
                ? (Color)ColorConverter.ConvertFromString("#ef4444")! 
                : f.IsExtra
                    ? (Color)ColorConverter.ConvertFromString("#3b82f6")!
                    : (Color)ColorConverter.ConvertFromString("#f59e0b")!)
        }).ToList();

        FilesList.ItemsSource = items;
    }
    
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            DragMove();
    }
    
    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        ShouldFix = true;
        DialogResult = true;
        Close();
    }
    
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ShouldFix = false;
        DialogResult = false;
        Close();
    }
    
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
    
    public static bool? Show(Window owner, string modelId, List<InvalidFileInfo> invalidFiles)
    {
        var dialog = new VerifyResultDialog(modelId, invalidFiles)
        {
            Owner = owner
        };
        return dialog.ShowDialog();
    }
}

public class InvalidFileInfo
{
    public string FilePath { get; set; } = "";
    public bool IsMissing { get; set; }
    public bool IsExtra { get; set; }
    public long LocalSize { get; set; }
    public long ExpectedSize { get; set; }
}

public class FileListItem
{
    public string FileName { get; set; } = "";
    public string Icon { get; set; } = "";
    public Brush IconColor { get; set; } = Brushes.White;
    public string Details { get; set; } = "";
    public string StatusText { get; set; } = "";
    public Brush StatusColor { get; set; } = Brushes.White;
}
