using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TbhBot.App.Services;
using TbhBot.Core.Il2Cpp;

namespace TbhBot.App.Views;

/// <summary>
/// Aba Trainer (Control Center) no visual Neo-Dashboard: glass cards, cyber toggles (título+descrição),
/// stat boxes em grid. Toda a FUNÇÃO é preservada (ACTk/God, automações + encadeamento ACTk, filtros do
/// fuse, stats/campos FORÇADOS com botão Override, profiles, desbloqueio do cubo). Leitura em background (timer 1s).
/// </summary>
public sealed class TrainerView : UserControl
{
    private readonly EngineService _svc;

    private sealed class EditRow
    {
        public required string Name;
        public required Button Apply;
        public required TextBox Value;
        public required TextBlock Current;
        public Border? Box;                    // o stat-box (pinta de âmbar quando forçado)
        public bool Active;
    }

    private readonly Dictionary<string, EditRow> _statRows = new();
    private readonly Dictionary<string, EditRow> _stageRows = new();

    private readonly TextBlock _cubeLabel;
    private Button _cubeBtn = null!;
    private CheckBox _actkCheck = null!;
    private readonly DispatcherTimer _timer;

    private readonly Dictionary<string, CheckBox> _switches = new();
    private readonly Dictionary<int, CheckBox> _typeChecks = new();
    private ComboBox _synthCombo = null!;
    private ComboBox _profCombo = null!;
    private readonly ProfileStore _profStore = new();

    private CheckBox Reg(string key, CheckBox cb) { _switches[key] = cb; return cb; }

    private int _synthGrade = 2;
    private readonly HashSet<int> _synthTypes = [0, 1, 2];

    private static readonly string[] GradeNames =
        ["Common", "Uncommon", "Rare", "Legendary", "Immortal", "Arcana", "Beyond", "Celestial", "Divine", "Cosmic"];

    // cores dos ícones dos cards (variação como no mockup)
    private const string Cyan = "#22D3EE", Purple = "#A78BFA", RedC = "#FF2A55", AmberC = "#FBBF24", OrangeC = "#FF7A18";

    public TrainerView(EngineService svc)
    {
        _svc = svc;
        _cubeLabel = MonoText("cubo: --");

        var stack = new StackPanel();
        stack.Children.Add(BuildProfiles());                                 // 1º: profiles no topo
        stack.Children.Add(Row2(BuildProtection(), BuildAutomationCore()));  // shield + core
        stack.Children.Add(BuildSynthesis());                               // synthesis
        stack.Children.Add(BuildResilience());                              // auto-restart logo abaixo de synthesis
        stack.Children.Add(BuildStats());                                   // stats
        stack.Children.Add(BuildStageFields());                             // campos de fase
        stack.Children.Add(BuildUnlocks());                                 // cubo
        stack.Margin = new Thickness(0, 0, 0, 24);                          // respiro no fim
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { _timer.Start(); Tick(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        RefreshUnlocks();
        ReadStats();
        ReadStage();
        PushFuseConfig();
        if (_switches.TryGetValue("evolve", out var ev) && ev.IsChecked == true && !_svc.Engine.WantEvolve)
            ev.IsChecked = false;
    }

    // ═══════════════ cards ═══════════════

    private Border BuildProtection()
    {
        var body = new StackPanel();
        var e = _svc.Engine;
        _actkCheck = Reg("actk", Toggle("ACTk Bypass", "Neutraliza o detector (idempotente)", e.WantActk, v => e.WantActk = v));
        body.Children.Add(_actkCheck);
        body.Children.Add(Reg("god", Toggle("God Mode", "Invulnerabilidade absoluta", e.WantGodmode, v => e.WantGodmode = v)));
        return GlassCard("🛡", Cyan, "Shield Systems", body);
    }

    private Border BuildAutomationCore()
    {
        var body = new StackPanel();
        var e = _svc.Engine;
        body.Children.Add(Reg("autobox", AutoToggle("Auto-Box", "Abre caixas vivas via dispatcher", e.WantAutobox, v => e.WantAutobox = v)));
        body.Children.Add(Reg("autostash", AutoToggle("Auto-Stash", "Move e organiza itens por grade", e.WantAutostash, v => e.WantAutostash = v)));
        return GlassCard("⚡", Purple, "Core Automations", body);
    }

    private Border BuildSynthesis()
    {
        var e = _svc.Engine;
        // coluna esquerda: fuse + filtros
        var left = new StackPanel();
        left.Children.Add(Reg("autofuse", AutoToggle("Auto-Fuse", "Fusão em lote no cubo (Lv.65~80)", e.WantAutofuse, v => e.WantAutofuse = v)));
        var fRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 12, 0, 8) };
        fRow.Children.Add(new TextBlock { Text = "Grade alvo:", Style = St("Sub.Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _synthCombo = new ComboBox { Width = 150, SelectedIndex = _synthGrade };
        foreach (var g in GradeNames) _synthCombo.Items.Add(g);
        _synthCombo.SelectionChanged += (_, _) => { _synthGrade = _synthCombo.SelectedIndex; PushFuseConfig(); };
        fRow.Children.Add(_synthCombo);
        left.Children.Add(fRow);
        var tRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        tRow.Children.Add(new TextBlock { Text = "Tipos:", Style = St("Sub.Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        AddTypeCheck(tRow, "Equip", 0);
        AddTypeCheck(tRow, "Acess", 1);
        AddTypeCheck(tRow, "Mat", 2);
        left.Children.Add(tRow);
        left.Children.Add(Hint("Funde do grade menor ATÉ o escolhido (nunca acima)."));

        // coluna direita: boss + evolve
        var right = new StackPanel();
        right.Children.Add(Reg("autoboss", AutoToggle("Auto-Boss", "Consome soulstones no x-10 e volta", e.WantAutoboss, v => e.WantAutoboss = v)));
        right.Children.Add(Reg("evolve", AutoToggle("Evolution Climb", "Sobe 1 fase; auto-stop no Torment 3-9", e.WantEvolve, v => e.WantEvolve = v)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0); left.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(right, 1); right.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(left); grid.Children.Add(right);
        return GlassCard("⚗️", RedC, "Synthesis & Progression", grid);
    }

    private Border BuildResilience()
    {
        var body = new StackPanel();
        var e = _svc.Engine;
        body.Children.Add(Reg("watchdog", Toggle("Watchdog (Auto-Restart)", "Reabre o jogo se cair + reaplica tudo", e.WantWatchdog, v => e.WantWatchdog = v)));
        return GlassCard("🚨", RedC, "Resilience Protocol", body);
    }

    private void AddTypeCheck(Panel host, string label, int t)
    {
        var cb = new CheckBox { Content = label, IsChecked = _synthTypes.Contains(t), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        cb.Checked += (_, _) => { _synthTypes.Add(t); PushFuseConfig(); };
        cb.Unchecked += (_, _) => { _synthTypes.Remove(t); PushFuseConfig(); };
        _typeChecks[t] = cb;
        host.Children.Add(cb);
    }

    private void PushFuseConfig()
    {
        if (!_svc.IsAttached) return;
        try { _svc.Engine.AutoFuse.MaxGrade = _synthGrade; _svc.Engine.AutoFuse.Types = [.. _synthTypes]; }
        catch { }
    }

    // ═══════════════ STATS ═══════════════

    private static readonly (string title, string[] members)[] StatGroups =
    {
        ("⚔  ATTACK", ["Attack Damage", "Attack Speed", "Critical Chance", "Critical Damage",
            "Cooldown Reduction", "Cast Speed", "Physical Damage", "Fire Damage",
            "Cold Damage", "Lightning Damage", "Chaos Damage"]),
        ("🛡  DEFENSE", ["Max Hp", "Armor", "Dodge Chance", "Block Chance", "All Element Resistance",
            "Hp Regen /Sec", "Dmg Absorption", "Dmg Reduction"]),
        ("✦  OTHER", ["Movement Speed", "Area of Effect %", "Area of Effect Damage",
            "Add HP/Kill", "Life Leech", "Skill Heal"]),
    };

    private Border BuildStats()
    {
        var body = new StackPanel();
        foreach (var (title, members) in StatGroups)
        {
            body.Children.Add(new TextBlock { Text = title, Foreground = B("Acc"), FontFamily = (FontFamily)FindResource("UI"), FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(2, 12, 0, 8) });
            var grid = new WrapPanel();
            foreach (var name in members)
            {
                if (!GameConstants.Stats.ContainsKey(name)) continue;
                var row = MakeEditRow(name);
                _statRows[name] = row;
                row.Apply.Click += (_, _) => ToggleForceStat(row);
                row.Value.TextChanged += (_, _) => { if (row.Active) RebuildForcedStats(applyNow: true); };
                grid.Children.Add(StatBox(row));
            }
            body.Children.Add(grid);
        }
        body.Children.Add(Hint("Digite e clique Override → escreve NA HORA e mantém forçado. Clique em Forced ✓ para parar."));
        return GlassCard("📊", Cyan, "Neural Overrides (Stats)", body);
    }

    private void ToggleForceStat(EditRow row)
    {
        if (row.Active) { row.Active = false; SetApplyButton(row); RebuildForcedStats(applyNow: false); return; }
        if (!double.TryParse(row.Value.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        { _svc.RaiseLog($"stat '{row.Name}': valor inválido"); return; }
        row.Active = true; SetApplyButton(row); RebuildForcedStats(applyNow: true);
    }

    private void RebuildForcedStats(bool applyNow)
    {
        var d = new Dictionary<string, double>();
        foreach (var (_, row) in _statRows)
            if (row.Active && double.TryParse(row.Value.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                d[row.Name] = v;
        _svc.Engine.WantStats = d;
        if (applyNow && d.Count > 0)
            Task.Run(() => { try { if (_svc.IsAttached) _svc.Engine.Stats.ApplyStats(d); } catch { } });
    }

    private void ReadStats()
    {
        if (!_svc.IsAttached) return;
        Task.Run(() =>
        {
            Dictionary<string, double> stats;
            try { stats = _svc.IsAttached ? _svc.Engine.Stats.ReadStats() : new(); }
            catch { stats = new(); }
            Dispatcher.Invoke(() =>
            {
                foreach (var (name, row) in _statRows)
                {
                    if (!stats.TryGetValue(name, out double v)) { row.Current.Text = "--"; continue; }
                    string txt = v.ToString("0.###", CultureInfo.InvariantCulture);
                    row.Current.Text = txt;
                    // só pré-preenche quando o campo está vazio E NÃO está sendo editado (senão apaga o que você digita)
                    if (string.IsNullOrWhiteSpace(row.Value.Text) && !row.Value.IsKeyboardFocused) row.Value.Text = txt;
                }
            });
        });
    }

    // ═══════════════ CAMPOS DE FASE ═══════════════

    private Border BuildStageFields()
    {
        var body = new StackPanel();
        var grid = new WrapPanel();
        foreach (var name in GameConstants.StageFields.Keys)
        {
            var row = MakeEditRow(name);
            _stageRows[name] = row;
            row.Apply.Click += (_, _) => ToggleForceStage(row);
            row.Value.TextChanged += (_, _) => { if (row.Active) RebuildForcedStage(applyNow: true); };
            grid.Children.Add(StatBox(row));
        }
        body.Children.Add(grid);
        body.Children.Add(Hint("⚠ Act/StageNo podem ser rejeitados pelo servidor. Override escreve na hora e mantém forçado."));
        var readBtn = new Button { Content = "🔄 Ler atuais", Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        readBtn.Click += (_, _) => ReadStageNow();
        return GlassCard("📐", AmberC, "Stage Data Override", body, readBtn);
    }

    // Preenche TODOS os campos de fase com os valores ATUAIS do jogo (sobrescreve o que estiver digitado).
    private void ReadStageNow()
    {
        if (!_svc.IsAttached) { _svc.RaiseLog("jogo fechado — não dá pra ler os campos de fase"); return; }
        Task.Run(() =>
        {
            Dictionary<string, int> fields;
            try { fields = _svc.Engine.Stats.ReadStage(); } catch { return; }
            Dispatcher.Invoke(() =>
            {
                foreach (var (name, row) in _stageRows)
                    if (fields.TryGetValue(name, out int v)) row.Value.Text = v.ToString(CultureInfo.InvariantCulture);
                _svc.RaiseLog("campos de fase preenchidos com os valores atuais");
            });
        });
    }

    private void ToggleForceStage(EditRow row)
    {
        if (row.Active) { row.Active = false; SetApplyButton(row); RebuildForcedStage(applyNow: false); return; }
        if (!int.TryParse(row.Value.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        { _svc.RaiseLog($"campo '{row.Name}': valor inválido"); return; }
        row.Active = true; SetApplyButton(row); RebuildForcedStage(applyNow: true);
    }

    private void RebuildForcedStage(bool applyNow)
    {
        var d = new Dictionary<string, int>();
        foreach (var (_, row) in _stageRows)
            if (row.Active && int.TryParse(row.Value.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                d[row.Name] = v;
        _svc.Engine.WantStage = d;
        if (applyNow && d.Count > 0)
            Task.Run(() => { try { if (_svc.IsAttached) _svc.Engine.Stats.ApplyStage(d); } catch { } });
    }

    private void ReadStage()
    {
        if (!_svc.IsAttached) return;
        Task.Run(() =>
        {
            Dictionary<string, int> fields;
            try { fields = _svc.IsAttached ? _svc.Engine.Stats.ReadStage() : new(); }
            catch { fields = new(); }
            Dispatcher.Invoke(() =>
            {
                foreach (var (name, row) in _stageRows)
                {
                    if (!fields.TryGetValue(name, out int v)) { row.Current.Text = "--"; continue; }
                    string txt = v.ToString(CultureInfo.InvariantCulture);
                    row.Current.Text = txt;
                    if (string.IsNullOrWhiteSpace(row.Value.Text) && !row.Value.IsKeyboardFocused) row.Value.Text = txt;
                }
            });
        });
    }

    // ═══════════════ DESBLOQUEIOS (cubo) ═══════════════

    private Border BuildUnlocks()
    {
        var body = new StackPanel();
        body.Children.Add(_cubeLabel);
        _cubeBtn = new Button { Content = "🧊 Cube → Lv.100", Style = St("Accent.Button"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        _cubeBtn.Click += (_, _) => DoCube();
        body.Children.Add(_cubeBtn);
        body.Children.Add(Hint("Os estágios ficam na aba Stage Map."));
        return GlassCard("🧊", Purple, "System Unlocks", body);
    }

    private void DoCube()
    {
        if (!_svc.IsAttached) return;
        if (!Confirm("Elevar o nível do cubo para 100.")) return;
        Task.Run(() => { try { if (_svc.IsAttached) _svc.Engine.Save.SetCubeLevel(100); } catch { } });
    }

    private static bool Confirm(string what)
        => MessageBox.Show(what + "\n\nO jogo fecha ~12s pelo anti-cheat — reabra e o valor fica salvo. Continuar?",
               "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void RefreshUnlocks()
    {
        if (!_svc.IsAttached) { _cubeLabel.Text = "cubo: jogo fechado"; _cubeBtn.IsEnabled = false; return; }
        Task.Run(() =>
        {
            int? cube = null;
            try { if (_svc.IsAttached) cube = _svc.Engine.Save.CubeLevel(); } catch { }
            Dispatcher.Invoke(() =>
            {
                bool max = cube is >= 100;
                _cubeLabel.Text = cube is int c ? (max ? $"cubo: Lv.{c} (máximo)" : $"cubo: Lv.{c}") : "cubo: --";
                _cubeBtn.IsEnabled = cube is int && !max;
                _cubeBtn.Content = max ? "🧊 Cube já está Lv.100" : "🧊 Cube → Lv.100";
            });
        });
    }

    // ═══════════════ PROFILES ═══════════════

    private Border BuildProfiles()
    {
        var body = new StackPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _profCombo = new ComboBox { Width = 190, IsEditable = true, Height = 38, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        bar.Children.Add(_profCombo);
        Button Mk(string txt, Action onClick, bool accent = false)
        {
            var b = new Button { Content = txt, Margin = new Thickness(0, 0, 8, 0) };
            if (accent) b.Style = St("Accent.Button");
            b.Click += (_, _) => onClick();
            return b;
        }
        bar.Children.Add(Mk("Save", SaveProfile, accent: true));
        bar.Children.Add(Mk("Load", LoadProfile));
        bar.Children.Add(Mk("Delete", DeleteProfile));
        body.Children.Add(bar);
        body.Children.Add(Hint("Salva switches + filtros do fuse + stats/campos forçados em %APPDATA%/tbh_bot."));
        RefreshProfileCombo(null);
        return GlassCard("💾", Purple, "Memory Profiles", body);
    }

    private void RefreshProfileCombo(string? select)
    {
        var cur = select ?? (_profCombo.Text ?? "");
        _profCombo.Items.Clear();
        foreach (var name in _profStore.Load().Keys.OrderBy(k => k)) _profCombo.Items.Add(name);
        _profCombo.Text = cur;
    }

    private Profile Capture()
    {
        var p = new Profile { FuseGrade = _synthGrade, FuseTypes = [.. _synthTypes] };
        foreach (var (key, cb) in _switches) p.Switches[key] = cb.IsChecked == true;
        foreach (var (_, row) in _statRows)
            if (row.Active && double.TryParse(row.Value.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                p.Stats[row.Name] = v;
        foreach (var (_, row) in _stageRows)
            if (row.Active && int.TryParse(row.Value.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                p.Stage[row.Name] = iv;
        return p;
    }

    private void Apply(Profile p)
    {
        foreach (var (key, cb) in _switches)
            if (p.Switches.TryGetValue(key, out bool on)) cb.IsChecked = on;
        _synthCombo.SelectedIndex = p.FuseGrade;
        foreach (var (t, cb) in _typeChecks) cb.IsChecked = p.FuseTypes.Contains(t);
        foreach (var (_, row) in _statRows)
        {
            bool want = p.Stats.TryGetValue(row.Name, out double v);
            if (want) row.Value.Text = v.ToString(CultureInfo.InvariantCulture);
            row.Active = want; SetApplyButton(row);
        }
        RebuildForcedStats(applyNow: true);
        foreach (var (_, row) in _stageRows)
        {
            bool want = p.Stage.TryGetValue(row.Name, out int iv);
            if (want) row.Value.Text = iv.ToString(CultureInfo.InvariantCulture);
            row.Active = want; SetApplyButton(row);
        }
        RebuildForcedStage(applyNow: true);
    }

    private void SaveProfile()
    {
        string name = (_profCombo.Text ?? "").Trim();
        if (name.Length == 0) { _svc.RaiseLog("digite um nome pro profile primeiro"); return; }
        var all = _profStore.Load(); all[name] = Capture(); _profStore.Save(all);
        RefreshProfileCombo(name); _svc.RaiseLog($"profile '{name}' salvo ✓");
    }

    private void LoadProfile()
    {
        string name = (_profCombo.Text ?? "").Trim();
        var all = _profStore.Load();
        if (!all.TryGetValue(name, out var p)) { _svc.RaiseLog($"profile '{name}' não existe"); return; }
        Apply(p); _svc.RaiseLog($"profile '{name}' carregado ✓");
    }

    private void DeleteProfile()
    {
        string name = (_profCombo.Text ?? "").Trim();
        var all = _profStore.Load();
        if (all.Remove(name)) { _profStore.Save(all); RefreshProfileCombo(""); _profCombo.Text = ""; _svc.RaiseLog($"profile '{name}' apagado"); }
        else _svc.RaiseLog($"profile '{name}' não existe");
    }

    // ═══════════════ helpers de UI (novos componentes) ═══════════════

    private static EditRow MakeEditRow(string name) => new()
    {
        Name = name,
        Apply = new Button { Content = "Override", Padding = new Thickness(12, 0, 12, 0), MinWidth = 82, VerticalAlignment = VerticalAlignment.Stretch },
        Value = new TextBox { VerticalContentAlignment = VerticalAlignment.Center },
        Current = new TextBlock { Text = "--", Foreground = (Brush)Application.Current.FindResource("Sub"), FontFamily = (FontFamily)Application.Current.FindResource("Mono"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
    };

    // stat-box (mockup): header [nome | badge valor atual] + controles [input | Override]
    private Border StatBox(EditRow row)
    {
        var box = new Border
        {
            Background = B("Card3"), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12),
            BorderBrush = B("Stroke"), BorderThickness = new Thickness(1), Width = 300, Margin = new Thickness(0, 0, 12, 12),
        };
        row.Box = box;

        var sp = new StackPanel();
        // header
        var hdr = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nm = new TextBlock { Text = row.Name, Foreground = B("Fg"), FontFamily = (FontFamily)FindResource("UI"), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(nm, 0);
        var badge = new Border { Background = B("Card3"), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
        row.Current.Foreground = B("Sub");
        badge.Child = row.Current;
        Grid.SetColumn(badge, 1);
        hdr.Children.Add(nm); hdr.Children.Add(badge);
        sp.Children.Add(hdr);
        // controles
        var ctl = new Grid { Height = 34 };
        ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(row.Value, 0); row.Value.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(row.Apply, 1);
        ctl.Children.Add(row.Value); ctl.Children.Add(row.Apply);
        sp.Children.Add(ctl);

        box.Child = sp;
        return box;
    }

    // forçado = box âmbar + botão âmbar "Forced ✓"
    private void SetApplyButton(EditRow row)
    {
        row.Apply.Content = row.Active ? "Forced ✓" : "Override";
        row.Apply.Style = row.Active ? St("Accent.Button") : null;
        if (row.Box is not null)
        {
            row.Box.BorderBrush = row.Active ? B("Amber") : B("Stroke");
            row.Box.Background = row.Active ? new SolidColorBrush(Color.FromArgb(0x1A, 0xFB, 0xBF, 0x24)) : B("Card3");
        }
    }

    private CheckBox Toggle(string title, string desc, bool initial, Action<bool> set)
    {
        var cb = new CheckBox { Style = St("CyberToggle"), Content = title, Tag = desc, IsChecked = initial };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        return cb;
    }

    private CheckBox AutoToggle(string title, string desc, bool initial, Action<bool> set)
    {
        var cb = Toggle(title, desc, initial, set);
        cb.Checked += (_, _) =>
        {
            if (_actkCheck.IsChecked != true)
            {
                _actkCheck.IsChecked = true;
                _svc.RaiseLog("ACTk Bypass ligado junto (o auto-modo usa o hook)");
            }
        };
        return cb;
    }

    // glass card com header [ícone colorido | título | (opcional) botão à direita]
    private Border GlassCard(string emoji, string colorHex, string title, UIElement body, UIElement? headerRight = null)
    {
        var panel = new StackPanel();
        var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        if (headerRight is not null) { DockPanel.SetDock(headerRight, Dock.Right); hdr.Children.Add(headerRight); }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var icoWrap = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Background = B("Card3"), VerticalAlignment = VerticalAlignment.Center };
        icoWrap.Child = new TextBlock { Text = emoji, FontSize = 15, Foreground = Freeze(colorHex), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(icoWrap);
        left.Children.Add(new TextBlock { Text = title, Style = St("CardTitle"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        hdr.Children.Add(left);
        panel.Children.Add(hdr);
        panel.Children.Add(new Border { Height = 1, Background = B("Stroke"), Margin = new Thickness(0, -6, 0, 16) });
        panel.Children.Add(body);
        return new Border { Style = St("Card.Border"), Child = panel };
    }

    private Border Row2(Border left, Border right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0); left.Margin = new Thickness(0, 0, 10, 20);
        Grid.SetColumn(right, 1); right.Margin = new Thickness(10, 0, 0, 20);
        grid.Children.Add(left); grid.Children.Add(right);
        return new Border { Child = grid };   // wrapper (sem estilo) só p/ entrar no StackPanel
    }

    private TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = B("Subtle"), FontFamily = (FontFamily)FindResource("UI"), FontSize = 11,
        Margin = new Thickness(2, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock MonoText(string text) => new()
    {
        Text = text, Foreground = B("Sub"), FontFamily = (FontFamily)Application.Current.FindResource("Mono"), FontSize = 12,
    };

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Style St(string key) => (Style)Application.Current.FindResource(key);
    private static Brush Freeze(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
}
