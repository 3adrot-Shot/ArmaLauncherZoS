namespace ArmaLauncherClient.Models;

/// <summary>
/// Настройки UI (персонаж, тема, слайдшоу)
/// </summary>
public class UiSettings
{
    // Персонаж
    public bool CharacterEnabled { get; set; } = true;
    public double CharacterOpacity { get; set; } = 0.7;
    public int CharacterMaxHeight { get; set; } = 600;
    public int CharacterOffsetX { get; set; } = 350;
    public int CharacterOffsetY { get; set; } = 150;
    public string? CharacterImagePath { get; set; } = "/Images/Back/ar3zka.png";
    
    // Язык
    public string Language { get; set; } = "ru";

    // Мгновенная установка — начинать скачивание файла сразу после его анализа
    public bool InstantInstall { get; set; } = false;

    // Слайдшоу
    public bool SlideshowEnabled { get; set; } = true;
    public int SlideshowInterval { get; set; } = 5;
    
    // Тема - фон
    public string? ThemeBackgroundMain { get; set; }
    public string? ThemeBackgroundPanel { get; set; }
    public string? ThemeBackgroundCard { get; set; }
    public string? ThemeBackgroundInput { get; set; }
    public string? ThemeBackgroundHover { get; set; }
    public string? ThemeBorder { get; set; }
    
    // Тема - акценты
    public string? ThemeAccent { get; set; }
    public string? ThemeAccentHover { get; set; }
    public string? ThemeAccentLight { get; set; }
    public string? ThemeButtonSecondary { get; set; }
    
    // Тема - статусы
    public string? ThemeSuccess { get; set; }
    public string? ThemeWarning { get; set; }
    public string? ThemeError { get; set; }
    public string? ThemeInfo { get; set; }
    
    // Тема - текст
    public string? ThemeTextPrimary { get; set; }
    public string? ThemeTextSecondary { get; set; }
    public string? ThemeTextMuted { get; set; }
    public string? ThemeTextDark { get; set; }
}
