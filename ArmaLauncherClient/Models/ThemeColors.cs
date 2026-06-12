using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ArmaLauncherClient.Models;

/// <summary>
/// Настраиваемые цвета темы интерфейса
/// </summary>
public class ThemeColors : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // ===== ФОНЫ =====

    private Color _backgroundMain = (Color)ColorConverter.ConvertFromString("#0d1424")!;
    public Color BackgroundMain
    {
        get => _backgroundMain;
        set { _backgroundMain = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundMainBrush)); }
    }
    public SolidColorBrush BackgroundMainBrush => new(_backgroundMain);

    private Color _backgroundPanel = (Color)ColorConverter.ConvertFromString("#111830")!;
    public Color BackgroundPanel
    {
        get => _backgroundPanel;
        set { _backgroundPanel = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundPanelBrush)); }
    }
    public SolidColorBrush BackgroundPanelBrush => new(_backgroundPanel);

    private Color _backgroundCard = (Color)ColorConverter.ConvertFromString("#151d33")!;
    public Color BackgroundCard
    {
        get => _backgroundCard;
        set { _backgroundCard = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundCardBrush)); OnPropertyChanged(nameof(BackgroundCardTransparentBrush)); }
    }
    public SolidColorBrush BackgroundCardBrush => new(_backgroundCard);
    public SolidColorBrush BackgroundCardTransparentBrush => new(Color.FromArgb(204, _backgroundCard.R, _backgroundCard.G, _backgroundCard.B));

    private Color _backgroundInput = (Color)ColorConverter.ConvertFromString("#1f2a44")!;
    public Color BackgroundInput
    {
        get => _backgroundInput;
        set { _backgroundInput = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundInputBrush)); }
    }
    public SolidColorBrush BackgroundInputBrush => new(_backgroundInput);

    private Color _backgroundHover = (Color)ColorConverter.ConvertFromString("#2a3f5f")!;
    public Color BackgroundHover
    {
        get => _backgroundHover;
        set { _backgroundHover = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackgroundHoverBrush)); }
    }
    public SolidColorBrush BackgroundHoverBrush => new(_backgroundHover);

    // ===== ГРАНИЦЫ =====

    private Color _border = (Color)ColorConverter.ConvertFromString("#2a3352")!;
    public Color Border
    {
        get => _border;
        set { _border = value; OnPropertyChanged(); OnPropertyChanged(nameof(BorderBrush)); }
    }
    public SolidColorBrush BorderBrush => new(_border);

    // ===== АКЦЕНТЫ =====

    private Color _accent = (Color)ColorConverter.ConvertFromString("#6366f1")!;
    public Color Accent
    {
        get => _accent;
        set { _accent = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccentBrush)); }
    }
    public SolidColorBrush AccentBrush => new(_accent);

    private Color _accentHover = (Color)ColorConverter.ConvertFromString("#4f46e5")!;
    public Color AccentHover
    {
        get => _accentHover;
        set { _accentHover = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccentHoverBrush)); }
    }
    public SolidColorBrush AccentHoverBrush => new(_accentHover);

    private Color _accentLight = (Color)ColorConverter.ConvertFromString("#7d81ff")!;
    public Color AccentLight
    {
        get => _accentLight;
        set { _accentLight = value; OnPropertyChanged(); OnPropertyChanged(nameof(AccentLightBrush)); }
    }
    public SolidColorBrush AccentLightBrush => new(_accentLight);

    private Color _buttonSecondary = (Color)ColorConverter.ConvertFromString("#6269AB")!;
    public Color ButtonSecondary
    {
        get => _buttonSecondary;
        set { _buttonSecondary = value; OnPropertyChanged(); OnPropertyChanged(nameof(ButtonSecondaryBrush)); }
    }
    public SolidColorBrush ButtonSecondaryBrush => new(_buttonSecondary);

    // ===== СТАТУСЫ =====

    private Color _success = (Color)ColorConverter.ConvertFromString("#10b981")!;
    public Color Success
    {
        get => _success;
        set { _success = value; OnPropertyChanged(); OnPropertyChanged(nameof(SuccessBrush)); }
    }
    public SolidColorBrush SuccessBrush => new(_success);

    private Color _warning = (Color)ColorConverter.ConvertFromString("#f59e0b")!;
    public Color Warning
    {
        get => _warning;
        set { _warning = value; OnPropertyChanged(); OnPropertyChanged(nameof(WarningBrush)); }
    }
    public SolidColorBrush WarningBrush => new(_warning);

    private Color _error = (Color)ColorConverter.ConvertFromString("#ef4444")!;
    public Color Error
    {
        get => _error;
        set { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(ErrorBrush)); }
    }
    public SolidColorBrush ErrorBrush => new(_error);

    private Color _info = (Color)ColorConverter.ConvertFromString("#3b82f6")!;
    public Color Info
    {
        get => _info;
        set { _info = value; OnPropertyChanged(); OnPropertyChanged(nameof(InfoBrush)); }
    }
    public SolidColorBrush InfoBrush => new(_info);

    // ===== ТЕКСТ =====

    private Color _textPrimary = Colors.White;
    public Color TextPrimary
    {
        get => _textPrimary;
        set { _textPrimary = value; OnPropertyChanged(); OnPropertyChanged(nameof(TextPrimaryBrush)); }
    }
    public SolidColorBrush TextPrimaryBrush => new(_textPrimary);

    private Color _textSecondary = (Color)ColorConverter.ConvertFromString("#d1d5f9")!;
    public Color TextSecondary
    {
        get => _textSecondary;
        set { _textSecondary = value; OnPropertyChanged(); OnPropertyChanged(nameof(TextSecondaryBrush)); }
    }
    public SolidColorBrush TextSecondaryBrush => new(_textSecondary);

    private Color _textMuted = (Color)ColorConverter.ConvertFromString("#9ca3af")!;
    public Color TextMuted
    {
        get => _textMuted;
        set { _textMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(TextMutedBrush)); }
    }
    public SolidColorBrush TextMutedBrush => new(_textMuted);

    private Color _textDark = (Color)ColorConverter.ConvertFromString("#6b7280")!;
    public Color TextDark
    {
        get => _textDark;
        set { _textDark = value; OnPropertyChanged(); OnPropertyChanged(nameof(TextDarkBrush)); }
    }
    public SolidColorBrush TextDarkBrush => new(_textDark);

    /// <summary>
    /// Сбрасывает все цвета на значения по умолчанию
    /// </summary>
    public void ResetToDefaults()
    {
        BackgroundMain = (Color)ColorConverter.ConvertFromString("#0d1424")!;
        BackgroundPanel = (Color)ColorConverter.ConvertFromString("#111830")!;
        BackgroundCard = (Color)ColorConverter.ConvertFromString("#151d33")!;
        BackgroundInput = (Color)ColorConverter.ConvertFromString("#1f2a44")!;
        BackgroundHover = (Color)ColorConverter.ConvertFromString("#2a3f5f")!;
        Border = (Color)ColorConverter.ConvertFromString("#2a3352")!;

        Accent = (Color)ColorConverter.ConvertFromString("#6366f1")!;
        AccentHover = (Color)ColorConverter.ConvertFromString("#4f46e5")!;
        AccentLight = (Color)ColorConverter.ConvertFromString("#7d81ff")!;
        ButtonSecondary = (Color)ColorConverter.ConvertFromString("#6269AB")!;

        Success = (Color)ColorConverter.ConvertFromString("#10b981")!;
        Warning = (Color)ColorConverter.ConvertFromString("#f59e0b")!;
        Error = (Color)ColorConverter.ConvertFromString("#ef4444")!;
        Info = (Color)ColorConverter.ConvertFromString("#3b82f6")!;

        TextPrimary = Colors.White;
        TextSecondary = (Color)ColorConverter.ConvertFromString("#d1d5f9")!;
        TextMuted = (Color)ColorConverter.ConvertFromString("#9ca3af")!;
        TextDark = (Color)ColorConverter.ConvertFromString("#6b7280")!;
    }

    /// <summary>
    /// Создаёт копию текущих настроек
    /// </summary>
    public ThemeColors Clone()
    {
        return new ThemeColors
        {
            BackgroundMain = this.BackgroundMain,
            BackgroundPanel = this.BackgroundPanel,
            BackgroundCard = this.BackgroundCard,
            BackgroundInput = this.BackgroundInput,
            BackgroundHover = this.BackgroundHover,
            Border = this.Border,
            Accent = this.Accent,
            AccentHover = this.AccentHover,
            AccentLight = this.AccentLight,
            ButtonSecondary = this.ButtonSecondary,
            Success = this.Success,
            Warning = this.Warning,
            Error = this.Error,
            Info = this.Info,
            TextPrimary = this.TextPrimary,
            TextSecondary = this.TextSecondary,
            TextMuted = this.TextMuted,
            TextDark = this.TextDark
        };
    }

    /// <summary>
    /// Копирует значения из другой темы
    /// </summary>
    public void CopyFrom(ThemeColors other)
    {
        BackgroundMain = other.BackgroundMain;
        BackgroundPanel = other.BackgroundPanel;
        BackgroundCard = other.BackgroundCard;
        BackgroundInput = other.BackgroundInput;
        BackgroundHover = other.BackgroundHover;
        Border = other.Border;
        Accent = other.Accent;
        AccentHover = other.AccentHover;
        AccentLight = other.AccentLight;
        ButtonSecondary = other.ButtonSecondary;
        Success = other.Success;
        Warning = other.Warning;
        Error = other.Error;
        Info = other.Info;
        TextPrimary = other.TextPrimary;
        TextSecondary = other.TextSecondary;
        TextMuted = other.TextMuted;
        TextDark = other.TextDark;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
