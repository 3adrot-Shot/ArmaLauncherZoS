using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArmaLauncherClient.Models;
using ArmaLauncherClient.ViewModels;

namespace ArmaLauncherClient.UC;

public partial class AdvancedSettingsDialog : Window
{
    private readonly MainViewModel _viewModel;
    private int _selectedImageIndex = 0; // 0 = ar3zka, 1 = AU
    
    private static AdvancedSettingsDialog? _instance;
    
    public AdvancedSettingsDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        
        LoadCurrentSettings();
        UpdateImageSelection();
    }
    
    /// <summary>
    /// Открывает немодальное окно настроек (синглтон)
    /// </summary>
    public static void Open(MainViewModel viewModel)
    {
        if (_instance != null && _instance.IsVisible)
        {
            _instance.Activate();
            return;
        }
        
        _instance = new AdvancedSettingsDialog(viewModel);
        _instance.Closed += (s, e) => _instance = null;
        _instance.Show();
    }
    
    private void LoadCurrentSettings()
    {
        // Загружаем настройки персонажа
        CharacterEnabledCheckbox.IsChecked = _viewModel.CharacterOverlayEnabled;
        OpacitySlider.Value = _viewModel.CharacterOpacity;
        HeightSlider.Value = _viewModel.CharacterMaxHeight;
        OffsetXSlider.Value = _viewModel.CharacterOffsetX;
        OffsetYSlider.Value = _viewModel.CharacterOffsetY;
        
        // Определяем выбранное изображение
        _selectedImageIndex = _viewModel.CharacterImagePath.Contains("AU") ? 1 : 0;
        
        // Загружаем цвета
        LoadColorsToInputs();
        
        // Подписываемся на изменения checkbox
        CharacterEnabledCheckbox.Checked += (s, e) => _viewModel.CharacterOverlayEnabled = true;
        CharacterEnabledCheckbox.Unchecked += (s, e) => _viewModel.CharacterOverlayEnabled = false;

        UpdateSliderTexts();
    }

    private void LoadColorsToInputs()
    {
        // Фон
        BackgroundMainInput.Text = ColorToHex(_viewModel.Theme.BackgroundMain);
        BackgroundPanelInput.Text = ColorToHex(_viewModel.Theme.BackgroundPanel);
        BackgroundCardInput.Text = ColorToHex(_viewModel.Theme.BackgroundCard);
        BackgroundInputInput.Text = ColorToHex(_viewModel.Theme.BackgroundInput);
        BackgroundHoverInput.Text = ColorToHex(_viewModel.Theme.BackgroundHover);
        BorderInput.Text = ColorToHex(_viewModel.Theme.Border);

        // Акценты
        AccentInput.Text = ColorToHex(_viewModel.Theme.Accent);
        AccentHoverInput.Text = ColorToHex(_viewModel.Theme.AccentHover);
        AccentLightInput.Text = ColorToHex(_viewModel.Theme.AccentLight);
        ButtonSecondaryInput.Text = ColorToHex(_viewModel.Theme.ButtonSecondary);

        // Статусы
        SuccessInput.Text = ColorToHex(_viewModel.Theme.Success);
        WarningInput.Text = ColorToHex(_viewModel.Theme.Warning);
        ErrorInput.Text = ColorToHex(_viewModel.Theme.Error);
        InfoInput.Text = ColorToHex(_viewModel.Theme.Info);

        // Текст
        TextPrimaryInput.Text = ColorToHex(_viewModel.Theme.TextPrimary);
        TextSecondaryInput.Text = ColorToHex(_viewModel.Theme.TextSecondary);
        TextMutedInput.Text = ColorToHex(_viewModel.Theme.TextMuted);
        TextDarkInput.Text = ColorToHex(_viewModel.Theme.TextDark);

        UpdateAllColorPreviews();
    }

    private void UpdateAllColorPreviews()
    {
        // Фон
        UpdatePreview(BackgroundMainPreview, _viewModel.Theme.BackgroundMain);
        UpdatePreview(BackgroundPanelPreview, _viewModel.Theme.BackgroundPanel);
        UpdatePreview(BackgroundCardPreview, _viewModel.Theme.BackgroundCard);
        UpdatePreview(BackgroundInputPreview, _viewModel.Theme.BackgroundInput);
        UpdatePreview(BackgroundHoverPreview, _viewModel.Theme.BackgroundHover);
        UpdatePreview(BorderPreview, _viewModel.Theme.Border);

        // Акценты
        UpdatePreview(AccentPreview, _viewModel.Theme.Accent);
        UpdatePreview(AccentHoverPreview, _viewModel.Theme.AccentHover);
        UpdatePreview(AccentLightPreview, _viewModel.Theme.AccentLight);
        UpdatePreview(ButtonSecondaryPreview, _viewModel.Theme.ButtonSecondary);

        // Статусы
        UpdatePreview(SuccessPreview, _viewModel.Theme.Success);
        UpdatePreview(WarningPreview, _viewModel.Theme.Warning);
        UpdatePreview(ErrorPreview, _viewModel.Theme.Error);
        UpdatePreview(InfoPreview, _viewModel.Theme.Info);

        // Текст
        UpdatePreview(TextPrimaryPreview, _viewModel.Theme.TextPrimary);
        UpdatePreview(TextSecondaryPreview, _viewModel.Theme.TextSecondary);
        UpdatePreview(TextMutedPreview, _viewModel.Theme.TextMuted);
        UpdatePreview(TextDarkPreview, _viewModel.Theme.TextDark);
    }

    private static void UpdatePreview(Border preview, Color color)
    {
        preview.Background = new SolidColorBrush(color);
    }

    private void UpdateImageSelection()
    {
        Image1Border.BorderBrush = _selectedImageIndex == 0 
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366f1")!)
            : Brushes.Transparent;
        Image2Border.BorderBrush = _selectedImageIndex == 1 
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366f1")) 
            : Brushes.Transparent;
    }
    
    private void SelectImage1_Click(object sender, MouseButtonEventArgs e)
    {
        _selectedImageIndex = 0;
        _viewModel.CharacterImagePath = "/Images/Back/ar3zka.png";
        UpdateImageSelection();
    }
    
    private void SelectImage2_Click(object sender, MouseButtonEventArgs e)
    {
        _selectedImageIndex = 1;
        _viewModel.CharacterImagePath = "/Images/Back/AU.png";
        UpdateImageSelection();
    }
    
    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        
        // Обновляем ViewModel
        _viewModel.CharacterOpacity = OpacitySlider.Value;
        _viewModel.CharacterMaxHeight = (int)HeightSlider.Value;
        _viewModel.CharacterOffsetX = (int)OffsetXSlider.Value;
        _viewModel.CharacterOffsetY = (int)OffsetYSlider.Value;

        UpdateSliderTexts();
    }

    private void UpdateSliderTexts()
    {
        if (OpacityText != null)
            OpacityText.Text = $"{(int)(OpacitySlider.Value * 100)}%";
        if (HeightText != null)
            HeightText.Text = $"{(int)HeightSlider.Value}px";
        if (OffsetXText != null)
            OffsetXText.Text = $"{(int)OffsetXSlider.Value}px";
        if (OffsetYText != null)
            OffsetYText.Text = $"{(int)OffsetYSlider.Value}px";
    }

    private void ColorInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        var colorName = textBox.Tag?.ToString();
        if (string.IsNullOrEmpty(colorName)) return;

        var hexValue = textBox.Text.Trim();
        if (!TryParseHex(hexValue, out var color)) return;

        // Применяем цвет через общий метод (без обновления TextBox чтобы избежать рекурсии)
        SetColorByName(colorName, color);
    }

    private void SetColorByName(string name, Color color)
    {
        switch (name)
        {
            case "BackgroundMain":
                _viewModel.Theme.BackgroundMain = color;
                UpdatePreview(BackgroundMainPreview, color);
                break;
            case "BackgroundPanel":
                _viewModel.Theme.BackgroundPanel = color;
                UpdatePreview(BackgroundPanelPreview, color);
                break;
            case "BackgroundCard":
                _viewModel.Theme.BackgroundCard = color;
                UpdatePreview(BackgroundCardPreview, color);
                break;
            case "BackgroundInput":
                _viewModel.Theme.BackgroundInput = color;
                UpdatePreview(BackgroundInputPreview, color);
                break;
            case "BackgroundHover":
                _viewModel.Theme.BackgroundHover = color;
                UpdatePreview(BackgroundHoverPreview, color);
                break;
            case "Border":
                _viewModel.Theme.Border = color;
                UpdatePreview(BorderPreview, color);
                break;
            case "Accent":
                _viewModel.Theme.Accent = color;
                UpdatePreview(AccentPreview, color);
                break;
            case "AccentHover":
                _viewModel.Theme.AccentHover = color;
                UpdatePreview(AccentHoverPreview, color);
                break;
            case "AccentLight":
                _viewModel.Theme.AccentLight = color;
                UpdatePreview(AccentLightPreview, color);
                break;
            case "ButtonSecondary":
                _viewModel.Theme.ButtonSecondary = color;
                UpdatePreview(ButtonSecondaryPreview, color);
                break;
            case "Success":
                _viewModel.Theme.Success = color;
                UpdatePreview(SuccessPreview, color);
                break;
            case "Warning":
                _viewModel.Theme.Warning = color;
                UpdatePreview(WarningPreview, color);
                break;
            case "Error":
                _viewModel.Theme.Error = color;
                UpdatePreview(ErrorPreview, color);
                break;
            case "Info":
                _viewModel.Theme.Info = color;
                UpdatePreview(InfoPreview, color);
                break;
            case "TextPrimary":
                _viewModel.Theme.TextPrimary = color;
                UpdatePreview(TextPrimaryPreview, color);
                break;
            case "TextSecondary":
                _viewModel.Theme.TextSecondary = color;
                UpdatePreview(TextSecondaryPreview, color);
                break;
            case "TextMuted":
                _viewModel.Theme.TextMuted = color;
                UpdatePreview(TextMutedPreview, color);
                break;
            case "TextDark":
                _viewModel.Theme.TextDark = color;
                UpdatePreview(TextDarkPreview, color);
                break;
        }
    }

    private static bool TryParseHex(string hex, out Color color)
    {
        color = Colors.Transparent;

        if (string.IsNullOrWhiteSpace(hex)) return false;
        
        if (!hex.StartsWith('#'))
            hex = "#" + hex;
        
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private static string ColorToHex(Color color)
    {
        if (color.A == 255)
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private string? _currentColorTarget;

    private void ColorPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;

        _currentColorTarget = border.Tag?.ToString();
        if (string.IsNullOrEmpty(_currentColorTarget)) return;

        // Получаем текущий цвет
        var currentColor = GetColorByName(_currentColorTarget);

        // Настраиваем ColorPicker
        ColorPicker.SetColor(currentColor);
        ColorPicker.ColorSelected -= OnColorSelected;
        ColorPicker.Cancelled -= OnColorCancelled;
        ColorPicker.ColorSelected += OnColorSelected;
        ColorPicker.Cancelled += OnColorCancelled;

        // Показываем Popup
        ColorPickerPopup.IsOpen = true;
    }

    private void OnColorSelected(Color color)
    {
        if (string.IsNullOrEmpty(_currentColorTarget)) return;

        // Применяем цвет
        ApplyColorToTarget(_currentColorTarget, color);

        ColorPickerPopup.IsOpen = false;
    }

    private void OnColorCancelled()
    {
        ColorPickerPopup.IsOpen = false;
    }

    private Color GetColorByName(string name)
    {
        return name switch
        {
            "BackgroundMain" => _viewModel.Theme.BackgroundMain,
            "BackgroundPanel" => _viewModel.Theme.BackgroundPanel,
            "BackgroundCard" => _viewModel.Theme.BackgroundCard,
            "BackgroundInput" => _viewModel.Theme.BackgroundInput,
            "BackgroundHover" => _viewModel.Theme.BackgroundHover,
            "Border" => _viewModel.Theme.Border,
            "Accent" => _viewModel.Theme.Accent,
            "AccentHover" => _viewModel.Theme.AccentHover,
            "AccentLight" => _viewModel.Theme.AccentLight,
            "ButtonSecondary" => _viewModel.Theme.ButtonSecondary,
            "Success" => _viewModel.Theme.Success,
            "Warning" => _viewModel.Theme.Warning,
            "Error" => _viewModel.Theme.Error,
            "Info" => _viewModel.Theme.Info,
            "TextPrimary" => _viewModel.Theme.TextPrimary,
            "TextSecondary" => _viewModel.Theme.TextSecondary,
            "TextMuted" => _viewModel.Theme.TextMuted,
            "TextDark" => _viewModel.Theme.TextDark,
            _ => Colors.White
        };
    }

    private void ApplyColorToTarget(string target, Color color)
    {
        var hex = ColorToHex(color);

        switch (target)
        {
            case "BackgroundMain":
                _viewModel.Theme.BackgroundMain = color;
                BackgroundMainInput.Text = hex;
                UpdatePreview(BackgroundMainPreview, color);
                break;
            case "BackgroundPanel":
                _viewModel.Theme.BackgroundPanel = color;
                BackgroundPanelInput.Text = hex;
                UpdatePreview(BackgroundPanelPreview, color);
                break;
            case "BackgroundCard":
                _viewModel.Theme.BackgroundCard = color;
                BackgroundCardInput.Text = hex;
                UpdatePreview(BackgroundCardPreview, color);
                break;
            case "BackgroundInput":
                _viewModel.Theme.BackgroundInput = color;
                BackgroundInputInput.Text = hex;
                UpdatePreview(BackgroundInputPreview, color);
                break;
            case "BackgroundHover":
                _viewModel.Theme.BackgroundHover = color;
                BackgroundHoverInput.Text = hex;
                UpdatePreview(BackgroundHoverPreview, color);
                break;
            case "Border":
                _viewModel.Theme.Border = color;
                BorderInput.Text = hex;
                UpdatePreview(BorderPreview, color);
                break;
            case "Accent":
                _viewModel.Theme.Accent = color;
                AccentInput.Text = hex;
                UpdatePreview(AccentPreview, color);
                break;
            case "AccentHover":
                _viewModel.Theme.AccentHover = color;
                AccentHoverInput.Text = hex;
                UpdatePreview(AccentHoverPreview, color);
                break;
            case "AccentLight":
                _viewModel.Theme.AccentLight = color;
                AccentLightInput.Text = hex;
                UpdatePreview(AccentLightPreview, color);
                break;
            case "ButtonSecondary":
                _viewModel.Theme.ButtonSecondary = color;
                ButtonSecondaryInput.Text = hex;
                UpdatePreview(ButtonSecondaryPreview, color);
                break;
            case "Success":
                _viewModel.Theme.Success = color;
                SuccessInput.Text = hex;
                UpdatePreview(SuccessPreview, color);
                break;
            case "Warning":
                _viewModel.Theme.Warning = color;
                WarningInput.Text = hex;
                UpdatePreview(WarningPreview, color);
                break;
            case "Error":
                _viewModel.Theme.Error = color;
                ErrorInput.Text = hex;
                UpdatePreview(ErrorPreview, color);
                break;
            case "Info":
                _viewModel.Theme.Info = color;
                InfoInput.Text = hex;
                UpdatePreview(InfoPreview, color);
                break;
            case "TextPrimary":
                _viewModel.Theme.TextPrimary = color;
                TextPrimaryInput.Text = hex;
                UpdatePreview(TextPrimaryPreview, color);
                break;
            case "TextSecondary":
                _viewModel.Theme.TextSecondary = color;
                TextSecondaryInput.Text = hex;
                UpdatePreview(TextSecondaryPreview, color);
                break;
            case "TextMuted":
                _viewModel.Theme.TextMuted = color;
                TextMutedInput.Text = hex;
                UpdatePreview(TextMutedPreview, color);
                break;
            case "TextDark":
                _viewModel.Theme.TextDark = color;
                TextDarkInput.Text = hex;
                UpdatePreview(TextDarkPreview, color);
                break;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Сбрасываем настройки персонажа
        _viewModel.CharacterOverlayEnabled = true;
        _viewModel.CharacterOpacity = 0.7;
        _viewModel.CharacterMaxHeight = 600;
        _viewModel.CharacterOffsetX = 350;
        _viewModel.CharacterOffsetY = 150;
        _viewModel.CharacterImagePath = "/Images/Back/ar3zka.png";
        _selectedImageIndex = 0;

        // Сбрасываем цвета
        _viewModel.Theme.ResetToDefaults();

        // Обновляем UI
        CharacterEnabledCheckbox.IsChecked = true;
        OpacitySlider.Value = 0.7;
        HeightSlider.Value = 600;
        OffsetXSlider.Value = 350;
        OffsetYSlider.Value = 150;

        UpdateImageSelection();
        LoadColorsToInputs();
        UpdateSliderTexts();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Сохраняем настройки
        _viewModel.SaveSettings();

        // Показываем уведомление
        MessageBox.Show("Настройки сохранены!", "Сохранено", MessageBoxButton.OK, MessageBoxImage.Information);
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
}
