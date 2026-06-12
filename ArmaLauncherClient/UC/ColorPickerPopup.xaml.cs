using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArmaLauncherClient.UC;

public partial class ColorPickerPopup : UserControl
{
    public event Action<Color>? ColorSelected;
    public event Action? Cancelled;
    
    private Color _currentColor;
    private Color _originalColor;
    private bool _isUpdating;
    
    // Предустановленные цвета
    private static readonly string[] PresetColorValues =
    [
        // Тёмные фоны
        "#0d1424", "#111830", "#151d33", "#1f2a44", "#2a3352",
        // Акценты
        "#6366f1", "#4f46e5", "#8b5cf6", "#ec4899", "#f43f5e",
        // Статусы
        "#10b981", "#22c55e", "#f59e0b", "#f97316", "#ef4444",
        // Текст
        "#ffffff", "#d1d5f9", "#9ca3af", "#6b7280", "#374151",
        // Дополнительные
        "#3b82f6", "#0ea5e9", "#14b8a6", "#84cc16", "#a855f7"
    ];
    
    public ColorPickerPopup()
    {
        InitializeComponent();
        InitializePresetColors();
    }
    
    public void SetColor(Color color)
    {
        _originalColor = color;
        _currentColor = color;
        UpdateUIFromColor(color);
    }
    
    private void InitializePresetColors()
    {
        foreach (var hex in PresetColorValues)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var border = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Tag = color
            };
            
            border.MouseEnter += (s, e) => 
            {
                if (s is Border b)
                    b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6366f1"));
            };
            border.MouseLeave += (s, e) => 
            {
                if (s is Border b)
                    b.BorderBrush = Brushes.Transparent;
            };
            border.MouseLeftButtonUp += PresetColor_Click;
            
            PresetColors.Children.Add(border);
        }
    }
    
    private void PresetColor_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is Color color)
        {
            _currentColor = color;
            UpdateUIFromColor(color);
        }
    }
    
    private void UpdateUIFromColor(Color color)
    {
        _isUpdating = true;
        
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        
        RedValue.Text = color.R.ToString();
        GreenValue.Text = color.G.ToString();
        BlueValue.Text = color.B.ToString();
        
        HexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ColorPreview.Background = new SolidColorBrush(color);
        
        _isUpdating = false;
    }
    
    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating || !IsLoaded) return;
        
        _currentColor = Color.FromRgb(
            (byte)RedSlider.Value,
            (byte)GreenSlider.Value,
            (byte)BlueSlider.Value);
        
        _isUpdating = true;
        
        RedValue.Text = ((int)RedSlider.Value).ToString();
        GreenValue.Text = ((int)GreenSlider.Value).ToString();
        BlueValue.Text = ((int)BlueSlider.Value).ToString();
        
        HexInput.Text = $"#{_currentColor.R:X2}{_currentColor.G:X2}{_currentColor.B:X2}";
        ColorPreview.Background = new SolidColorBrush(_currentColor);
        
        _isUpdating = false;
    }
    
    private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating) return;
        
        var hex = HexInput.Text.Trim();
        if (!hex.StartsWith('#'))
            hex = "#" + hex;
        
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            _currentColor = color;
            
            _isUpdating = true;
            
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            
            RedValue.Text = color.R.ToString();
            GreenValue.Text = color.G.ToString();
            BlueValue.Text = color.B.ToString();
            
            ColorPreview.Background = new SolidColorBrush(color);
            
            _isUpdating = false;
        }
        catch
        {
            // Невалидный HEX - игнорируем
        }
    }
    
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ColorSelected?.Invoke(_currentColor);
    }
    
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke();
    }
}
