using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
namespace PortPatrol;

public partial class App : Application
{
    private async void App_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Window? splash = null;
        try { splash = BuildSplash(); splash.Show(); await Task.Delay(1400); }
        catch { /* splash is cosmetic only */ }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();

        if (splash != null)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => splash.Close();
            splash.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    private static Window BuildSplash()
    {
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/icon.png")),
            Width = 220, Height = 220, Stretch = Stretch.Uniform
        };
        var title = new TextBlock
        {
            Text = "PortPatrol", Foreground = Brushes.White, FontSize = 26, FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0)
        };
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(logo);
        panel.Children.Add(title);
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x83, 0x00)), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8), Child = panel
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Width = 360, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = border, Opacity = 0
        };
        w.Loaded += (_, _) => w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        return w;
    }
}
