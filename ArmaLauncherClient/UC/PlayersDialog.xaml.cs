using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using ArmaLauncherClient.Services;

namespace ArmaLauncherClient.UC;

public partial class PlayersDialog : Window
{
    public PlayersDialog()
    {
        InitializeComponent();
    }
    
    public static void Show(Window owner, GameServerInfo server)
    {
        var dialog = new PlayersDialog
        {
            Owner = owner
        };
        
        dialog.ServerNameText.Text = server.Name;
        dialog.PlayerCountText.Text = LocalizationManager.F("players_count", server.PlayerCount);
        dialog.TitleText.Text = LocalizationManager.F("players_online_title", server.PlayerCount, server.PlayerCountLimit);
        
        if (server.Players.Count > 0)
        {
            dialog.PlayersList.ItemsSource = server.Players;
            dialog.EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            dialog.PlayersList.Visibility = Visibility.Collapsed;
            dialog.EmptyState.Visibility = Visibility.Visible;
        }
        
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
}
