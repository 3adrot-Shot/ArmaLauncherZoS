using System.ComponentModel;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Manages UI localization strings for Russian and English languages.
/// Singleton with INotifyPropertyChanged — all XAML bindings auto-refresh on language switch.
/// </summary>
public class LocalizationManager : INotifyPropertyChanged
{
    public static LocalizationManager Instance { get; } = new();

    private string _currentLanguage = "ru";

    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage == value) return;
            _currentLanguage = value;
            // Notify ALL bindings (including indexer) to refresh
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    public string this[string key] =>
        _translations.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var value)
            ? value
            : key;

    /// <summary>
    /// Static shorthand for C# code: Loc.S("key")
    /// </summary>
    public static string S(string key) => Instance[key];

    /// <summary>
    /// Static shorthand with format: Loc.F("key", arg1, arg2)
    /// </summary>
    public static string F(string key, params object[] args) => string.Format(Instance[key], args);

    public event PropertyChangedEventHandler? PropertyChanged;

    public static readonly string[] SupportedLanguages = ["ru", "en"];
    public static readonly string[] LanguageDisplayNames = ["Русский", "English"];

    private readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["ru"] = new Dictionary<string, string>
        {
            // ===== Navigation =====
            ["nav_game"] = "Игра",
            ["nav_news"] = "Новости",
            ["nav_mods"] = "Моды",
            ["nav_servers"] = "Серверы",
            ["nav_settings"] = "Настройки",
            ["nav_about"] = "О программе",

            // ===== Game Section =====
            ["go_to_site"] = "Перейти на сайт →",
            ["btn_verify"] = "✓ Проверить",
            ["btn_refresh_catalog"] = "Обновить каталог",
            ["game_action_install"] = "Установить",
            ["game_action_update"] = "Обновить",
            ["game_action_play"] = "Играть",
            ["game_not_selected"] = "Игра не выбрана. Нажмите \"Обновить каталог\".",
            ["game_not_installed"] = "Игра не установлена.",
            ["server_no_mods"] = "Без модов",
            ["server_mods_count"] = "{0} модов",

            // ===== Players/Mods Dialogs =====
            ["players_count"] = "{0} игроков",
            ["players_online_title"] = "Игроки онлайн ({0}/{1})",
            ["mods_on_server_title"] = "📦 Моды на сервере ({0})",
            ["addons_count"] = "{0} аддонов",
            ["addons_on_server_title"] = "📦 Аддоны на сервере ({0})",
            ["total_size"] = "Общий размер: {0}",

            // ===== News Section =====
            ["news_title"] = "📰 Новости",
            ["btn_refresh"] = "↻ Обновить",
            ["news_loading"] = "Загрузка новостей...",

            // ===== Mods Section =====
            ["mods_server_filter"] = "Фильтр по серверу",
            ["mods_select_server"] = "Выберите сервер для проверки совместимости модов",
            ["mods_installed"] = "Установлено",
            ["mods_missing"] = "Отсутствует",
            ["mods_version_ne"] = "Версия ≠",
            ["btn_download_missing"] = "📥 Скачать недостающие",
            ["mods_suffix"] = " модов",
            ["btn_verify_all"] = "🔍 Проверить все",
            ["btn_verify_all_tooltip"] = "Проверить файлы всех установленных модов",
            ["stats_total"] = "Всего: ",
            ["stats_downloaded"] = "Скачано: ",
            ["stats_needed"] = "Нужно: ",
            ["mods_catalog_title"] = "📦 Каталог всех модов",
            ["btn_install_all"] = "📥 Установить все",
            ["btn_install_all_tooltip"] = "Скачать и установить все моды",
            ["btn_verify_all_integrity"] = "✓ Проверить все",
            ["btn_verify_all_tooltip2"] = "Проверить целостность всех модов",
            ["btn_refresh_list"] = "Обновить список",
            ["mods_empty"] = "Нет доступных модов",
            ["mods_refresh_catalog"] = "↻ Обновить каталог",
            ["btn_download"] = "📥 Скачать",
            ["btn_verify_short"] = "Проверить",
            ["mods_download_tooltip"] = "Скачать мод",
            ["mods_clear_filter"] = "Сбросить фильтр",
            ["mods_your_version"] = "У вас: v",
            ["mods_required_version"] = "Нужно: v",
            ["mods_requires_version"] = "Требуется: v",
            ["mods_files_suffix"] = " файлов",

            // ===== Servers Section =====
            ["servers_title"] = "🖥 Мониторинг серверов",
            ["servers_name"] = "Название",
            ["servers_online"] = "Онлайн",
            ["servers_scenario"] = "Сценарий",
            ["servers_version"] = "Версия",
            ["servers_mods"] = "Моды",
            ["servers_chart"] = "График",
            ["servers_loading"] = "Загрузка серверов...",

            // ===== Settings Section =====
            ["settings_server"] = "🌐 Сервер",
            ["settings_download_method"] = "⚡ Метод загрузки",
            ["settings_instant_install"] = "⚡ Мгновенная установка",
            ["settings_instant_install_desc"] = "Начинать скачивание файла сразу после его анализа, не дожидаясь проверки всех файлов (быстрее старт при обновлении)",
            ["settings_game_path"] = "🎮 Путь к игре",
            ["settings_game_path_desc"] = "Укажите папку с игрой или куда установить игру",
            ["btn_browse_tooltip"] = "Выбрать папку",
            ["btn_detect_tooltip"] = "Найти существующую игру",
            ["btn_open"] = "📂 Открыть",
            ["btn_reset"] = "↩ Сбросить",
            ["settings_mods_path"] = "📦 Путь к модам (Addons)",
            ["settings_mods_path_desc"] = "Папка для скачивания модификаций",
            ["settings_launch_params"] = "🚀 Параметры запуска",
            ["settings_launch_desc"] = "Настройки для запуска игры с модификациями",
            ["settings_use_mods_path"] = "Использовать путь к модам при запуске",
            ["settings_auto_params"] = "Автоматически добавляет -addonsDir и -addonDownloadDir",
            ["settings_launch_params_label"] = "Параметры запуска:",
            ["settings_appearance"] = "🎨 Оформление",
            ["settings_appearance_desc"] = "Настройки внешнего вида лаунчера",
            ["btn_advanced"] = "⚙ Продвинутое",
            ["settings_slideshow"] = "Слайдшоу фоновых изображений",
            ["settings_slideshow_desc"] = "Автоматическая смена фона с плавным переходом",
            ["settings_slideshow_interval"] = "Интервал смены (сек):",
            ["settings_show_logo"] = "Показывать лого на фоне",
            ["settings_logo_desc"] = "Изображение поверх фонового слайдшоу",
            ["settings_tools"] = "🔧 Инструменты",
            ["btn_diagnostics"] = "🔍 Диагностика",
            ["btn_test_all_servers"] = "🌐 Тест всех серверов",
            ["btn_refresh_catalog2"] = "↻ Обновить каталог",
            ["settings_language"] = "🌍 Язык / Language",

            // ===== Progress =====
            ["progress_remaining"] = "Осталось: ",
            ["btn_cancel"] = "✕ Отмена",
            ["console_tooltip"] = "Открыть консоль логов",

            // ===== Advanced Settings Dialog =====
            ["advanced_title"] = "Продвинутые настройки",
            ["advanced_logo"] = "🎭 Логотип",
            ["advanced_show_logo"] = "Показывать логотип на фоне",
            ["advanced_image"] = "Изображение:",
            ["advanced_opacity"] = "Прозрачность:",
            ["advanced_max_height"] = "Макс. высота:",
            ["advanced_offset_x"] = "Смещение X:",
            ["advanced_offset_y"] = "Смещение Y:",
            ["advanced_offset_hint"] = "X: влево (-) / вправо (+)  •  Y: вверх (-) / вниз (+)",
            ["btn_save"] = "💾 Сохранить",
            ["btn_reset2"] = "↩ Сбросить",

            // ===== Console Log Dialog =====
            ["console_title"] = "Консоль логов",
            ["console_lines"] = " строк",
            ["console_autoscroll"] = "Автопрокрутка",
            ["btn_clear"] = "🗑️ Очистить",
            ["btn_open_file"] = "📂 Открыть файл",
            ["btn_close"] = "Закрыть",

            // ===== About Dialog =====
            ["about_title"] = "О лаунчере",
            ["about_hello"] = "Здравствуй!",
            ["about_welcome_text"] = "Если ты видишь это сообщение, то очевидно ты скачал наш лаунчер, и мне очень важно чтобы тебе было им комфортно пользоваться без всяких VPN и проблем.",
            ["about_dev_title"] = "🔥 О разработке",
            ["about_dev_text1"] = "Лаунчер разработан человеком под псевдонимом",
            ["about_dev_flame"] = "\"Пламя\"",
            ["about_dev_text2"] = "при поддержке",
            ["about_dev_zos"] = "\"Zone of Survival (ZoS)\"",
            ["about_dev_text3"] = "и на данный момент проходит этап унифицирования для удобства.",
            ["about_dev_text4"] = "Я пытаюсь сделать это приложение как можно лучше, чтобы было меньше вылетов, багов и был удобный интерфейс — чтобы даже в игру хотелось заходить через лаунчер.",
            ["about_feedback_title"] = "💬 Обратная связь",
            ["about_feedback_text1"] = "В случае возникновения проблем, даже по небольшой части интерфейса, или вы видите часть интерфейса несуразной, неудобной, не интуитивной — прошу отписать мне!",
            ["about_feedback_text2"] = "Буду очень рад если в случае возникновения проблем и даже вопросов вы будете отписывать мне. Я открыт вопросам, если это хотя бы немного уместно.",
            ["about_telegram"] = "Написать в Telegram: @Zadrotix_dev",
            ["about_support_title"] = "❤️ Поддержка",
            ["about_support_text"] = "Очень буду рад если вы меня поддержите! Лаунчер будет обновляться по мере моей нагрузки на досуге.",
            ["about_enjoy"] = "Приятного пользования лаунчером! 🎮",
            ["about_copyright"] = "© 2026 Пламя & Zone of Survival",

            // ===== First Setup Dialog =====
            ["setup_title"] = "Первоначальная настройка",
            ["setup_welcome"] = "Добро пожаловать",
            ["setup_select_folders"] = "Выберите папки для установки",
            ["setup_ssd_hint"] = "Рекомендуется использовать SSD диск для лучшей производительности",
            ["setup_game_folder"] = "Папка для игры",
            ["setup_game_size"] = "Сюда будет установлена Arma Reforger (~26 ГБ)",
            ["setup_mods_folder"] = "Папка для модов",
            ["setup_mods_size"] = "Сюда будут скачиваться модификации (размер зависит от выбранных модов)",
            ["setup_same_folder"] = "Использовать одну папку для игры и модов",
            ["setup_same_folder_desc"] = "Моды будут в подпапке Addons рядом с игрой",
            ["btn_use_default"] = "Использовать по умолчанию",
            ["btn_continue"] = "✓ Продолжить",

            // ===== Players Dialog =====
            ["players_title"] = "Игроки на сервере",
            ["players_empty"] = "На сервере нет игроков",
            ["players_suffix"] = " игроков",

            // ===== Mods Dialog =====
            ["mods_on_server"] = "Моды на сервере",
            ["mods_addons_on_server"] = "📦 Аддоны на сервере",
            ["mods_no_mods"] = "На сервере нет модов",
            ["mods_open_workshop"] = "Открыть в Workshop",

            // ===== Disk Space Dialog =====
            ["disk_title"] = "Недостаточно места",
            ["disk_header"] = "Недостаточно места на диске",
            ["disk_message"] = "Не удалось установить мод",
            ["disk_label"] = "Диск:",
            ["disk_free"] = "Свободно:",
            ["disk_required"] = "Требуется:",
            ["disk_recommendations"] = "Рекомендации:",
            ["disk_tip1"] = "• Удалите ненужные файлы с диска",
            ["disk_tip2"] = "• Очистите корзину",
            ["disk_tip3"] = "• Измените путь установки в настройках",
            ["btn_settings"] = "⚙ Настройки",

            // ===== Verify Result Dialog =====
            ["verify_title"] = "Результат проверки",
            ["verify_found"] = "Найдены несоответствия",
            ["verify_summary"] = "Обнаружено {0} файлов с несоответствиями",
            ["verify_missing"] = "{0} отсутствуют",
            ["verify_size_mismatch"] = "{0} с неверным размером",
            ["verify_download_size"] = "Нужно скачать: {0}",
            ["verify_file_missing"] = "Отсутствует ({0})",
            ["verify_file_size"] = "Размер: {0} → {1}",
            ["verify_status_missing"] = "отсутствует",
            ["verify_status_wrong_size"] = "неверный размер",
            ["btn_fix"] = "🔧 Исправить",

            // ===== Mods Verify Result Dialog =====
            ["mods_verify_title"] = "Результат проверки модов",
            ["mods_verify_damaged"] = "✕ Повреждено",
            ["mods_verify_not_installed"] = "○ Не установлено",
            ["mods_verify_select_all"] = "Выбрать все повреждённые моды",
            ["mods_verify_selected"] = " выбрано",
            ["mods_verify_all_ok"] = "Все моды в порядке",
            ["mods_verify_all_ok_detail"] = "Проверено {0} модов - все файлы корректны",
            ["mods_verify_found_damaged"] = "Найдено {0} повреждённых модов",
            ["btn_repair"] = "🔧 Починить выбранные",

            // ===== Server Stats Dialog =====
            ["stats_online_title"] = "📊 Статистика онлайна",
            ["stats_loading"] = "Загрузка данных...",
            ["stats_error"] = "Не удалось загрузить данные",
            ["stats_players"] = "Игроков: ",
            ["stats_zoom"] = "Масштаб: {0}%",
            ["stats_zoom_hint"] = "Колесико мыши для масштаба",
            ["stats_address"] = "Адрес: {0}:{1}",

            // ===== Server Mods Status Dialog =====
            ["server_mods_compat"] = "🔍 Совместимость модов",
            ["server_mods_status_ok"] = "✓ Установлен и совместим",
            ["server_mods_status_missing"] = "✕ Мод не установлен",
            ["server_mods_status_download"] = "📥 Доступен для скачивания",
            ["server_mods_status_version"] = "⚠ Версии не совпадают",
            ["server_mods_verify_tooltip"] = "Проверить файлы",
            ["server_mods_download_tooltip"] = "Скачать мод",

            // ===== Color Picker =====
            ["color_quick_select"] = "Быстрый выбор",
            ["color_cancel"] = "Отмена",
            ["color_apply"] = "Применить",

            // ===== All Servers SpeedTest Dialog =====
            ["speedtest_title"] = "SpeedTest всех серверов",
            ["speedtest_header"] = "⚡ SpeedTest серверов",
            ["speedtest_press_start"] = "Нажмите 'Начать' для запуска",
            ["speedtest_best"] = "🏆 Лучший сервер",
            ["speedtest_use"] = "Использовать",
            ["btn_start"] = "▶ Начать",

            // ===== Notification Dialog =====
            ["notification_title"] = "Уведомление",

            // ===== ViewModel Status Messages =====
            ["status_update_available"] = "Доступно обновление: v{0}",
            ["status_installed_version"] = "Установлена версия v{0}",
            ["status_checking_folder"] = "Проверка папки...",
            ["status_game_found_no_ver"] = "Найдены файлы игры, но нет информации о версии",
            ["status_game_update_needed"] = "Найдена игра v{0}, требуется обновление",
            ["status_game_up_to_date"] = "Найдена актуальная версия игры v{0}",
            ["status_game_not_found"] = "Файлы игры не найдены в указанной папке",
            ["status_game_unknown_ver"] = "✖ Найдены файлы игры, но версия неизвестна. Рекомендуется переустановка или проверка.",
            ["status_game_update"] = "✖ Найдена v{0} → доступно обновление до v{1}",
            ["status_game_current"] = "✔ Найдена v{0} (актуальная версия)",
            ["status_no_mods_download"] = "Нет модов для скачивания",
            ["status_downloading"] = "Скачивание {0}/{1}: {2}...",
            ["status_cancelled_mods"] = "Отменено. Установлено {0}/{1} модов",
            ["status_installed_mods"] = "Установлено {0} модов",
            ["status_all_compatible"] = "✓ Все совместимы",
            ["status_need_mods"] = "⚠ Нужно {0} мод(ов)",
            ["status_version_mismatch"] = "⚠ {0} с другой версией",
            ["status_loading_news"] = "Загрузка новостей...",
            ["status_loaded_news"] = "Загружено {0} новостей",
            ["status_no_news"] = "Новости не найдены",
            ["status_news_error"] = "Ошибка загрузки новостей",
            ["status_loading_servers"] = "Загрузка серверов...",
            ["status_loaded_servers"] = "Найдено {0} серверов, игроков онлайн: {1}",
            ["status_no_servers"] = "Серверы не найдены",
            ["status_servers_error"] = "Ошибка загрузки серверов",
            ["status_no_addons"] = "Нет аддонов для установки",
            ["status_no_addons_verify"] = "Нет аддонов для проверки",
            ["status_no_mods_verify"] = "Нет модов для проверки",
            ["update_notify_title"] = "Доступны обновления",
            ["update_notify_game_and_addons"] = "Вышло обновление игры и {0} мод(ов). Рекомендуется обновить.",
            ["update_notify_game"] = "Вышло обновление игры. Рекомендуется обновить.",
            ["update_notify_addons"] = "Доступны обновления для {0} мод(ов). Рекомендуется обновить.",
            ["status_updates_available"] = "⬆ Доступны обновления: {0}",
            ["status_check_addon"] = "Проверка {0}/{1}: {2}",
            ["status_addon_current"] = "Проверка {0}/{1}: {2} - актуален ✓",
            ["status_disk_error"] = "Ошибка: недостаточно места на диске",
            ["status_game_exe_missing"] = "Файл ArmaReforgerSteam.exe не найден. Выполните установку.",
            ["status_game_launched_params"] = "Игра запущена с параметрами модов (PID: {0})",
            ["status_game_launched"] = "Игра запущена",
            ["status_game_starting"] = "Игра запускается...",
            ["status_game_launch_error"] = "Не удалось запустить игру: {0}",
            ["status_folder_error"] = "Не удается открыть папку: {0}",
            ["status_verify_mods"] = "Проверка {0} модов...",
            ["status_verify_mod"] = "Проверка: {0}...",
            ["status_verify_all_ok"] = "✓ Все {0} модов прошли проверку",
            ["status_verify_result"] = "Проверено: ✓ {0} OK, ✕ {1} повреждено",
            ["status_repairing"] = "Восстановление {0} модов...",
            ["status_install_count"] = "✓ {0} установлено",
            ["status_skip_count"] = "○ {0} пропущено",
            ["status_fail_count"] = "✕ {0} ошибок",
            ["status_cancelled_processed"] = "Отменено. Обработано {0}/{1}: {2}",
            ["status_processed"] = "Обработано {0} аддонов: {1}",
            ["status_not_installed_count"] = "○ {0} не установлено",
            ["status_invalid_count"] = "✕ {0} повреждено",
            ["status_verified"] = "Проверено {0} аддонов: {1}",
            ["action_update"] = "Обновление",
            ["action_install"] = "Установка",
            ["status_update_version"] = "Обновление: v{0}",

            // ===== ServerModStatusVM =====
            ["mod_status_newer"] = "Новее",
            ["mod_status_outdated"] = "Устарел",
            ["mod_status_download"] = "Скачать",
            ["mod_status_missing"] = "Нет",

            // ===== App Error Messages =====
            ["error_report_saved"] = "\n\nПодробный отчёт сохранён на рабочий стол:\n{0}",
            ["error_critical_title"] = "ArmaLauncher - Критическая ошибка",
            ["error_critical_fallback"] = "Критическая ошибка приложения.\n\n{0}\n\nНе удалось создать отчёт: {1}",
            ["error_dotnet"] = "Ошибка: Не найдены необходимые компоненты .NET\n\nРешение: Установите .NET Desktop Runtime 8.0 или новее:\nhttps://dotnet.microsoft.com/download/dotnet/8.0\n\nСкачайте файл '.NET Desktop Runtime 8.0.x - Windows x64'",
            ["error_dll"] = "Ошибка: Не найдена системная библиотека DLL\n\nРешение: Установите Visual C++ Redistributable:\nhttps://aka.ms/vs/17/release/vc_redist.x64.exe",
            ["error_wpf"] = "Ошибка: Проблема с графическими компонентами Windows\n\nРешение:\n1. Установите .NET Desktop Runtime (не просто .NET Runtime)\n2. Обновите драйверы видеокарты\n3. Попробуйте запустить: sfc /scannow (от имени администратора)",
            ["error_access"] = "Ошибка: Доступ запрещён\n\nРешение:\n1. Запустите программу от имени администратора\n2. Проверьте настройки антивируса\n3. Убедитесь, что программа не заблокирована",
            ["error_network"] = "Ошибка: Проблема с сетью\n\nРешение:\n1. Проверьте подключение к интернету\n2. Отключите VPN если используется\n3. Проверьте настройки брандмауэра",
            ["error_generic"] = "Произошла ошибка: {0}\n\nПроверьте отчёт на рабочем столе для подробностей.",
            ["diag_saved"] = "Диагностика сохранена на рабочем столе",
            ["error_launch_title"] = "Ошибка запуска",
            ["error_launch_msg"] = "Не удалось запустить игру:\n{0}",
            ["error_install_title"] = "Ошибка установки",
            ["error_install_msg"] = "Не удалось установить {0}:\n{1}\n\nФайлы не были обновлены. Попробуйте запустить установку ещё раз.",

            // ===== Code-behind messages =====
            ["disk_free_space"] = "Свободно: {0}",
            ["setup_select_game"] = "Выберите папку для игры",
            ["setup_select_mods"] = "Выберите папку для модов",
            ["setup_error_create"] = "Не удалось создать папки:\n{0}",
            ["setup_error_title"] = "Ошибка",
            ["disk_install_failed"] = "Не удалось установить:\n{0}",
            ["mods_count_format"] = "{0} модов",
            ["mods_title_format"] = "📦 Моды на сервере ({0})",
            ["addons_count_format"] = "{0} аддонов",
            ["addons_title_format"] = "📦 Аддоны на сервере ({0})",
            ["players_count_format"] = "{0} игроков",
            ["players_title_format"] = "Игроки онлайн ({0}/{1})",
            ["server_mods_title_format"] = "🔍 Моды на сервере ({0})",
            ["diag_message"] = "Диагностика системы сохранена на рабочий стол.\n\nОтправьте этот файл разработчику для анализа проблемы.",
            ["diag_title"] = "Диагностика",
            ["diag_error"] = "Ошибка при создании диагностики: {0}",
            ["console_lines_format"] = " ({0} строк)",
            ["console_log_error"] = "Ошибка загрузки логов: {0}",
            ["console_skip_lines"] = "... (пропущено {0} строк) ...\n\n",
            ["stats_address_format"] = "Адрес: {0}:{1}",
            ["stats_zoom_format"] = "Масштаб: {0}%",
        },

        ["en"] = new Dictionary<string, string>
        {
            // ===== Navigation =====
            ["nav_game"] = "Game",
            ["nav_news"] = "News",
            ["nav_mods"] = "Mods",
            ["nav_servers"] = "Servers",
            ["nav_settings"] = "Settings",
            ["nav_about"] = "About",

            // ===== Game Section =====
            ["go_to_site"] = "Go to website →",
            ["btn_verify"] = "✓ Verify",
            ["btn_refresh_catalog"] = "Refresh catalog",
            ["game_action_install"] = "Install",
            ["game_action_update"] = "Update",
            ["game_action_play"] = "Play",
            ["game_not_selected"] = "No game selected. Click \"Refresh catalog\".",
            ["game_not_installed"] = "Game not installed.",
            ["server_no_mods"] = "No mods",
            ["server_mods_count"] = "{0} mods",

            // ===== Players/Mods Dialogs =====
            ["players_count"] = "{0} players",
            ["players_online_title"] = "Players online ({0}/{1})",
            ["mods_on_server_title"] = "📦 Mods on server ({0})",
            ["addons_count"] = "{0} addons",
            ["addons_on_server_title"] = "📦 Addons on server ({0})",
            ["total_size"] = "Total size: {0}",

            // ===== News Section =====
            ["news_title"] = "📰 News",
            ["btn_refresh"] = "↻ Refresh",
            ["news_loading"] = "Loading news...",

            // ===== Mods Section =====
            ["mods_server_filter"] = "Server filter",
            ["mods_select_server"] = "Select server to check mod compatibility",
            ["mods_installed"] = "Installed",
            ["mods_missing"] = "Missing",
            ["mods_version_ne"] = "Version ≠",
            ["btn_download_missing"] = "📥 Download missing",
            ["mods_suffix"] = " mods",
            ["btn_verify_all"] = "🔍 Verify all",
            ["btn_verify_all_tooltip"] = "Verify files of all installed mods",
            ["stats_total"] = "Total: ",
            ["stats_downloaded"] = "Downloaded: ",
            ["stats_needed"] = "Needed: ",
            ["mods_catalog_title"] = "📦 All Mods Catalog",
            ["btn_install_all"] = "📥 Install all",
            ["btn_install_all_tooltip"] = "Download and install all mods",
            ["btn_verify_all_integrity"] = "✓ Verify all",
            ["btn_verify_all_tooltip2"] = "Verify integrity of all mods",
            ["btn_refresh_list"] = "Refresh list",
            ["mods_empty"] = "No mods available",
            ["mods_refresh_catalog"] = "↻ Refresh catalog",
            ["btn_download"] = "📥 Download",
            ["btn_verify_short"] = "Verify",
            ["mods_download_tooltip"] = "Download mod",
            ["mods_clear_filter"] = "Clear filter",
            ["mods_your_version"] = "Yours: v",
            ["mods_required_version"] = "Required: v",
            ["mods_requires_version"] = "Requires: v",
            ["mods_files_suffix"] = " files",

            // ===== Servers Section =====
            ["servers_title"] = "🖥 Server Monitoring",
            ["servers_name"] = "Name",
            ["servers_online"] = "Online",
            ["servers_scenario"] = "Scenario",
            ["servers_version"] = "Version",
            ["servers_mods"] = "Mods",
            ["servers_chart"] = "Chart",
            ["servers_loading"] = "Loading servers...",

            // ===== Settings Section =====
            ["settings_server"] = "🌐 Server",
            ["settings_download_method"] = "⚡ Download method",
            ["settings_instant_install"] = "⚡ Instant install",
            ["settings_instant_install_desc"] = "Start downloading a file right after it is analyzed, without waiting for all files to be checked (faster start on updates)",
            ["settings_game_path"] = "🎮 Game path",
            ["settings_game_path_desc"] = "Specify game folder or where to install",
            ["btn_browse_tooltip"] = "Browse folder",
            ["btn_detect_tooltip"] = "Find existing game",
            ["btn_open"] = "📂 Open",
            ["btn_reset"] = "↩ Reset",
            ["settings_mods_path"] = "📦 Mods path (Addons)",
            ["settings_mods_path_desc"] = "Folder for downloading mods",
            ["settings_launch_params"] = "🚀 Launch parameters",
            ["settings_launch_desc"] = "Settings for launching game with mods",
            ["settings_use_mods_path"] = "Use mods path when launching",
            ["settings_auto_params"] = "Automatically adds -addonsDir and -addonDownloadDir",
            ["settings_launch_params_label"] = "Launch parameters:",
            ["settings_appearance"] = "🎨 Appearance",
            ["settings_appearance_desc"] = "Launcher appearance settings",
            ["btn_advanced"] = "⚙ Advanced",
            ["settings_slideshow"] = "Background image slideshow",
            ["settings_slideshow_desc"] = "Automatic background change with smooth transition",
            ["settings_slideshow_interval"] = "Change interval (sec):",
            ["settings_show_logo"] = "Show logo on background",
            ["settings_logo_desc"] = "Image overlay on background slideshow",
            ["settings_tools"] = "🔧 Tools",
            ["btn_diagnostics"] = "🔍 Diagnostics",
            ["btn_test_all_servers"] = "🌐 Test all servers",
            ["btn_refresh_catalog2"] = "↻ Refresh catalog",
            ["settings_language"] = "🌍 Language",

            // ===== Progress =====
            ["progress_remaining"] = "Remaining: ",
            ["btn_cancel"] = "✕ Cancel",
            ["console_tooltip"] = "Open log console",

            // ===== Advanced Settings Dialog =====
            ["advanced_title"] = "Advanced Settings",
            ["advanced_logo"] = "🎭 Logo",
            ["advanced_show_logo"] = "Show logo on background",
            ["advanced_image"] = "Image:",
            ["advanced_opacity"] = "Opacity:",
            ["advanced_max_height"] = "Max height:",
            ["advanced_offset_x"] = "Offset X:",
            ["advanced_offset_y"] = "Offset Y:",
            ["advanced_offset_hint"] = "X: left (-) / right (+)  •  Y: up (-) / down (+)",
            ["btn_save"] = "💾 Save",
            ["btn_reset2"] = "↩ Reset",

            // ===== Console Log Dialog =====
            ["console_title"] = "Log Console",
            ["console_lines"] = " lines",
            ["console_autoscroll"] = "Auto-scroll",
            ["btn_clear"] = "🗑️ Clear",
            ["btn_open_file"] = "📂 Open file",
            ["btn_close"] = "Close",

            // ===== About Dialog =====
            ["about_title"] = "About Launcher",
            ["about_version"] = "Version 1.0.0",
            ["about_hello"] = "Hello!",
            ["about_welcome_text"] = "If you see this message, you've obviously downloaded our launcher, and it's very important to me that you can use it comfortably without any VPN or issues.",
            ["about_dev_title"] = "🔥 About Development",
            ["about_dev_text1"] = "The launcher was developed by a person under the pseudonym",
            ["about_dev_flame"] = "\"Flame\"",
            ["about_dev_text2"] = "with support from",
            ["about_dev_zos"] = "\"Zone of Survival (ZoS)\"",
            ["about_dev_text3"] = "and is currently undergoing a unification stage for convenience.",
            ["about_dev_text4"] = "I'm trying to make this app as good as possible — fewer crashes, bugs, and a convenient interface — so you'd even want to launch the game through the launcher.",
            ["about_feedback_title"] = "💬 Feedback",
            ["about_feedback_text1"] = "If you encounter any problems, even with a small part of the interface, or you find something awkward, inconvenient, or unintuitive — please write to me!",
            ["about_feedback_text2"] = "I'll be very happy if you reach out to me with any problems or questions. I'm open to questions, as long as they're at least somewhat relevant.",
            ["about_telegram"] = "Write on Telegram: @Zadrotix_dev",
            ["about_support_title"] = "❤️ Support",
            ["about_support_text"] = "I would be very happy if you support me! The launcher will be updated as my workload allows in my free time.",
            ["about_enjoy"] = "Enjoy using the launcher! 🎮",
            ["about_copyright"] = "© 2026 Flame & Zone of Survival",

            // ===== First Setup Dialog =====
            ["setup_title"] = "Initial Setup",
            ["setup_welcome"] = "Welcome",
            ["setup_select_folders"] = "Select folders for installation",
            ["setup_ssd_hint"] = "Using an SSD drive is recommended for better performance",
            ["setup_game_folder"] = "Game folder",
            ["setup_game_size"] = "Arma Reforger will be installed here (~26 GB)",
            ["setup_mods_folder"] = "Mods folder",
            ["setup_mods_size"] = "Mods will be downloaded here (size depends on selected mods)",
            ["setup_same_folder"] = "Use one folder for game and mods",
            ["setup_same_folder_desc"] = "Mods will be in Addons subfolder next to game",
            ["btn_use_default"] = "Use default",
            ["btn_continue"] = "✓ Continue",

            // ===== Players Dialog =====
            ["players_title"] = "Players on Server",
            ["players_empty"] = "No players on server",
            ["players_suffix"] = " players",

            // ===== Mods Dialog =====
            ["mods_on_server"] = "Mods on server",
            ["mods_addons_on_server"] = "📦 Addons on server",
            ["mods_no_mods"] = "No mods on server",
            ["mods_open_workshop"] = "Open in Workshop",

            // ===== Disk Space Dialog =====
            ["disk_title"] = "Not Enough Space",
            ["disk_header"] = "Not enough disk space",
            ["disk_message"] = "Failed to install mod",
            ["disk_label"] = "Disk:",
            ["disk_free"] = "Free:",
            ["disk_required"] = "Required:",
            ["disk_recommendations"] = "Recommendations:",
            ["disk_tip1"] = "• Delete unnecessary files from disk",
            ["disk_tip2"] = "• Empty recycle bin",
            ["disk_tip3"] = "• Change install path in settings",
            ["btn_settings"] = "⚙ Settings",

            // ===== Verify Result Dialog =====
            ["verify_title"] = "Verification Result",
            ["verify_found"] = "Inconsistencies found",
            ["verify_summary"] = "Found {0} files with inconsistencies",
            ["verify_missing"] = "{0} missing",
            ["verify_size_mismatch"] = "{0} with wrong size",
            ["verify_download_size"] = "Need to download: {0}",
            ["verify_file_missing"] = "Missing ({0})",
            ["verify_file_size"] = "Size: {0} → {1}",
            ["verify_status_missing"] = "missing",
            ["verify_status_wrong_size"] = "wrong size",
            ["btn_fix"] = "🔧 Fix",

            // ===== Mods Verify Result Dialog =====
            ["mods_verify_title"] = "Mods Verification Result",
            ["mods_verify_damaged"] = "✕ Damaged",
            ["mods_verify_not_installed"] = "○ Not installed",
            ["mods_verify_select_all"] = "Select all damaged mods",
            ["mods_verify_selected"] = " selected",
            ["mods_verify_all_ok"] = "All mods are OK",
            ["mods_verify_all_ok_detail"] = "Verified {0} mods - all files are correct",
            ["mods_verify_found_damaged"] = "Found {0} damaged mods",
            ["btn_repair"] = "🔧 Repair selected",

            // ===== Server Stats Dialog =====
            ["stats_online_title"] = "📊 Online Statistics",
            ["stats_loading"] = "Loading data...",
            ["stats_error"] = "Failed to load data",
            ["stats_players"] = "Players: ",
            ["stats_zoom"] = "Scale: {0}%",
            ["stats_zoom_hint"] = "Mouse wheel to zoom",
            ["stats_address"] = "Address: {0}:{1}",

            // ===== Server Mods Status Dialog =====
            ["server_mods_compat"] = "🔍 Mod Compatibility",
            ["server_mods_status_ok"] = "✓ Installed and compatible",
            ["server_mods_status_missing"] = "✕ Mod not installed",
            ["server_mods_status_download"] = "📥 Available for download",
            ["server_mods_status_version"] = "⚠ Version mismatch",
            ["server_mods_verify_tooltip"] = "Verify files",
            ["server_mods_download_tooltip"] = "Download mod",

            // ===== Color Picker =====
            ["color_quick_select"] = "Quick select",
            ["color_cancel"] = "Cancel",
            ["color_apply"] = "Apply",

            // ===== All Servers SpeedTest Dialog =====
            ["speedtest_title"] = "SpeedTest All Servers",
            ["speedtest_header"] = "⚡ Server SpeedTest",
            ["speedtest_press_start"] = "Press 'Start' to begin",
            ["speedtest_best"] = "🏆 Best Server",
            ["speedtest_use"] = "Use",
            ["btn_start"] = "▶ Start",

            // ===== Notification Dialog =====
            ["notification_title"] = "Notification",

            // ===== ViewModel Status Messages =====
            ["status_update_available"] = "Update available: v{0}",
            ["status_installed_version"] = "Installed version v{0}",
            ["status_checking_folder"] = "Checking folder...",
            ["status_game_found_no_ver"] = "Game files found, but no version info",
            ["status_game_update_needed"] = "Found game v{0}, update required",
            ["status_game_up_to_date"] = "Found up-to-date game v{0}",
            ["status_game_not_found"] = "Game files not found in specified folder",
            ["status_game_unknown_ver"] = "✖ Game files found, but version unknown. Reinstall or verify recommended.",
            ["status_game_update"] = "✖ Found v{0} → update available to v{1}",
            ["status_game_current"] = "✔ Found v{0} (up to date)",
            ["status_no_mods_download"] = "No mods to download",
            ["status_downloading"] = "Downloading {0}/{1}: {2}...",
            ["status_cancelled_mods"] = "Cancelled. Installed {0}/{1} mods",
            ["status_installed_mods"] = "Installed {0} mods",
            ["status_all_compatible"] = "✓ All compatible",
            ["status_need_mods"] = "⚠ Need {0} mod(s)",
            ["status_version_mismatch"] = "⚠ {0} with different version",
            ["status_loading_news"] = "Loading news...",
            ["status_loaded_news"] = "Loaded {0} news",
            ["status_no_news"] = "No news found",
            ["status_news_error"] = "Error loading news",
            ["status_loading_servers"] = "Loading servers...",
            ["status_loaded_servers"] = "Found {0} servers, players online: {1}",
            ["status_no_servers"] = "No servers found",
            ["status_servers_error"] = "Error loading servers",
            ["status_no_addons"] = "No addons to install",
            ["status_no_addons_verify"] = "No addons to verify",
            ["status_no_mods_verify"] = "No mods to verify",
            ["update_notify_title"] = "Updates available",
            ["update_notify_game_and_addons"] = "A game update and {0} mod update(s) are available. We recommend updating.",
            ["update_notify_game"] = "A game update is available. We recommend updating.",
            ["update_notify_addons"] = "Updates are available for {0} mod(s). We recommend updating.",
            ["status_updates_available"] = "⬆ Updates available: {0}",
            ["status_check_addon"] = "Checking {0}/{1}: {2}",
            ["status_addon_current"] = "Checking {0}/{1}: {2} - up to date ✓",
            ["status_disk_error"] = "Error: not enough disk space",
            ["status_game_exe_missing"] = "ArmaReforgerSteam.exe not found. Please install.",
            ["status_game_launched_params"] = "Game launched with mod parameters (PID: {0})",
            ["status_game_launched"] = "Game launched",
            ["status_game_starting"] = "Game starting...",
            ["status_game_launch_error"] = "Failed to launch game: {0}",
            ["status_folder_error"] = "Cannot open folder: {0}",
            ["status_verify_mods"] = "Verifying {0} mods...",
            ["status_verify_mod"] = "Verifying: {0}...",
            ["status_verify_all_ok"] = "✓ All {0} mods passed verification",
            ["status_verify_result"] = "Verified: ✓ {0} OK, ✕ {1} damaged",
            ["status_repairing"] = "Repairing {0} mods...",
            ["status_install_count"] = "✓ {0} installed",
            ["status_skip_count"] = "○ {0} skipped",
            ["status_fail_count"] = "✕ {0} failed",
            ["status_cancelled_processed"] = "Cancelled. Processed {0}/{1}: {2}",
            ["status_processed"] = "Processed {0} addons: {1}",
            ["status_not_installed_count"] = "○ {0} not installed",
            ["status_invalid_count"] = "✕ {0} damaged",
            ["status_verified"] = "Verified {0} addons: {1}",
            ["action_update"] = "Updating",
            ["action_install"] = "Installing",
            ["status_update_version"] = "Update: v{0}",

            // ===== ServerModStatusVM =====
            ["mod_status_newer"] = "Newer",
            ["mod_status_outdated"] = "Outdated",
            ["mod_status_download"] = "Download",
            ["mod_status_missing"] = "Missing",

            // ===== App Error Messages =====
            ["error_report_saved"] = "\n\nDetailed report saved to desktop:\n{0}",
            ["error_critical_title"] = "ArmaLauncher - Critical Error",
            ["error_critical_fallback"] = "Critical application error.\n\n{0}\n\nFailed to create report: {1}",
            ["error_dotnet"] = "Error: Required .NET components not found\n\nSolution: Install .NET Desktop Runtime 8.0 or newer:\nhttps://dotnet.microsoft.com/download/dotnet/8.0\n\nDownload '.NET Desktop Runtime 8.0.x - Windows x64'",
            ["error_dll"] = "Error: System DLL library not found\n\nSolution: Install Visual C++ Redistributable:\nhttps://aka.ms/vs/17/release/vc_redist.x64.exe",
            ["error_wpf"] = "Error: Windows graphics components issue\n\nSolution:\n1. Install .NET Desktop Runtime (not just .NET Runtime)\n2. Update graphics drivers\n3. Try running: sfc /scannow (as administrator)",
            ["error_access"] = "Error: Access denied\n\nSolution:\n1. Run program as administrator\n2. Check antivirus settings\n3. Make sure program is not blocked",
            ["error_network"] = "Error: Network issue\n\nSolution:\n1. Check internet connection\n2. Disable VPN if used\n3. Check firewall settings",
            ["error_generic"] = "An error occurred: {0}\n\nCheck the report on desktop for details.",
            ["diag_saved"] = "Diagnostics saved to desktop",
            ["error_launch_title"] = "Launch Error",
            ["error_launch_msg"] = "Failed to launch game:\n{0}",
            ["error_install_title"] = "Installation Error",
            ["error_install_msg"] = "Failed to install {0}:\n{1}\n\nFiles were not updated. Please try the installation again.",

            // ===== Code-behind messages =====
            ["disk_free_space"] = "Free: {0}",
            ["setup_select_game"] = "Select game folder",
            ["setup_select_mods"] = "Select mods folder",
            ["setup_error_create"] = "Failed to create folders:\n{0}",
            ["setup_error_title"] = "Error",
            ["disk_install_failed"] = "Failed to install:\n{0}",
            ["mods_count_format"] = "{0} mods",
            ["mods_title_format"] = "📦 Mods on server ({0})",
            ["addons_count_format"] = "{0} addons",
            ["addons_title_format"] = "📦 Addons on server ({0})",
            ["players_count_format"] = "{0} players",
            ["players_title_format"] = "Players online ({0}/{1})",
            ["server_mods_title_format"] = "🔍 Mods on server ({0})",
            ["diag_message"] = "System diagnostics saved to desktop.\n\nSend this file to the developer for analysis.",
            ["diag_title"] = "Diagnostics",
            ["diag_error"] = "Error creating diagnostics: {0}",
            ["console_lines_format"] = " ({0} lines)",
            ["console_log_error"] = "Error loading logs: {0}",
            ["console_skip_lines"] = "... (skipped {0} lines) ...\n\n",
            ["stats_address_format"] = "Address: {0}:{1}",
            ["stats_zoom_format"] = "Scale: {0}%",
        }
    };
}
