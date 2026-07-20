using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TbhBot.App.Services;

namespace TbhBot.App.Views;

/// <summary>
/// Mapa dos 120 estagios (4 dificuldades x 3 atos x 10 estagios). StageKey = (dif+1)*1000 + act*100 + est.
/// Le Save.StageProgress() (Max,Cur,Wave) em background e pinta cada celula. Botoes desbloqueiam ate uma
/// dificuldade (Save.SetMaxStage, que fecha o jogo ~12s mas persiste). Espelha _build_stages/_render_stages
/// do tbh_panel.py.
/// </summary>
public sealed class StagesView : UserControl
{
    private static readonly string[] Diffs = { "NORMAL", "NIGHTMARE", "HELL", "TORMENT" };

    // cor por dificuldade (Normal/Nightmare/Hell/Torment) — igual ao STAGE_DIFFCOL do Python
    private static readonly Brush[] DiffCol =
    {
        Frozen("#5CBF6A"), Frozen("#4BB0CC"), Frozen("#E8A13A"), Frozen("#E5564C"),
    };

    // fundos/bordas fixos (nao existem como recurso de tema)
    private static readonly Brush UnlockedFill = Frozen("#2F2110"); // laranja-escuro
    private static readonly Brush LockedFill   = Frozen("#141519");
    private static readonly Brush LockedStroke = Frozen("#2B2E34");
    private static readonly Brush LockedText   = Frozen("#666B74");

    private readonly EngineService _svc;
    private readonly Dictionary<int, Cell> _cells = new();
    private readonly TextBlock _status;
    private readonly TextBlock _hint;
    private readonly TextBlock _empty;
    private readonly StackPanel _map;
    private readonly DispatcherTimer _timer;

    private sealed record Cell(Border Box, TextBlock Text);

    public StagesView(EngineService svc)
    {
        _svc = svc;

        var sub     = Brush("Sub");
        var subtle  = Brush("Subtle");
        var card    = Brush("Card");
        var stroke  = Brush("Stroke");
        var accStyle = (Style)Application.Current.FindResource("Accent.Button");

        // ---------------- barra de topo ----------------
        var unlockAll = new Button
        {
            Content = "Desbloquear TUDO",
            Style = accStyle,
            Margin = new Thickness(0, 0, 8, 0),
        };
        unlockAll.Click += (_, _) => Unlock(4310, "TORMENT 3-10");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(unlockAll);
        for (int d = 0; d < 4; d++)
        {
            int key = (d + 1) * 1000 + 310;                       // x-3-10 = ultima fase da dificuldade
            string nm = Diffs[d];
            var b = new Button
            {
                Content = "até " + nm,
                Foreground = DiffCol[d],
                Margin = new Thickness(2, 0, 2, 0),
                FontSize = 11,
            };
            b.Click += (_, _) => Unlock(key, nm + " 3-10");
            buttons.Children.Add(b);
        }

        var refresh = new Button { Content = "⟳ Refresh", Style = accStyle };
        refresh.Click += (_, _) => Refresh();

        _status = new TextBlock
        {
            Text = "—",
            Foreground = sub,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 10, 8, 4) };
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        DockPanel.SetDock(buttons, Dock.Left);
        top.Children.Add(buttons);
        top.Children.Add(_status);   // preenche o meio

        _hint = new TextBlock
        {
            Text = "passe o mouse sobre uma fase",
            Foreground = subtle,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            FontSize = 11,
            Margin = new Thickness(14, 0, 14, 2),
        };

        // ---------------- mapa ----------------
        _map = new StackPanel { Margin = new Thickness(14, 6, 14, 14) };
        BuildMap();

        _empty = new TextBlock
        {
            Text = "carregando… (abra o jogo para as fases aparecerem)",
            Foreground = sub,
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            FontSize = 13,
            Margin = new Thickness(18, 18, 0, 0),
        };

        var stack = new Grid();
        stack.Children.Add(_map);
        stack.Children.Add(_empty);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };

        var cardBorder = new Border
        {
            Background = card,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = stroke,
            Margin = new Thickness(8, 2, 8, 8),
            Child = scroll,
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(top, 0);
        Grid.SetRow(_hint, 1);
        Grid.SetRow(cardBorder, 2);
        rootGrid.Children.Add(top);
        rootGrid.Children.Add(_hint);
        rootGrid.Children.Add(cardBorder);

        Content = rootGrid;

        _svc.StateChanged += Refresh;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _svc.StateChanged -= Refresh;
        };
    }

    // ---------------- construcao do mapa (uma vez) ----------------
    private void BuildMap()
    {
        var sub = Brush("Sub");
        var mono = (FontFamily)Application.Current.FindResource("Mono");

        for (int d = 0; d < 4; d++)
        {
            // cabecalho da dificuldade: nome (colorido) + contador X/30
            var head = new DockPanel { Margin = new Thickness(0, d == 0 ? 0 : 14, 0, 4) };
            var name = new TextBlock
            {
                Text = Diffs[d],
                Foreground = DiffCol[d],
                FontFamily = mono,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
            };
            var count = new TextBlock
            {
                Text = "0/30",
                Foreground = sub,
                FontFamily = mono,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = "count" + d,
            };
            _diffCounts[d] = count;
            DockPanel.SetDock(count, Dock.Right);
            head.Children.Add(count);
            head.Children.Add(name);
            _map.Children.Add(head);

            int baseKey = (d + 1) * 1000;
            for (int a = 1; a <= 3; a++)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                row.Children.Add(new TextBlock
                {
                    Text = "Ato " + a,
                    Foreground = sub,
                    FontFamily = mono,
                    FontSize = 11,
                    Width = 56,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                for (int s = 1; s <= 10; s++)
                {
                    int key = baseKey + a * 100 + s;
                    bool boss = s == 10;
                    var txt = new TextBlock
                    {
                        Text = boss ? "★" : s.ToString(),
                        FontFamily = mono,
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var box = new Border
                    {
                        Width = 40,
                        Height = 40,
                        Margin = new Thickness(3),
                        CornerRadius = new CornerRadius(4),
                        BorderThickness = new Thickness(1),
                        Child = txt,
                        Cursor = System.Windows.Input.Cursors.Hand,
                    };
                    int capKey = key;
                    box.MouseEnter += (_, _) => ShowHint(capKey);
                    _cells[key] = new Cell(box, txt);
                    row.Children.Add(box);
                }
                _map.Children.Add(row);
            }
        }
    }

    private readonly TextBlock[] _diffCounts = new TextBlock[4];

    // ---------------- leitura em background + pintura ----------------
    private void Refresh()
    {
        if (!_svc.IsAttached)
        {
            Paint(-1, -1);
            return;
        }
        Task.Run(() =>
        {
            int max = -1, cur = -1;
            try
            {
                var p = _svc.Engine.Save.StageProgress();   // (Max,Cur,Wave)
                max = p.Max; cur = p.Cur;
            }
            catch { max = -1; cur = -1; }
            Dispatcher.Invoke(() => Paint(max, cur));
        });
    }

    private int _lastMax = int.MinValue, _lastCur = int.MinValue;

    private void Paint(int max, int cur)
    {
        _lastMax = max; _lastCur = cur;
        bool resolved = max >= 0;
        _empty.Visibility = resolved ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = _svc.IsAttached
            ? "jogo aberto, resolvendo offsets…"
            : "jogo fechado — reconecta ao reabrir";
        _map.Visibility = resolved ? Visibility.Visible : Visibility.Collapsed;
        if (!resolved) { _status.Text = "jogo fechado ou resolvendo offsets…"; return; }

        var acc    = Brush("Acc");
        var accH   = Brush("AccH");
        var accTxt = Brush("AccTxt");
        var amber  = Brush("Amber");
        var fg     = Brush("Fg");

        int unlocked = 0;
        for (int d = 0; d < 4; d++)
        {
            int baseKey = (d + 1) * 1000;
            int duc = 0;
            for (int a = 1; a <= 3; a++)
                for (int s = 1; s <= 10; s++)
                {
                    int key = baseKey + a * 100 + s;
                    var cell = _cells[key];
                    bool boss = s == 10;
                    bool isCur = key == cur && cur >= 0;
                    bool isUn  = key <= max;
                    if (isUn) { unlocked++; duc++; }

                    if (isCur)
                    {
                        cell.Box.Background = acc;
                        cell.Box.BorderBrush = accH;
                        cell.Box.BorderThickness = new Thickness(2);
                        cell.Text.Foreground = accTxt;
                        cell.Text.FontWeight = FontWeights.Bold;
                    }
                    else if (isUn)
                    {
                        cell.Box.Background = UnlockedFill;
                        cell.Box.BorderBrush = boss ? amber : acc;
                        cell.Box.BorderThickness = new Thickness(boss ? 2 : 1);
                        cell.Text.Foreground = fg;
                        cell.Text.FontWeight = boss ? FontWeights.Bold : FontWeights.Normal;
                    }
                    else
                    {
                        cell.Box.Background = LockedFill;
                        cell.Box.BorderBrush = boss ? amber : LockedStroke;
                        cell.Box.BorderThickness = new Thickness(boss ? 2 : 1);
                        cell.Text.Foreground = boss ? amber : LockedText;
                        cell.Text.FontWeight = boss ? FontWeights.Bold : FontWeights.Normal;
                    }
                }
            _diffCounts[d].Text = $"{duc}/30";
        }

        _status.Text = $"{unlocked}/120 liberados · atual: {(cur >= 0 ? StageName(cur) : "—")}";
    }

    private void ShowHint(int key)
    {
        int max = _lastMax, cur = _lastCur;
        string st = key == cur && cur >= 0
            ? "← ATUAL"
            : (max >= 0 && key <= max ? "liberado" : "bloqueado");
        string bossTag = key % 100 == 10 ? "  ·  ★ boss (custa soulstone)" : "";
        _hint.Text = $"{StageName(key)}  ·  {st}{bossTag}";
    }

    // ---------------- desbloqueio ----------------
    private void Unlock(int value, string label)
    {
        if (!_svc.IsAttached)
        {
            _status.Text = "jogo fechado";
            return;
        }
        var r = MessageBox.Show(
            $"Liberar estágios até {label}?\n\n" +
            "O jogo vai FECHAR em ~12s (anti-cheat), mas o progresso já fica salvo — " +
            "basta reabrir.",
            "Desbloquear estágios",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        _status.Text = "aplicando…";
        Task.Run(() =>
        {
            bool ok; int val;
            try { (ok, val) = _svc.Engine.Save.SetMaxStage(value); }
            catch { ok = false; val = value; }
            Dispatcher.Invoke(() =>
            {
                _status.Text = ok
                    ? $"✔ liberado até {label} ({val}) · o jogo fecha em ~12s; reabra (já salvo)"
                    : "falhou — jogo fechado?";
            });
        });
    }

    // ---------------- helpers ----------------
    private static string StageName(int key)
    {
        int d = key / 1000 - 1;
        if (d < 0 || d > 3) return key.ToString();
        return $"{Diffs[d]} {(key % 1000) / 100}-{key % 100}";
    }

    private static Brush Brush(string key)
        => (Brush)Application.Current.FindResource(key);

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
