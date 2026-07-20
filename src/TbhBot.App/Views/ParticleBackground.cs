using System;
using System.Windows;
using System.Windows.Media;

namespace TbhBot.App.Views;

/// <summary>
/// Fundo animado "cyber network" (porte do &lt;canvas&gt; do web-preview): ~80 partículas flutuando, com
/// linhas ligando as próximas. A cor segue a conexão: laranja (conectado) / âmbar (offline). Desenhado a
/// cada frame via CompositionTarget.Rendering + OnRender. Fica atrás de tudo, não recebe mouse.
/// </summary>
public sealed class ParticleBackground : FrameworkElement
{
    private const int Count = 80;
    private const double LinkDist = 150.0;

    private struct P { public double X, Y, Vx, Vy; }
    private readonly P[] _p = new P[Count];
    private readonly Random _rng = new();
    private bool _started;

    /// <summary>true = tema laranja (conectado); false = âmbar (offline).</summary>
    public bool Connected { get; set; }

    private Pen? _linkPen;
    private Brush? _dotBrush;
    private Color _color = Color.FromRgb(0xFB, 0xBF, 0x24);

    public ParticleBackground()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    private void Start()
    {
        if (_started) return;
        _started = true;
        double w = ActualWidth > 0 ? ActualWidth : 1200, h = ActualHeight > 0 ? ActualHeight : 800;
        for (int i = 0; i < Count; i++)
            _p[i] = new P { X = _rng.NextDouble() * w, Y = _rng.NextDouble() * h,
                            Vx = (_rng.NextDouble() - 0.5) * 1.4, Vy = (_rng.NextDouble() - 0.5) * 1.4 };
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        for (int i = 0; i < Count; i++)
        {
            _p[i].X += _p[i].Vx; _p[i].Y += _p[i].Vy;
            if (_p[i].X < 0 || _p[i].X > w) _p[i].Vx *= -1;
            if (_p[i].Y < 0 || _p[i].Y > h) _p[i].Vy *= -1;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // fundo transparente clicável-através: pinta um retângulo transparente pra ocupar a área
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var target = Connected ? Color.FromRgb(0xFF, 0x7A, 0x18) : Color.FromRgb(0xFB, 0xBF, 0x24);
        if (target != _color || _dotBrush is null)
        {
            _color = target;
            _dotBrush = new SolidColorBrush(Color.FromArgb(0x80, _color.R, _color.G, _color.B)); _dotBrush.Freeze();
        }

        for (int i = 0; i < Count; i++)
        {
            var a = _p[i];
            dc.DrawEllipse(_dotBrush, null, new Point(a.X, a.Y), 1.5, 1.5);
            for (int j = i + 1; j < Count; j++)
            {
                var b = _p[j];
                double dx = a.X - b.X, dy = a.Y - b.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 >= LinkDist * LinkDist) continue;
                double alpha = (1 - Math.Sqrt(d2) / LinkDist) * 0.2;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), _color.R, _color.G, _color.B)), 1);
                pen.Freeze();
                dc.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
            }
        }
    }
}
