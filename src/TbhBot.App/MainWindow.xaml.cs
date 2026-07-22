using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TbhBot.App.Services;
using TbhBot.App.Views;
using TbhBot.Core.Update;

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
        // Onde os perfis moram (pasta do exe). Loga também o BaseDirectory: num publish single-file ele
        // pode ser a pasta temporária de extração — é o que justifica usar ProcessPath pra persistir.
        _svc.RaiseLog($"perfis: {ProfileStore.ExeDir}  (BaseDirectory={AppContext.BaseDirectory})");
        UpdateConn();
        Closed += (_, _) => _svc.Stop();
        _ = CheckUpdateAsync();
    }

    // ══════════════════ AUTO-UPDATE DO PAINEL ══════════════════
    // É o que faz o painel voltar a funcionar sozinho quando O JOGO atualiza: cada release já sai
    // com os offsets do build novo embutidos, então baixar a versão nova cura tudo — sem exigir
    // .NET 6 / Il2CppDumper na máquina de quem usa.
    private readonly AutoUpdate _upd = new();
    private string _updUrl = "", _updTag = "";
    private bool _updBusy;

    private async Task CheckUpdateAsync()
    {
        var (available, tag, url) = await _upd.CheckAsync(AutoUpdate.CurrentVersion);
        if (!available) return;
        _updUrl = url; _updTag = tag;
        RefreshBanner();
    }

    /// <summary>Decide a mensagem do banner. UM lugar só — senão o aviso de build novo e o de versão
    /// nova ficam se sobrescrevendo conforme a ordem em que chegam.</summary>
    private void RefreshBanner()
    {
        if (_updBusy) return;                                  // download em andamento: não mexe
        bool unknownBuild = _svc.IsAttached && !_svc.Engine.OffsetsLoaded;
        bool hasUpdate = _updUrl.Length > 0;
        if (!unknownBuild && !hasUpdate) { UpdateBanner.Visibility = Visibility.Collapsed; return; }

        string build = _svc.Engine.BuildHash is { Length: >= 7 } h ? h[..7] : "?";
        if (unknownBuild)
        {
            // Mais urgente: o usuário está vendo features sumirem. Explica o porquê e o que resolve.
            UpdateTitle.Text = "O jogo atualizou — este painel ainda não conhece esse build";
            UpdateDesc.Text = hasUpdate
                ? $"Baixe a {_updTag}: ela já vem com os offsets do build {build}. Sem isso, só os cheats por AOB funcionam."
                : $"Build {build} desconhecido. Só os cheats por AOB funcionam até sair uma versão nova do painel.";
        }
        else
        {
            UpdateTitle.Text = $"Nova versão disponível: {_updTag}";
            UpdateDesc.Text  = $"Você está na v{AutoUpdate.CurrentVersion}. A versão nova já vem com os offsets do build atual do jogo.";
        }
        UpdateBtn.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_updBusy || _updUrl.Length == 0) return;
        _updBusy = true;
        UpdateBtn.IsEnabled = false;
        try
        {
            var prog = new Progress<double>(p => UpdateBtn.Content = $"BAIXANDO {p * 100:0}%");
            var (newExe, exe, dir) = await _upd.DownloadAndStageAsync(_updUrl, prog);
            UpdateBtn.Content = "REINICIANDO...";
            _upd.LaunchUpdater(newExe, exe, dir);   // troca o exe depois que ESTE processo sair
            Close();
        }
        catch (Exception ex)
        {
            UpdateBtn.Content = "ATUALIZAR AGORA";
            UpdateBtn.IsEnabled = true;
            _updBusy = false;
            UpdateDesc.Text = $"Falhou: {ex.Message} — baixe manualmente em github.com/{AutoUpdate.Repo}/releases";
        }
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
        RefreshBanner();
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
