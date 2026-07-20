using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace TbhBot.App.Services;

/// <summary>
/// Janela do overlay: cobre a tela virtual inteira, topmost, transparente e CLICK-THROUGH (não rouba clique/
/// foco do jogo). Desenha um badge de preço ao lado do tooltip. Coordenadas do badge vêm em PIXELS de tela;
/// converto p/ DIP pela escala de DPI da janela. Espelha o _win/draw do tbh_overlay.py.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly Canvas _cv = new();
    private double _dpi = 1.0;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        ShowActivated = false;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Owner = janela principal -> o overlay fecha junto com o app (senão seguraria o processo vivo).
        Owner = Application.Current?.MainWindow;

        Content = _cv;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            IntPtr ex = GetWindowLongPtr(h, GWL_EXSTYLE);
            SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)(ex.ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        };
    }

    /// <summary>Mantém topmost (o jogo/Steam pode se sobrepor).</summary>
    public void EnsureTopmost()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Desenha (ou limpa) o badge. null = nada sob o cursor.</summary>
    public void Render(OverlayService.Badge? badge)
    {
        _cv.Children.Clear();
        if (badge is null) return;

        // pixel de tela -> DIP no canvas (origem do canvas = canto da tela virtual)
        double x = badge.Sx / _dpi - SystemParameters.VirtualScreenLeft;
        double y = badge.Sy / _dpi - SystemParameters.VirtualScreenTop;

        var txt = new TextBlock
        {
            Text = badge.Text,
            Foreground = Brush(badge.Ink),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
        };
        var border = new Border
        {
            Background = Brush(badge.Face),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 3, 7, 3),
            Child = txt,
        };
        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y - 13);   // centraliza verticalmente no ponto do tooltip
        _cv.Children.Add(border);
    }

    private static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b;
    }
}
