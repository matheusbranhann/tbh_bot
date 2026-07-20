using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TbhBot.App.Services;
using TbhBot.App.Views;

namespace TbhBot.App;

public partial class MainWindow : Window
{
    private readonly EngineService _svc = new();
    private readonly Dictionary<RadioButton, (UserControl view, string title)> _tabs = new();

    public MainWindow()
    {
        InitializeComponent();

        _svc.StateChanged += UpdateConn;
        _svc.Log += m => StatusMini.Text = m;

        // as 5 views vivem juntas no ContentHost; a nav alterna a Visibility (só uma visível).
        Register(NavTrainer,   new TrainerView(_svc),   "Control Center");
        Register(NavInventory, new InventoryView(_svc), "Inventory");
        Register(NavMarket,    new MarketView(_svc),    "Market");
        Register(NavRunes,     new RunesView(_svc),     "Runes Tree");
        Register(NavStages,    new StagesView(_svc),    "Stage Map");

        Show(NavTrainer);
        _svc.Start();
        UpdateConn();
        Closed += (_, _) => _svc.Stop();
    }

    private void Register(RadioButton nav, UserControl view, string title)
    {
        view.Visibility = Visibility.Collapsed;
        ContentHost.Children.Add(view);
        _tabs[nav] = (view, title);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) Show(rb);
    }

    private void Show(RadioButton nav)
    {
        foreach (var (r, (view, title)) in _tabs)
        {
            bool on = r == nav;
            view.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) PageTitle.Text = title;
        }
    }

    private void UpdateConn()
    {
        bool ok = _svc.IsAttached;
        Particles.Connected = ok;
        var acc = (Brush)FindResource("Acc");
        var red = (Brush)FindResource("Red");
        if (ok)
        {
            var e = _svc.Engine;
            string build = e.BuildHash is { Length: >= 7 } h ? h[..7] : "?";
            ConnLabel.Text = "Game Detected";
            ConnLabel.Foreground = acc;
            ConnDot.Fill = acc;
            ConnDotGlow.Color = Color.FromRgb(0xFF, 0x7A, 0x18);
            ConnDetails.Text = $"Build: {build} | Offsets: {(e.OffsetsLoaded ? "OK" : "AOB")}";
            LaunchBtn.Content = "ENGINE RUNNING";
            LaunchBtn.IsEnabled = false;
        }
        else
        {
            ConnLabel.Text = "OFFLINE";
            ConnLabel.Foreground = red;
            ConnDot.Fill = red;
            ConnDotGlow.Color = Color.FromRgb(0xFF, 0x2A, 0x55);
            ConnDetails.Text = "Aguardando conexão...";
            LaunchBtn.Content = "LAUNCH ENGINE";
            LaunchBtn.IsEnabled = true;
        }
    }

    private void OnLaunchGame(object sender, RoutedEventArgs e) => _svc.LaunchGame();

    // ── chrome custom ──
    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null) return;   // não arrasta ao clicar num botão
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null) { if (d is T t) return t; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d); }
        return null;
    }
}
