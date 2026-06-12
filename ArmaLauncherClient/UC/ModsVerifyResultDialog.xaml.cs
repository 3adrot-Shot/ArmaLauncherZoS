using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArmaLauncherClient.Services;
using ArmaLauncherClient.ViewModels;

namespace ArmaLauncherClient.UC;

public partial class ModsVerifyResultDialog : Window
{
    private List<ModVerifyResultItem> _mods = [];
    private List<string> _selectedModsToRepair = [];

    public List<string> ModsToRepair => _selectedModsToRepair;

    public ModsVerifyResultDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Показывает диалог с результатами проверки модов
    /// </summary>
    public static List<string>? Show(Window owner, List<ModVerifyResultItem> results)
    {
        var dialog = new ModsVerifyResultDialog
        {
            Owner = owner
        };

        // Сортируем: сначала повреждённые, потом не установленные, потом OKа
        var sortedResults = results
            .OrderBy(r => r.Status switch
            {
                ModVerifyStatus.Invalid => 0,
                ModVerifyStatus.NotInstalled => 1,
                ModVerifyStatus.Valid => 2,
                _ => 3
            })
            .ThenBy(r => r.Name)
            .ToList();

        dialog._mods = sortedResults;
        dialog.ModsList.ItemsSource = sortedResults;

        // Подсчёт статистики
        int valid = results.Count(r => r.Status == ModVerifyStatus.Valid);
        int invalid = results.Count(r => r.Status == ModVerifyStatus.Invalid);
        int notInstalled = results.Count(r => r.Status == ModVerifyStatus.NotInstalled);

        dialog.ValidCountText.Text = valid.ToString();
        dialog.InvalidCountText.Text = invalid.ToString();
        dialog.NotInstalledCountText.Text = notInstalled.ToString();

        // Показываем элементы управления если есть повреждённые моды
        if (invalid > 0)
        {
            dialog.SelectAllBorder.Visibility = Visibility.Visible;
            dialog.RepairButton.Visibility = Visibility.Visible;
            dialog.TitleIcon.Text = "⚠";
            dialog.TitleText.Text = $"Найдено {invalid} повреждённых модов";

            // По умолчанию выбираем все повреждённые
            foreach (var mod in results.Where(r => r.Status == ModVerifyStatus.Invalid))
            {
                mod.IsSelected = true;
            }
            dialog.SelectAllCheckbox.IsChecked = true;
            dialog.UpdateSelectedCount();
        }
        else
        {
            dialog.TitleIcon.Text = "✓";
            dialog.TitleText.Text = "Все моды в порядке";
            dialog.StatusText.Text = $"Проверено {results.Count} модов - все файлы корректны";
        }

        var result = dialog.ShowDialog();

        if (result == true && dialog._selectedModsToRepair.Count > 0)
        {
            return dialog._selectedModsToRepair;
        }

        return null;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        _selectedModsToRepair = _mods
            .Where(m => m.IsSelected && m.Status == ModVerifyStatus.Invalid)
            .Select(m => m.FolderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList()!;

        DialogResult = true;
        Close();
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_mods == null || _mods.Count == 0) return;

        var isChecked = SelectAllCheckbox.IsChecked == true;
        foreach (var mod in _mods.Where(m => m.Status == ModVerifyStatus.Invalid))
        {
            mod.IsSelected = isChecked;
        }
        ModsList.ItemsSource = null;
        ModsList.ItemsSource = _mods;
        UpdateSelectedCount();
    }

    private void ModCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_mods == null || _mods.Count == 0) return;
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        if (_mods == null || _mods.Count == 0) return;

        int selected = _mods.Count(m => m.IsSelected && m.Status == ModVerifyStatus.Invalid);
        int total = _mods.Count(m => m.Status == ModVerifyStatus.Invalid);

        SelectedCountText.Text = $"{selected} из {total} выбрано";
        RepairButton.Content = selected > 0 ? $"🔧 Починить ({selected})" : "🔧 Починить выбранные";
        RepairButton.IsEnabled = selected > 0;

        // Обновляем состояние "выбрать все"
        SelectAllCheckbox.IsChecked = selected == total && total > 0 ? true : (selected == 0 ? false : null);
    }
}

/// <summary>
/// Статус проверки мода
/// </summary>
public enum ModVerifyStatus
{
    Valid,
    Invalid,
    NotInstalled
}

/// <summary>
/// Результат проверки мода
/// </summary>
public class ModVerifyResultItem : INotifyPropertyChanged
{
    public string FolderId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public ModVerifyStatus Status { get; set; }
    public List<InvalidFileDisplayItem> InvalidFiles { get; set; } = [];

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    public bool CanRepair => Status == ModVerifyStatus.Invalid;
    public bool HasVersion => !string.IsNullOrEmpty(Version);
    public bool HasInvalidFiles => InvalidFiles.Count > 0;
    public string InvalidFilesHeader => $"📁 {InvalidFiles.Count} файлов с ошибками";

    public string StatusText => Status switch
    {
        ModVerifyStatus.Valid => "✓ Все файлы в порядке",
        ModVerifyStatus.Invalid => $"✕ {InvalidFiles.Count} файлов повреждено",
        ModVerifyStatus.NotInstalled => "○ Мод не установлен",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Информация о повреждённом файле для отображения
/// </summary>
public class InvalidFileDisplayItem
{
    public string FileName { get; set; } = "";
    public bool IsMissing { get; set; }
    public string Icon => IsMissing ? "✕" : "⚠";
    public string IconColor => IsMissing ? "#ef4444" : "#f59e0b";
}
