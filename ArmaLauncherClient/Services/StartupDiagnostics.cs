using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ArmaLauncherClient.Services;

/// <summary>
/// Startup diagnostics to help identify missing dependencies and system issues
/// </summary>
public static class StartupDiagnostics
{
    public static string RunFullDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ArmaLauncher Startup Diagnostics ===");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        
        // System Info
        sb.AppendLine("--- System Info ---");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"CPU Cores: {Environment.ProcessorCount}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
        sb.AppendLine();
        
        // .NET Runtime Check
        sb.AppendLine("--- .NET Runtime ---");
        CheckDotNetRuntime(sb);
        sb.AppendLine();
        
        // Visual C++ Redistributable
        sb.AppendLine("--- Visual C++ Redistributable ---");
        CheckVcRedist(sb);
        sb.AppendLine();
        
        // WebView2 Runtime (if used)
        sb.AppendLine("--- WebView2 Runtime ---");
        CheckWebView2(sb);
        sb.AppendLine();
        
        // Required DLLs
        sb.AppendLine("--- Required DLLs ---");
        CheckRequiredDlls(sb);
        sb.AppendLine();
        
        // Network
        sb.AppendLine("--- Network ---");
        CheckNetwork(sb);
        sb.AppendLine();
        
        // Disk Space
        sb.AppendLine("--- Disk Space ---");
        CheckDiskSpace(sb);
        sb.AppendLine();
        
        // Permissions
        sb.AppendLine("--- Permissions ---");
        CheckPermissions(sb);
        sb.AppendLine();
        
        // App Directory
        sb.AppendLine("--- Application ---");
        sb.AppendLine($"Executable: {Environment.ProcessPath}");
        sb.AppendLine($"Working Directory: {Environment.CurrentDirectory}");
        sb.AppendLine($"App Data: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");
        
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(assemblyLocation))
        {
            sb.AppendLine($"Assembly Location: {assemblyLocation}");
            var dir = Path.GetDirectoryName(assemblyLocation);
            if (dir != null)
            {
                sb.AppendLine($"Files in app directory:");
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*.dll").Take(20))
                    {
                        sb.AppendLine($"  {Path.GetFileName(file)}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Error listing files: {ex.Message}");
                }
            }
        }
        
        return sb.ToString();
    }

    private static void CheckDotNetRuntime(StringBuilder sb)
    {
        try
        {
            var version = Environment.Version;
            sb.AppendLine($"CLR Version: {version}");
            
            // Check if running on correct runtime
            if (version.Major < 8)
            {
                sb.AppendLine("WARNING: This app requires .NET 8.0 or later!");
                sb.AppendLine("Download from: https://dotnet.microsoft.com/download/dotnet/8.0");
            }
            else
            {
                sb.AppendLine("OK: .NET runtime version is sufficient");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error checking .NET: {ex.Message}");
        }
    }

    private static void CheckVcRedist(StringBuilder sb)
    {
        try
        {
            // Check for Visual C++ 2015-2022 Redistributable (x64)
            var vcKeys = new[]
            {
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64",
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"
            };

            bool found = false;
            foreach (var keyPath in vcKeys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                    if (key != null)
                    {
                        var installed = key.GetValue("Installed");
                        var version = key.GetValue("Version");
                        var major = key.GetValue("Major");
                        var minor = key.GetValue("Minor");
                        
                        if (installed != null && (int)installed == 1)
                        {
                            sb.AppendLine($"OK: VC++ Redistributable found");
                            sb.AppendLine($"   Version: {version}, Major: {major}, Minor: {minor}");
                            found = true;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!found)
            {
                sb.AppendLine("WARNING: Visual C++ Redistributable 2015-2022 (x64) not found!");
                sb.AppendLine("Download from: https://aka.ms/vs/17/release/vc_redist.x64.exe");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error checking VC++ Redist: {ex.Message}");
        }
    }

    private static void CheckWebView2(StringBuilder sb)
    {
        try
        {
            var webView2Keys = new[]
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
            };

            bool found = false;
            foreach (var keyPath in webView2Keys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                    if (key != null)
                    {
                        var version = key.GetValue("pv");
                        if (version != null && !string.IsNullOrEmpty(version.ToString()))
                        {
                            sb.AppendLine($"OK: WebView2 Runtime found: {version}");
                            found = true;
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!found)
            {
                sb.AppendLine("INFO: WebView2 Runtime not found (may not be required)");
                sb.AppendLine("Download from: https://developer.microsoft.com/microsoft-edge/webview2/");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error checking WebView2: {ex.Message}");
        }
    }

    private static void CheckRequiredDlls(StringBuilder sb)
    {
        // Common DLLs that might be missing
        var systemDlls = new[]
        {
            "kernel32.dll",
            "user32.dll",
            "gdi32.dll",
            "advapi32.dll",
            "shell32.dll",
            "ole32.dll",
            "oleaut32.dll",
            "comdlg32.dll",
            "comctl32.dll",
            "dwmapi.dll",       // Desktop Window Manager (for transparency)
            "d3d11.dll",        // Direct3D 11
            "dxgi.dll",         // DirectX Graphics Infrastructure
            "d2d1.dll",         // Direct2D
            "dwrite.dll",       // DirectWrite
            "windowscodecs.dll" // WIC for image handling
        };

        int found = 0;
        int missing = 0;
        
        foreach (var dll in systemDlls)
        {
            var handle = LoadLibrary(dll);
            if (handle != IntPtr.Zero)
            {
                FreeLibrary(handle);
                found++;
            }
            else
            {
                sb.AppendLine($"WARNING: {dll} - NOT FOUND or failed to load!");
                missing++;
            }
        }
        
        sb.AppendLine($"System DLLs: {found} found, {missing} missing");
        
        if (missing > 0)
        {
            sb.AppendLine("Missing DLLs may indicate corrupted Windows installation.");
            sb.AppendLine("Try: sfc /scannow (run as Administrator)");
        }
    }

    private static void CheckNetwork(StringBuilder sb)
    {
        try
        {
            // Check if network is available
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                sb.AppendLine("ERROR: No network connection available!");
                return;
            }
            
            sb.AppendLine("OK: Network is available");
            
            // List network interfaces
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Take(5);
                
            foreach (var ni in interfaces)
            {
                sb.AppendLine($"  {ni.Name}: {ni.NetworkInterfaceType}");
            }
            
            // Try to reach servers
            var servers = new[]
            {
                "77.105.161.204",
                "77.105.161.129",
                "85.236.0.91"
            };
            
            foreach (var server in servers)
            {
                try
                {
                    using var ping = new Ping();
                    var reply = ping.Send(server, 3000);
                    if (reply.Status == IPStatus.Success)
                    {
                        sb.AppendLine($"  Ping {server}: {reply.RoundtripTime}ms");
                    }
                    else
                    {
                        sb.AppendLine($"  Ping {server}: FAILED ({reply.Status})");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Ping {server}: ERROR ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error checking network: {ex.Message}");
        }
    }

    private static void CheckDiskSpace(StringBuilder sb)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var drive = Path.GetPathRoot(appData);
            
            if (!string.IsNullOrEmpty(drive))
            {
                var driveInfo = new DriveInfo(drive);
                var freeGb = driveInfo.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                var totalGb = driveInfo.TotalSize / 1024.0 / 1024.0 / 1024.0;
                
                sb.AppendLine($"Drive {drive}");
                sb.AppendLine($"  Free: {freeGb:F1} GB / {totalGb:F1} GB");
                
                if (freeGb < 30)
                {
                    sb.AppendLine($"  WARNING: Less than 30 GB free! Game requires ~27 GB");
                }
                else
                {
                    sb.AppendLine($"  OK: Sufficient space for game (~27 GB needed)");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error checking disk: {ex.Message}");
        }
    }

    private static void CheckPermissions(StringBuilder sb)
    {
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArmaLauncher");
            
            // Try to create directory and write a test file
            Directory.CreateDirectory(appData);
            var testFile = Path.Combine(appData, ".write_test");
            
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            
            sb.AppendLine($"OK: Can write to {appData}");
        }
        catch (UnauthorizedAccessException)
        {
            sb.AppendLine("ERROR: No write permission to AppData!");
            sb.AppendLine("Try running as Administrator or check antivirus");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error checking permissions: {ex.Message}");
        }
        
        // Check if running as admin
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            var isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            sb.AppendLine($"Running as Administrator: {isAdmin}");
        }
        catch { }
    }

    public static string GetCrashReport(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CRASH REPORT ===");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        
        // Exception details
        sb.AppendLine("--- Exception ---");
        sb.AppendLine($"Type: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine($"Source: {ex.Source}");
        sb.AppendLine();
        sb.AppendLine("Stack Trace:");
        sb.AppendLine(ex.StackTrace);
        
        // Inner exceptions
        var inner = ex.InnerException;
        int depth = 0;
        while (inner != null && depth < 5)
        {
            sb.AppendLine();
            sb.AppendLine($"--- Inner Exception {++depth} ---");
            sb.AppendLine($"Type: {inner.GetType().FullName}");
            sb.AppendLine($"Message: {inner.Message}");
            sb.AppendLine(inner.StackTrace);
            inner = inner.InnerException;
        }
        
        // Check for common issues
        sb.AppendLine();
        sb.AppendLine("--- Possible Causes ---");
        AnalyzeException(ex, sb);
        
        // Add diagnostics
        sb.AppendLine();
        sb.Append(RunFullDiagnostics());
        
        return sb.ToString();
    }

    private static void AnalyzeException(Exception ex, StringBuilder sb)
    {
        var msg = ex.ToString().ToLowerInvariant();
        
        if (msg.Contains("could not load file or assembly"))
        {
            sb.AppendLine("- Missing .NET assembly or DLL");
            sb.AppendLine("- Try reinstalling .NET Runtime 8.0 or later");
            sb.AppendLine("  https://dotnet.microsoft.com/download/dotnet/8.0");
        }
        
        if (msg.Contains("typeloadexception") || msg.Contains("type load"))
        {
            sb.AppendLine("- Type loading failure - likely missing runtime");
            sb.AppendLine("- Install .NET Desktop Runtime 8.0 x64");
        }
        
        if (msg.Contains("dllnotfoundexception") || msg.Contains("dll not found"))
        {
            sb.AppendLine("- Missing native DLL");
            sb.AppendLine("- Install Visual C++ Redistributable 2015-2022 x64");
            sb.AppendLine("  https://aka.ms/vs/17/release/vc_redist.x64.exe");
        }
        
        if (msg.Contains("wpf") || msg.Contains("presentationcore") || msg.Contains("presentationframework"))
        {
            sb.AppendLine("- WPF component failure");
            sb.AppendLine("- Install .NET Desktop Runtime (not just .NET Runtime)");
            sb.AppendLine("  https://dotnet.microsoft.com/download/dotnet/8.0");
        }
        
        if (msg.Contains("xaml") || msg.Contains("baml"))
        {
            sb.AppendLine("- XAML parsing error");
            sb.AppendLine("- Application files may be corrupted, try re-downloading");
        }
        
        if (msg.Contains("socket") || msg.Contains("network") || msg.Contains("http"))
        {
            sb.AppendLine("- Network error");
            sb.AppendLine("- Check firewall and antivirus settings");
            sb.AppendLine("- Try disabling VPN if active");
        }
        
        if (msg.Contains("access") && (msg.Contains("denied") || msg.Contains("unauthorized")))
        {
            sb.AppendLine("- Permission denied");
            sb.AppendLine("- Try running as Administrator");
            sb.AppendLine("- Check antivirus quarantine");
        }
        
        if (msg.Contains("out of memory") || msg.Contains("outofmemory"))
        {
            sb.AppendLine("- Insufficient memory");
            sb.AppendLine("- Close other applications");
            sb.AppendLine("- Check for memory leaks");
        }
    }

    public static void SaveDiagnosticsToFile(string content)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"ArmaLauncher_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            
            File.WriteAllText(path, content);
            FileLogger.Log($"Diagnostics saved to: {path}");
        }
        catch (Exception ex)
        {
            FileLogger.Error("Failed to save diagnostics", ex);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);
}
