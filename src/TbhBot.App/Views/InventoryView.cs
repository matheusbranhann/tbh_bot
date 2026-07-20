using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TbhBot.App.Services;
using TbhBot.Core.Game;

namespace TbhBot.App.Views;

/// <summary>
/// Aba Inventário = LISTA. Porta o inv_tree do tbh_panel.py: lê Inventory.List() (nome/grade/qtd/preço via
/// item_prices.json embutido) e mostra uma TABELA ordenável (Item/Grade/Qtd/Unit $/Total $) com cor por
/// grade + rodapé "Total: $X · N itens (M tipos)". Cabeçalho clicável ordena; padrão = maior valor primeiro.
/// </summary>
public sealed class InventoryView : UserControl
{
    private readonly EngineService _svc;
    private readonly TextBlock _total;
    private readonly StackPanel _rows = new();
    private readonly DispatcherTimer _timer;

    private List<InvItem> _data = new();
    private string _sortKey = "total";
    private bool _sortDesc = true;

    // largura fixa das colunas da direita (o nome preenche o resto) — alinha header + linhas.
    private const double WGrade = 120, WQty = 60, WUnit = 90, WTotal = 100;

    // cor por grade (0..9) — Common..Cosmic.
    private static readonly Brush[] GradeCol =
    {
        Frozen("#9AA0AB"), Frozen("#5CBF6A"), Frozen("#4BB0CC"), Frozen("#B06BE8"), Frozen("#E5564C"),
        Frozen("#E8A13A"), Frozen("#E8C93A"), Frozen("#4BE0D0"), Frozen("#FF9440"), Frozen("#FF5EA8"),
    };
    private static Brush GradeBrush(int g) => g is >= 0 and <= 9 ? GradeCol[g] : B("Sub");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b;
    }
    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    public InventoryView(EngineService svc)
    {
        _svc = svc;
        var root = new DockPanel { Margin = new Thickness(12) };

        // ---------- topo: título + total + refresh ----------
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(top, Dock.Top);

        var refresh = new Button { Content = "⟳ Refresh", Padding = new Thickness(9, 4, 9, 4) };
        refresh.Click += (_, _) => Refresh();
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);

        _total = new TextBlock
        {
            Text = "",
            Foreground = Frozen("#5CBF6A"),
            FontFamily = (FontFamily)Application.Current.FindResource("Mono"),
            FontSize = 15, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        top.Children.Add(_total);
        root.Children.Add(top);

        // ---------- header clicável ----------
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---------- linhas roláveis ----------
        var card = new Border { Style = S("Card.Border"), Padding = new Thickness(0) };
        card.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rows,
        };
        root.Children.Add(card);

        Content = root;

        _svc.StateChanged += Refresh;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => { _timer.Stop(); _svc.StateChanged -= Refresh; };
    }

    private Border BuildHeader()
    {
        var dp = new DockPanel { Margin = new Thickness(10, 0, 10, 4) };
        void Col(string key, string label, double w, bool right)
        {
            var tb = new TextBlock
            {
                Foreground = B("Sub"), FontWeight = FontWeights.SemiBold, FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tb.Text = label + (_sortKey == key ? (_sortDesc ? " ▼" : " ▲") : "");
            tb.MouseLeftButtonUp += (_, _) => SortBy(key);
            if (w > 0) { tb.Width = w; DockPanel.SetDock(tb, Dock.Right); }
            dp.Children.Add(tb);
        }
        // docka os da direita primeiro (ordem inversa), nome preenche o resto
        Col("total", "Total $", WTotal, true);
        Col("unit", "Unit $", WUnit, true);
        Col("qty", "Qtd", WQty, true);
        Col("grade", "Grade", WGrade, false);
        Col("name", "Item", 0, false);   // fill
        return new Border { Child = dp, Margin = new Thickness(0, 0, 0, 4) };
    }

    private void SortBy(string key)
    {
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key is "total" or "unit" or "qty"; }  // números começam desc
        // reconstrói o header (setas) e re-renderiza
        if (Content is DockPanel dpRoot)
        {
            var old = dpRoot.Children.OfType<Border>().FirstOrDefault(b => b.Child is DockPanel);
            if (old is not null) { int i = dpRoot.Children.IndexOf(old); dpRoot.Children.RemoveAt(i); var h = BuildHeader(); DockPanel.SetDock(h, Dock.Top); dpRoot.Children.Insert(i, h); }
        }
        RenderRows();
    }

    // ===================== dados =====================

    private void Refresh()
    {
        if (!_svc.IsAttached)
        {
            _rows.Children.Clear();
            _total.Text = "";
            _rows.Children.Add(Note("jogo fechado — reabra para listar o inventário"));
            return;
        }
        Task.Run(() =>
        {
            List<InvItem> data;
            try { if (!_svc.IsAttached) return; data = _svc.Engine.Inventory.List(); }
            catch { return; }
            Dispatcher.Invoke(() => { _data = data; RenderRows(); });
        });
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        if (_data.Count == 0)
        {
            _total.Text = "";
            _rows.Children.Add(Note("nenhum item lido (resolvendo offsets?)"));
            return;
        }

        IEnumerable<InvItem> q = _sortKey switch
        {
            "name" => _data.OrderBy(i => i.Name),
            "grade" => _data.OrderBy(i => i.Grade),
            "qty" => _data.OrderBy(i => i.Qty),
            "unit" => _data.OrderBy(i => i.Unit),
            _ => _data.OrderBy(i => i.Unit * i.Qty),
        };
        var rows = (_sortDesc ? q.Reverse() : q).ToList();

        double grand = 0; int items = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var it = rows[i];
            grand += it.Unit * it.Qty; items += it.Qty;
            _rows.Children.Add(BuildRow(it, i % 2 == 1));
        }
        _total.Text = $"Total: ${grand.ToString("0.00", CultureInfo.InvariantCulture)}   ·   {items} itens ({rows.Count} tipos)";
    }

    private Border BuildRow(InvItem it, bool alt)
    {
        var dp = new DockPanel { Margin = new Thickness(10, 5, 10, 5) };

        TextBlock Cell(string text, double w, bool right, Brush fg, bool mono = false, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text, Foreground = fg, FontSize = 12,
                TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            };
            if (mono) tb.FontFamily = (FontFamily)Application.Current.FindResource("Mono");
            if (w > 0) { tb.Width = w; DockPanel.SetDock(tb, Dock.Right); }
            return tb;
        }

        double total = it.Unit * it.Qty;
        bool hi = it.Unit >= 0.05;
        string unitS = it.Unit > 0 ? "$" + it.Unit.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        string totalS = it.Unit > 0 ? "$" + total.ToString("0.00", CultureInfo.InvariantCulture) : "—";

        dp.Children.Add(Cell(totalS, WTotal, true, hi ? Frozen("#5CBF6A") : B("Sub"), mono: true, bold: true));
        dp.Children.Add(Cell(unitS, WUnit, true, B("Sub"), mono: true));
        dp.Children.Add(Cell(it.Qty.ToString(), WQty, true, B("Fg"), mono: true));
        dp.Children.Add(Cell(Inventory.GradeName(it.Grade), WGrade, false, GradeBrush(it.Grade), bold: true));
        dp.Children.Add(Cell(it.Name, 0, false, B("Fg")));   // fill

        return new Border { Child = dp, Background = alt ? B("Card2") : Brushes.Transparent };
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text, Foreground = B("Subtle"), Margin = new Thickness(12),
        TextWrapping = TextWrapping.Wrap,
    };
}
