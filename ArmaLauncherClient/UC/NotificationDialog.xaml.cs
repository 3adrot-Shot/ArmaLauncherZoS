using System.Windows;
using System.Windows.Input;

namespace ArmaLauncherClient.UC;

public enum NotificationDialogType
{
    Info,
    Warning,
    Error,
    Success
}

public partial class NotificationDialog : Window
{
    public NotificationDialog()
    {
        InitializeComponent();
    }
    
    /// <summary>
    /// Показывает диалог уведомления
    /// </summary>
    public static void Show(Window owner, string title, string message, NotificationDialogType type = NotificationDialogType.Info)
    {
        var dialog = new NotificationDialog
        {
            Owner = owner
        };
        
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        
        // Настраиваем внешний вид в зависимости от типа
        switch (type)
        {
            case NotificationDialogType.Error:
                dialog.TitleIcon.Text = "❌";
                dialog.OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xef, 0x44, 0x44));
                break;
            case NotificationDialogType.Warning:
                dialog.TitleIcon.Text = "⚠";
                dialog.OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xf5, 0x9e, 0x0b));
                break;
            case NotificationDialogType.Success:
                dialog.TitleIcon.Text = "✓";
                dialog.OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0xc5, 0x5e));
                break;
            default:
                dialog.TitleIcon.Text = "ℹ";
                break;
        }
        
        dialog.ShowDialog();
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
    
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
