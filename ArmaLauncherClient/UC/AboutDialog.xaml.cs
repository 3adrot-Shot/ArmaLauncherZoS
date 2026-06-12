using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace ArmaLauncherClient.UC;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        LauncherVersionTextBlock.Text = $"Launcher version: {GetLauncherVersion()}";
    }

    private static string GetLauncherVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    }
    
    public static void ShowAbout(Window owner)
    {
        var dialog = new AboutDialog
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }
    
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void TelegramButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/Zadrotix_dev",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore errors opening browser
        }
    }
}
