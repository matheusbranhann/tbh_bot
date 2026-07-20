using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TbhBot.App.Services;
using TbhBot.Core.Market;

namespace TbhBot.App.Views;

/// <summary>
/// Aba Mercado: busca o preco de um item no Steam Community Market.
/// Nao usa o Engine (precos sao externos ao jogo) — o ctor recebe o EngineService
/// so por convencao das abas.
/// </summary>
public sealed class MarketView : UserControl
{
    private readonly MarketDb _db = new();
    private readonly OverlayService _overlay = new();
    private readonly TextBox _search;
    private readonly Button _btn;
    private readonly Button _ovBtn;
    private readonly StackPanel _results;

    public MarketView(EngineService svc)
    {
        _overlay.Log += m => svc.RaiseLog(m);
        var fg = (Brush)Application.Current.FindResource("Fg");
        var accentBtn = (Style)Application.Current.FindResource("Accent.Button");
        var subText = (Style)Application.Current.FindResource("Sub.Text");

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "Steam Community Market",
            Foreground = fg,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Busca o preco (USD) de um item pelo nome exato do mercado.",
            Style = subText,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // Toggle do OVERLAY de preço (OCR sobre o tooltip do jogo).
        _ovBtn = new Button
        {
            Content = "◉  Price overlay: OFF",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 0, 6),
        };
        _ovBtn.Click += (_, _) =>
        {
            bool on = _overlay.Toggle();
            _ovBtn.Content = on ? "◉  Price overlay: ON" : "◉  Price overlay: OFF";
            _ovBtn.Style = on ? (Style)Application.Current.FindResource("Accent.Button") : null;
        };
        root.Children.Add(_ovBtn);
        root.Children.Add(new TextBlock
        {
            Text = "passe o mouse sobre um item no jogo → mostra o preço Steam ao lado do tooltip (OCR nativo).",
            Style = subText,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // Linha de busca: TextBox + botao.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _search = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_search, 0);

        _btn = new Button
        {
            Content = "Buscar",
            Style = accentBtn,
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_btn, 1);

        row.Children.Add(_search);
        row.Children.Add(_btn);
        root.Children.Add(row);

        // Lista de resultados.
        _results = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _results
        };
        root.Children.Add(scroll);

        _btn.Click += async (_, _) => await DoSearchAsync();
        _search.KeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) await DoSearchAsync();
        };

        Content = root;
    }

    private async Task DoSearchAsync()
    {
        var name = _search.Text?.Trim() ?? "";
        if (name.Length == 0) return;

        _btn.IsEnabled = false;
        var original = (string)_btn.Content;
        _btn.Content = "...";
        try
        {
            decimal? price = await _db.GetPriceAsync(name);
            AddResult(name, price);
        }
        catch
        {
            AddResult(name, null);
        }
        finally
        {
            _btn.Content = original;
            _btn.IsEnabled = true;
            _search.Clear();
            _search.Focus();
        }
    }

    private void AddResult(string name, decimal? price)
    {
        var acc = (Brush)Application.Current.FindResource("Acc");
        var fg = (Brush)Application.Current.FindResource("Fg");
        var red = (Brush)Application.Current.FindResource("Red");
        var subText = (Style)Application.Current.FindResource("Sub.Text");
        var cardBorder = (Style)Application.Current.FindResource("Card.Border");

        var grid = new Grid { Margin = new Thickness(10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBlock = new TextBlock
        {
            Text = name,
            Foreground = fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 0);

        bool has = price is not null;
        var priceBlock = new TextBlock
        {
            Text = has ? "$" + price!.Value.ToString("0.00", CultureInfo.InvariantCulture) : "sem preço",
            Foreground = has ? acc : red,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(priceBlock, 1);

        grid.Children.Add(nameBlock);
        grid.Children.Add(priceBlock);

        var card = new Border
        {
            Style = cardBorder,
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid
        };

        // Mais recente no topo.
        _results.Children.Insert(0, card);
    }
}
