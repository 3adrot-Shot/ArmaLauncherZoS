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
    private bool _confirmed;

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
        
        dialog.ApplyType(type);

        dialog.ShowDialog();
    }

    /// <summary>
    /// Показывает диалог подтверждения с кнопками подтверждения и отмены.
    /// Возвращает true, если пользователь подтвердил действие.
    /// </summary>
    public static bool ShowConfirm(Window owner, string title, string message,
        NotificationDialogType type = NotificationDialogType.Warning,
        string? okText = null, string? cancelText = null)
    {
        var dialog = new NotificationDialog
        {
            Owner = owner
        };

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ApplyType(type);

        dialog.CancelButton.Visibility = Visibility.Visible;
        if (!string.IsNullOrEmpty(okText)) dialog.OkButton.Content = okText;
        if (!string.IsNullOrEmpty(cancelText)) dialog.CancelButton.Content = cancelText;

        dialog.ShowDialog();
        return dialog._confirmed;
    }

    private void ApplyType(NotificationDialogType type)
    {
        // Настраиваем внешний вид в зависимости от типа
        switch (type)
        {
            case NotificationDialogType.Error:
                TitleIcon.Text = "❌";
                OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xef, 0x44, 0x44));
                break;
            case NotificationDialogType.Warning:
                TitleIcon.Text = "⚠";
                OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xf5, 0x9e, 0x0b));
                break;
            case NotificationDialogType.Success:
                TitleIcon.Text = "✓";
                OkButton.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0xc5, 0x5e));
                break;
            default:
                TitleIcon.Text = "ℹ";
                break;
        }
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
        _confirmed = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
