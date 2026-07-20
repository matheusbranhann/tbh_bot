using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TbhBot.App.Services;
using TbhBot.Core.Game;

namespace TbhBot.App.Views;

/// <summary>
/// Aba de runas = ÁRVORE navegável. Canvas com ZOOM (roda do mouse) + PAN (arrastar com o botão esquerdo);
/// CLICAR numa runa abre o PAINEL DE INFO à direita (nome, efeito via EAccountStatus, nível/máx, custo,
/// conexões) com botões de Desbloquear / Level +1 / Maxar. Sem botões de categoria — o desbloqueio é por
/// clique na runa. Todo desbloqueio faz CLAMP no teto por-runa (over-max = NRE / loading infinito).
/// </summary>
public sealed class RunesView : UserControl
{
    private readonly EngineService _svc;
    private readonly Canvas _canvas = new() { Background = Frozen("#191920") };
    private readonly MatrixTransform _mt = new(Matrix.Identity);
    private readonly Border _viewport;
    private readonly TextBlock _status;
    private readonly StackPanel _info = new();
    private readonly DispatcherTimer _timer;

    private Dictionary<int, RuneDef>? _defs;
    private Dictionary<int, int> _levels = new();
    private Dictionary<int, List<RuneLevelRow>> _levelRows = new();
    private Dictionary<int, (double x, double y)> _pos = new();
    private int _selected;
    private bool _busy, _centered;

    // pan
    private bool _panning, _panMoved;
    private Point _panStart, _panLast;

    // ícones embutidos
    private static Dictionary<string, string>? _iconB64;
    private readonly Dictionary<string, BitmapImage?> _iconCache = new();

    private static readonly Dictionary<string, Brush> CatColor = new()
    {
        ["chest"] = Frozen("#f0a83a"), ["combat"] = Frozen("#e5564c"),
        ["gold"] = Frozen("#e8c93a"), ["exp"] = Frozen("#5cbf6a"), ["util"] = Frozen("#4bb0cc"),
    };
    private static readonly Dictionary<string, string> CatLabel = new()
    {
        ["chest"] = "Caixa/Drop", ["combat"] = "Combate", ["gold"] = "Ouro", ["exp"] = "EXP", ["util"] = "Utilidade",
    };
    private static readonly Brush LockedBorder = Frozen("#34343e");
    private static readonly Brush MaxedBorder  = Frozen("#ffd24a");
    private static readonly Brush NodeFill      = Frozen("#26262e");
    private static readonly Brush SelBorder      = Frozen("#ffffff");
    private static readonly Brush LineBrush      = Frozen("#33333f");

    private static Brush Frozen(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    public RunesView(EngineService svc)
    {
        _svc = svc;
        var root = new DockPanel { Margin = new Thickness(10) };

        // ---- barra (SEM botões de categoria; desbloqueio é por clique na runa) ----
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(bar, Dock.Top);
        bar.Children.Add(MakeBtn("🔓 Desbloquear TUDO (máx)", "Accent.Button", () => Bulk(toMax: true)));
        bar.Children.Add(MakeBtn("Tudo nível 1", null, () => Bulk(toMax: false)));
        bar.Children.Add(MakeBtn("🎯 Centralizar", null, CenterView));
        bar.Children.Add(MakeBtn("⟳ Refresh", null, Refresh));
        root.Children.Add(bar);

        _status = new TextBlock { Style = S("Sub.Text"), Margin = new Thickness(2, 0, 0, 6) };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);

        // ---- painel de info à direita ----
        var infoCard = new Border { Style = S("Card.Border"), Width = 280, Margin = new Thickness(8, 0, 0, 0) };
        infoCard.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _info };
        DockPanel.SetDock(infoCard, Dock.Right);
        root.Children.Add(infoCard);
        ShowInfoEmpty();

        // ---- viewport com zoom/pan ----
        _canvas.RenderTransform = _mt;
        _viewport = new Border { ClipToBounds = true, Child = _canvas, Cursor = Cursors.SizeAll };
        _viewport.MouseWheel += OnWheel;
        _viewport.MouseLeftButtonDown += OnDown;
        _viewport.MouseMove += OnMove;
        _viewport.MouseLeftButtonUp += OnUp;
        var card = new Border { Style = S("Card.Border"), Padding = new Thickness(0), Child = _viewport };
        root.Children.Add(card);

        Content = root;

        _svc.StateChanged += Refresh;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => { _timer.Stop(); _svc.StateChanged -= Refresh; };
    }

    private Button MakeBtn(string text, string? style, Action onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 4, 9, 4) };
        if (style is not null) b.Style = S(style);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ===================== zoom / pan =====================

    private void OnWheel(object s, MouseWheelEventArgs e)
    {
        e.Handled = true;   // roda = SÓ zoom aqui; não deixa borbulhar (senão rolava a página/painel)
        var p = e.GetPosition(_viewport);
        double f = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var m = _mt.Matrix;
        double target = Math.Clamp(m.M11 * f, 0.35, 2.5);
        f = target / m.M11;
        m.ScaleAt(f, f, p.X, p.Y);
        _mt.Matrix = m;
    }

    // Centraliza a árvore no viewport (centróide dos nós, escala 0.6). Usado no 1º render + botão "Centralizar"
    // (recupera de qualquer zoom/pan que tenha jogado a árvore pra fora da tela).
    private void CenterView()
    {
        if (_pos.Count == 0) return;
        double cx = _pos.Values.Average(p => p.x) + 20, cy = _pos.Values.Average(p => p.y) + 20;
        const double s = 0.6;
        double vw = _viewport.ActualWidth > 0 ? _viewport.ActualWidth : 520;
        double vh = _viewport.ActualHeight > 0 ? _viewport.ActualHeight : 560;
        var m = Matrix.Identity;
        m.Scale(s, s);
        m.Translate(vw / 2 - cx * s, vh / 2 - cy * s);
        _mt.Matrix = m;
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _panStart = _panLast = e.GetPosition(_viewport);
        _panning = true; _panMoved = false;
        _viewport.CaptureMouse();
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(_viewport);
        var d = p - _panLast; _panLast = p;
        if ((p - _panStart).Length > 4) _panMoved = true;
        var m = _mt.Matrix; m.Translate(d.X, d.Y); _mt.Matrix = m;
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false; _viewport.ReleaseMouseCapture();
        if (_panMoved) return;                          // arrastou = pan, não seleciona

        var cp = e.GetPosition(_canvas);                // coords do canvas (já considera o transform)
        foreach (var (k, pos) in _pos)
            if (cp.X >= pos.x && cp.X <= pos.x + 40 && cp.Y >= pos.y && cp.Y <= pos.y + 40)
            { Select(k); return; }
        _status.Text = $"clique em ({cp.X:0},{cp.Y:0}) — nenhuma runa aí";
    }

    // ===================== leitura / render =====================

    private void Refresh()
    {
        if (!_svc.IsAttached) { _canvas.Children.Clear(); _status.Text = "jogo fechado — reabra para as runas"; return; }
        if (_busy) return;
        Task.Run(() =>
        {
            Dictionary<int, RuneDef>? defs; Dictionary<int, int> levels; Dictionary<int, List<RuneLevelRow>> rows;
            try
            {
                if (!_svc.IsAttached) return;
                defs = _svc.Engine.RuneDefs.Read();
                levels = _svc.Engine.Save.ReadRunes();
                rows = _svc.Engine.RuneLevels.Read();
            }
            catch { return; }
            Dispatcher.Invoke(() => Render(defs, levels, rows));
        });
    }

    private void Render(Dictionary<int, RuneDef>? defs, Dictionary<int, int> levels, Dictionary<int, List<RuneLevelRow>> rows)
    {
        _canvas.Children.Clear();
        if (defs is null || defs.Count == 0) { _status.Text = "jogo fechado ou resolvendo offsets…"; return; }
        _defs = defs; _levels = levels; _levelRows = rows;
        _pos = Layout(defs);

        foreach (var (k, d) in defs)
        {
            if (!_pos.TryGetValue(k, out var p1)) continue;
            foreach (int nx in d.Next)
                if (_pos.TryGetValue(nx, out var p2))
                    _canvas.Children.Add(new Line { X1 = p1.x + 20, Y1 = p1.y + 20, X2 = p2.x + 20, Y2 = p2.y + 20, Stroke = LineBrush, StrokeThickness = 2 });
        }

        double maxX = 0, maxY = 0;
        foreach (var (k, d) in defs)
        {
            if (!_pos.TryGetValue(k, out var p)) continue;
            AddNode(k, d, p.x, p.y, levels.GetValueOrDefault(k));
            maxX = Math.Max(maxX, p.x + 60); maxY = Math.Max(maxY, p.y + 66);
        }
        _canvas.Width = maxX; _canvas.Height = maxY;

        if (!_centered && _pos.Count > 0)
        {
            _centered = true;
            Dispatcher.BeginInvoke(new Action(CenterView), DispatcherPriority.ContextIdle);
        }

        int unlocked = defs.Keys.Count(k => levels.GetValueOrDefault(k) >= 1);
        _status.Text = $"{unlocked}/{defs.Count} desbloqueadas  ·  roda = zoom, arrastar = mover, clique numa runa = info";
        if (_selected != 0 && defs.ContainsKey(_selected)) ShowInfo(_selected);   // atualiza o painel
    }

    private void AddNode(int key, RuneDef d, double x, double y, int lv)
    {
        int mx = d.Max;
        bool locked = lv <= 0, maxed = lv >= mx && lv > 0;
        Brush border = key == _selected ? SelBorder : (locked ? LockedBorder : (maxed ? MaxedBorder : CatColor[RuneCat(d.Name)]));

        var box = new Border
        {
            Width = 40, Height = 40, Background = NodeFill,
            BorderBrush = border, BorderThickness = new Thickness(key == _selected ? 3 : 2), CornerRadius = new CornerRadius(3),
        };
        var img = Icon(d.Icon);
        if (img is not null) box.Child = new Image { Source = img, Width = 34, Height = 34, Opacity = locked ? 0.45 : 1.0 };
        Canvas.SetLeft(box, x); Canvas.SetTop(box, y);
        _canvas.Children.Add(box);

        var txt = new TextBlock
        {
            Text = $"{lv}/{mx}", Foreground = maxed ? MaxedBorder : (lv > 0 ? B("Fg") : B("Subtle")),
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"), FontSize = 10, FontWeight = FontWeights.Bold,
            Width = 40, TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(txt, x); Canvas.SetTop(txt, y + 42);
        _canvas.Children.Add(txt);
    }

    // ===================== painel de info =====================

    private void Select(int key) { _selected = key; ShowInfo(key); if (_defs is not null) Render(_defs, _levels, _levelRows); }

    private void ShowInfoEmpty()
    {
        _info.Children.Clear();
        _info.Children.Add(new TextBlock { Text = "clique numa runa", Foreground = B("Subtle"), Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap });
    }

    private void ShowInfo(int key)
    {
        _info.Children.Clear();
        if (_defs is null || !_defs.TryGetValue(key, out var d)) { ShowInfoEmpty(); return; }
        int lv = _levels.GetValueOrDefault(key), mx = d.Max;
        string cat = RuneCat(d.Name);
        var rows = _levelRows.GetValueOrDefault(key) ?? new();
        int status = rows.Count > 0 ? rows[0].Status : -1;

        void Head(string t) => _info.Children.Add(new TextBlock { Text = t, Foreground = B("Acc"), FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.Wrap });
        void Line(string t, Brush? fg = null) => _info.Children.Add(new TextBlock { Text = t, Foreground = fg ?? B("Fg"), FontSize = 12, Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap });

        _info.Margin = new Thickness(12);
        var img = Icon(d.Icon);
        if (img is not null) _info.Children.Add(new Image { Source = img, Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) });

        Head(d.Name);
        Line($"Efeito: {(status >= 0 ? RuneLevels.EffectName(status) + (RuneLevels.IsPercent(status) ? " (%)" : "") : "—")}", CatColor[cat]);
        Line($"Categoria: {CatLabel[cat]}");
        Line($"Nível: {lv} / {mx}", lv >= mx && lv > 0 ? MaxedBorder : B("Fg"));

        // custo do próximo nível (linha de RuneLevelInfoData do nível-alvo)
        int nextLv = Math.Min(lv + 1, mx);
        var nextRow = rows.FirstOrDefault(r => r.Level == nextLv) ?? rows.FirstOrDefault(r => r.Level == nextLv - 1);
        if (lv < mx && nextRow is not null) Line($"Custo p/ Lv.{nextLv}: {nextRow.Cost:N0} ouro", B("Sub"));

        var conns = d.Next.Where(_defs.ContainsKey).Select(k => _defs[k].Name).ToList();
        if (conns.Count > 0) Line($"Liga: {string.Join(", ", conns)}", B("Subtle"));

        _info.Children.Add(new Border { Height = 8 });

        // ações: Desbloquear (lv 0) / Level +1 / Maxar — SEMPRE clampado ao teto
        if (lv <= 0)
            _info.Children.Add(ActionBtn("🔓 Desbloquear", "Accent.Button", () => SetRune(key, 1)));
        if (lv > 0 && lv < mx)
            _info.Children.Add(ActionBtn("➕ Level +1", "Accent.Button", () => SetRune(key, Math.Min(lv + 1, mx))));
        if (lv < mx)
            _info.Children.Add(ActionBtn($"⏫ Maxar (Lv.{mx})", null, () => SetRune(key, mx)));
        if (lv >= mx && lv > 0)
            Line("✓ no máximo", MaxedBorder);
    }

    private Button ActionBtn(string txt, string? style, Action onClick)
    {
        var b = new Button { Content = txt, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(0, 5, 0, 5) };
        if (style is not null) b.Style = S(style);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ===================== escrita (clamp por-runa) =====================

    private void SetRune(int key, int target)
    {
        if (!_svc.IsAttached || _busy) return;
        var d = _defs?.GetValueOrDefault(key);
        if (d is null) return;
        int tgt = Math.Min(target, d.Max);                 // CLAMP ao teto por-runa (nunca estoura)
        WriteThen(() => _svc.Engine.Save.SetRune(key, tgt), $"runa '{d.Name}' → Lv.{tgt}");
    }

    private void Bulk(bool toMax)
    {
        if (!_svc.IsAttached || _busy) return;
        WriteThen(() =>
        {
            foreach (var (k, d) in _svc.Engine.RuneDefs.Read())
                _svc.Engine.Save.SetRune(k, Math.Min(toMax ? d.Max : 1, d.Max));
        }, toMax ? "todas no máximo" : "todas nível 1");
    }

    private void WriteThen(Action work, string okMsg)
    {
        _busy = true; _status.Text = "aplicando…";
        Task.Run(() =>
        {
            bool ok = true;
            try { if (_svc.IsAttached) work(); else ok = false; } catch { ok = false; }
            Dispatcher.Invoke(() => { _busy = false; _status.Text = ok ? okMsg : "falha (jogo fechado?)"; Refresh(); });
        });
    }

    // ===================== categoria / layout / ícones =====================

    private static string RuneCat(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (new[] { "chest", "drop", "openall", "openone", "autoopen", "wavecount" }.Any(n.Contains)) return "chest";
        if (new[] { "attackdamage", "attackspeed", "armor", "movespeed" }.Any(n.Contains)) return "combat";
        if (n.Contains("gold")) return "gold";
        if (n.Contains("exp")) return "exp";
        return "util";
    }

    private static Dictionary<int, (double x, double y)> Layout(Dictionary<int, RuneDef> defs)
    {
        var ch = defs.ToDictionary(kv => kv.Key, kv => kv.Value.Next.Where(defs.ContainsKey).ToList());
        var par = defs.Keys.ToDictionary(k => k, _ => new List<int>());
        foreach (var (k, cs) in ch) foreach (int c in cs) par[c].Add(k);
        var roots = defs.Keys.Where(k => par[k].Count == 0).OrderBy(k => k).ToList();
        var depth = new Dictionary<int, int>(); var tpar = new Dictionary<int, int?>(); var q = new Queue<int>();
        foreach (int r in roots) { depth[r] = 0; tpar[r] = null; q.Enqueue(r); }
        while (q.Count > 0) { int n = q.Dequeue(); foreach (int c in ch[n]) if (!depth.ContainsKey(c)) { depth[c] = depth[n] + 1; tpar[c] = n; q.Enqueue(c); } }
        foreach (int k in defs.Keys) if (!depth.ContainsKey(k)) depth[k] = 0;
        var tch = defs.Keys.ToDictionary(k => k, _ => new List<int>());
        foreach (var (n, p) in tpar) if (p is int pp) tch[pp].Add(n);
        var yp = new Dictionary<int, double>(); double ctr = 0.0;
        void Assign(int n) { var cc = tch[n].OrderBy(x => x).ToList(); if (cc.Count == 0) { yp[n] = ctr; ctr += 1; } else { foreach (int c in cc) Assign(c); yp[n] = cc.Average(c => yp[c]); } }
        foreach (int r in roots) { Assign(r); ctr += 1.3; }
        foreach (int k in defs.Keys) if (!yp.ContainsKey(k)) { yp[k] = ctr; ctr += 1; }
        const double CW = 104, RH = 58, PAD = 28;
        return defs.Keys.ToDictionary(k => k, k => (PAD + depth[k] * CW, PAD + yp[k] * RH));
    }

    private BitmapImage? Icon(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_iconCache.TryGetValue(name, out var cached)) return cached;
        BitmapImage? bmp = null;
        try
        {
            var map = IconMap();
            if (map.TryGetValue(name, out var b64) && !string.IsNullOrEmpty(b64))
            {
                byte[] png = Convert.FromBase64String(b64);
                bmp = new BitmapImage();
                bmp.BeginInit(); bmp.StreamSource = new MemoryStream(png); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
            }
        }
        catch { bmp = null; }
        _iconCache[name] = bmp;
        return bmp;
    }

    private static Dictionary<string, string> IconMap()
    {
        if (_iconB64 is not null) return _iconB64;
        var map = new Dictionary<string, string>();
        try
        {
            var asm = typeof(RunesView).Assembly;
            var res = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("runes_icons.json", StringComparison.OrdinalIgnoreCase));
            if (res is not null) { using var st = asm.GetManifestResourceStream(res)!; map = JsonSerializer.Deserialize<Dictionary<string, string>>(st) ?? map; }
        }
        catch { }
        _iconB64 = map;
        return map;
    }
}
