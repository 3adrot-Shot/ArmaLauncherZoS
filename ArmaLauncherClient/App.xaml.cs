using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using ArmaLauncherClient.Services;
using ArmaLauncherClient.UC;
using ArmaLauncherClient.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArmaLauncherClient;

public partial class App : Application
{
    // Список доступных серверов
    public static readonly List<ServerInfo> AvailableServers =
    [
        // Площадка ZoS 1
        new ServerInfo { Name = "ZoS DC1 RU1", Url = "http://85.236.0.72:5000" },
        new ServerInfo { Name = "ZoS DC1 RU2", Url = "http://94.230.14.150:5000" },
        new ServerInfo { Name = "ZoS DC1 RU3", Url = "http://85.236.0.71:5000" },
        new ServerInfo { Name = "ZoS DC1 RU4", Url = "http://94.230.14.151:5000" },

        //Площадка ZoS 2
         new ServerInfo { Name = "ZoS DC2 RU1", Url = "http://79.98.208.7:5000" },

        // Площадка Always 1
        new ServerInfo { Name = "Always Msk 1", Url = "http://87.251.78.224:5000" },
        new ServerInfo { Name = "Always Germany 1", Url = "http://85.137.253.145:5000" },
        new ServerInfo { Name = "Always Germany 2", Url = "http://85.137.253.158:5000" }
    ];
    
    // Текущий выбранный сервер (по умолчанию первый)
    public static ServerInfo CurrentServer { get; set; } = AvailableServers[1];
    
    // HttpClient for general API calls
    public static HttpClient HttpClient { get; } = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    
    private IHost? _host;

    public App()
    {
        // Set up global exception handlers FIRST
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        
        try
        {
            // Initialize file logger
            FileLogger.Initialize();
            FileLogger.Log("=== Application starting ===");
            
            // Log basic system info
            FileLogger.Log($"OS: {Environment.OSVersion}");
            FileLogger.Log($".NET: {Environment.Version}");
            FileLogger.Log($"64-bit: {Environment.Is64BitProcess}");
            FileLogger.Log($"Working Dir: {Environment.CurrentDirectory}");
        }
        catch (Exception ex)
        {
            // Even logging failed - write to desktop
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), 
                    "ArmaLauncher_CriticalError.txt");
                File.WriteAllText(path, $"Failed to initialize logger: {ex}");
            }
            catch { }
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        HandleFatalException(e.ExceptionObject as Exception ?? new Exception("Unknown error"), "AppDomain.UnhandledException");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatalException(e.Exception, "Dispatcher.UnhandledException");
        e.Handled = true; // Prevent default crash dialog
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLogger.Error("UnobservedTaskException", e.Exception);
        e.SetObserved(); // Prevent process termination
    }

    private void HandleFatalException(Exception ex, string source)
    {
        try
        {
            FileLogger.Error($"FATAL ERROR ({source})", ex);
            
            // Generate crash report with diagnostics
            var report = StartupDiagnostics.GetCrashReport(ex);
            
            // Save to desktop
            var reportPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"ArmaLauncher_Crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            
            File.WriteAllText(reportPath, report);
            FileLogger.Log($"Crash report saved: {reportPath}");
            
            // Show user-friendly error
            var message = GetUserFriendlyError(ex);
            message += $"\n\nПодробный отчёт сохранён на рабочий стол:\n{reportPath}";
            
            MessageBox.Show(message, "ArmaLauncher - Критическая ошибка", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception innerEx)
        {
            // Last resort
            MessageBox.Show(
                $"Критическая ошибка приложения.\n\n{ex.Message}\n\nНе удалось создать отчёт: {innerEx.Message}",
                "ArmaLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetUserFriendlyError(Exception ex)
    {
        var msg = ex.ToString().ToLowerInvariant();
        
        if (msg.Contains("could not load file or assembly") || msg.Contains("typeloadexception"))
        {
            return "Ошибка: Не найдены необходимые компоненты .NET\n\n" +
                   "Решение: Установите .NET Desktop Runtime 8.0 или новее:\n" +
                   "https://dotnet.microsoft.com/download/dotnet/8.0\n\n" +
                   "Скачайте файл '.NET Desktop Runtime 8.0.x - Windows x64'";
        }
        
        if (msg.Contains("dllnotfoundexception") || (msg.Contains("dll") && msg.Contains("not found")))
        {
            return "Ошибка: Не найдена системная библиотека DLL\n\n" +
                   "Решение: Установите Visual C++ Redistributable:\n" +
                   "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        }
        
        if (msg.Contains("wpf") || msg.Contains("presentation"))
        {
            return "Ошибка: Проблема с графическими компонентами Windows\n\n" +
                   "Решение:\n" +
                   "1. Установите .NET Desktop Runtime (не просто .NET Runtime)\n" +
                   "2. Обновите драйверы видеокарты\n" +
                   "3. Попробуйте запустить: sfc /scannow (от имени администратора)";
        }
        
        if (msg.Contains("access") && msg.Contains("denied"))
        {
            return "Ошибка: Доступ запрещён\n\n" +
                   "Решение:\n" +
                   "1. Запустите программу от имени администратора\n" +
                   "2. Проверьте настройки антивируса\n" +
                   "3. Убедитесь, что программа не заблокирована";
        }
        
        if (msg.Contains("network") || msg.Contains("socket") || msg.Contains("connection"))
        {
            return "Ошибка: Проблема с сетью\n\n" +
                   "Решение:\n" +
                   "1. Проверьте подключение к интернету\n" +
                   "2. Отключите VPN если используется\n" +
                   "3. Проверьте настройки брандмауэра";
        }
        
        return $"Произошла ошибка: {ex.Message}\n\n" +
               "Проверьте отчёт на рабочем столе для подробностей.";
    }

    public void InitializeHost()
    {
        FileLogger.Log($"Initializing with server: {CurrentServer.Url}");

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Configure HttpClient with automatic decompression
                services.AddHttpClient("LauncherApi", client =>
                {
                    client.BaseAddress = new Uri(CurrentServer.Url);
                    client.DefaultRequestHeaders.Add("User-Agent", "ArmaLauncher/1.0");
                    client.Timeout = TimeSpan.FromMinutes(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    // Enable automatic decompression for GZip and Brotli
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
                });

                services.AddSingleton<DeduplicationCache>();
                services.AddSingleton<CryptoService>();

                services.AddSingleton<UpdateManager>(sp =>
                {
                    var factory = sp.GetRequiredService<IHttpClientFactory>();
                    var client = factory.CreateClient("LauncherApi");
                    return new UpdateManager(
                        client,
                        sp.GetRequiredService<DeduplicationCache>(),
                        sp.GetRequiredService<CryptoService>(),
                        CurrentServer.Url);
                });

                services.AddTransient<MainViewModel>(sp =>
                {
                    var factory = sp.GetRequiredService<IHttpClientFactory>();
                    var httpClient = factory.CreateClient("LauncherApi");
                    return new MainViewModel(
                        sp.GetRequiredService<UpdateManager>(),
                        sp.GetRequiredService<DeduplicationCache>(),
                        httpClient);
                });
                services.AddTransient<WindowMain>();
             })
             .Build();
     }
    
    public async Task SwitchServerAsync(ServerInfo newServer)
    {
        FileLogger.Log($"Switching server to: {newServer.Url}");
        
        // Dispose old host
        if (_host != null)
        {
            var oldVm = _host.Services.GetService<MainViewModel>();
            if (oldVm != null)
                await oldVm.DisposeAsync();
            
            await _host.StopAsync();
            _host.Dispose();
        }
        
        CurrentServer = newServer;
        
        // Create new host with new server
        InitializeHost();
        await _host!.StartAsync();
    }
    
    public T GetService<T>() where T : class => _host!.Services.GetRequiredService<T>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        FileLogger.Log("OnStartup");
        
        // Check for --diagnostics flag
        if (e.Args.Contains("--diagnostics") || e.Args.Contains("-d"))
        {
            var diagnostics = StartupDiagnostics.RunFullDiagnostics();
            StartupDiagnostics.SaveDiagnosticsToFile(diagnostics);
            MessageBox.Show("Диагностика сохранена на рабочем столе", "ArmaLauncher", 
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        
        try
        {
            var previousShutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Проверяем наличие Game на каждом сервере
            await CheckServersForGameAsync();
            
            // Выбираем первый сервер с игрой
            var firstWithGame = AvailableServers.FirstOrDefault(s => s.HasGame);
            if (firstWithGame != null)
            {
                CurrentServer = firstWithGame;
            }
            
            InitializeHost();
            await _host!.StartAsync();

            // Проверяем первый запуск (игра не установлена и пути дефолтные)
            var updateManager = _host.Services.GetRequiredService<UpdateManager>();
            if (ShouldShowFirstSetup(updateManager))
            {
                var (confirmed, gamePath, modsPath) = UC.FirstSetupDialog.Show(
                    updateManager.GameInstallRoot,
                    updateManager.ModsInstallRoot);

                if (confirmed)
                {
                    // Применяем выбранные пути
                    if (gamePath != updateManager.GameInstallRoot)
                    {
                        updateManager.SetGamePath(gamePath);
                        FileLogger.Log($"First setup: Game path set to {gamePath}");
                    }

                    if (modsPath != updateManager.ModsInstallRoot)
                    {
                        updateManager.SetModsPath(modsPath);
                        FileLogger.Log($"First setup: Mods path set to {modsPath}");
                    }

                    // Создаём маркер что настройка выполнена
                    var markerPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ArmaLauncher",
                        ".first_setup_done");
                    CreateFirstSetupMarker(markerPath);
                }
            }

            var mainWindow = _host.Services.GetRequiredService<WindowMain>();
            var viewModel = _host.Services.GetRequiredService<MainViewModel>();
            viewModel.LoadSettings(); // Загружаем UI настройки
            mainWindow.DataContext = viewModel;
            ShutdownMode = previousShutdownMode;
            mainWindow.Show();

            FileLogger.Log("Window shown");
        }
        catch (Exception ex)
        {
            FileLogger.Error("Startup failed", ex);
            HandleFatalException(ex, "OnStartup");
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    private static async Task CheckServersForGameAsync()
    {
        FileLogger.Log("Checking servers for game availability...");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var tasks = AvailableServers.Select(async server =>
        {
            try
            {
                var response = await httpClient.GetAsync($"{server.Url}/info");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    // Проверяем наличие поля game в ответе
                    server.HasGame = json.Contains("\"game\"") && !json.Contains("\"game\":null");
                }
                else
                {
                    server.HasGame = false;
                }
            }
            catch
            {
                server.HasGame = false;
            }

            FileLogger.Log($"Server {server.Name}: HasGame={server.HasGame}");
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Проверяет нужно ли показать диалог первой настройки
    /// </summary>
    private static bool ShouldShowFirstSetup(UpdateManager updateManager)
    {
        // Проверяем файл маркера первого запуска
        var markerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArmaLauncher",
            ".first_setup_done");

        if (File.Exists(markerPath))
        {
            return false; // Настройка уже была выполнена
        }

        // Проверяем установлена ли игра
        var gameInstallPath = updateManager.GameInstallRoot;
        var gameExePath = Path.Combine(gameInstallPath, "ArmaReforgerSteam.exe");

        if (File.Exists(gameExePath))
        {
            // Игра уже установлена - создаём маркер и не показываем диалог
            CreateFirstSetupMarker(markerPath);
            return false;
        }

        // Проверяем есть ли уже скачанные моды
        var modsPath = updateManager.ModsInstallRoot;
        if (Directory.Exists(modsPath) && Directory.GetDirectories(modsPath).Length > 0)
        {
            // Моды уже есть - создаём маркер и не показываем диалог
            CreateFirstSetupMarker(markerPath);
            return false;
        }

        // Первый запуск - показываем диалог
        FileLogger.Log("First setup detected - showing setup dialog");
        return true;
    }

    private static void CreateFirstSetupMarker(string markerPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(markerPath, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            FileLogger.Log($"Failed to create first setup marker: {ex.Message}");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        FileLogger.Log("OnExit");
        
        if (_host != null)
        {
            try
            {
                var vm = _host.Services.GetService<MainViewModel>();
                if (vm != null) await vm.DisposeAsync();

                await _host.StopAsync();
                _host.Dispose();
            }
            catch (Exception ex)
            {
                FileLogger.Error("Error during shutdown", ex);
            }
        }
        
        FileLogger.Log("Exited");
        base.OnExit(e);
    }
}

public class ServerInfo
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public bool HasGame { get; set; } = true; // Will be checked at runtime
    
    public override string ToString() => $"{Name} ({Url})";
}
