using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using EnshroudedServerManager.Core;

namespace EnshroudedServerManager;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version: {ServerManager.Version}";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => Close();
}
