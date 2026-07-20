using System.Runtime.InteropServices;
using System.Text;

namespace TbhBot.App.Services;

/// <summary>P/Invoke pro overlay: cursor, enumeração de janelas e captura de tela via GDI (StretchBlt + GetDIBits).</summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nint extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    public const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;

    /// <summary>Clique real (mouse_event) na posição de tela — o jogo ignora PostMessage, só aceita raw input.</summary>
    public static void ClickReal(int sx, int sy)
    {
        GetCursorPos(out var old);
        SetCursorPos(sx, sy);       Thread.Sleep(80);
        mouse_event(MouseLeftDown, 0, 0, 0, 0); Thread.Sleep(80);
        mouse_event(MouseLeftUp, 0, 0, 0, 0);   Thread.Sleep(100);
        SetCursorPos(old.X, old.Y);
    }

    // --- GDI (captura + upscale) ---
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd,
        IntPtr src, int xs, int ys, int ws, int hs, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        byte[] bits, ref BITMAPINFO bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        // paleta (não usada p/ 32bpp) — reservado p/ o marshaller não estourar
        public uint pad0, pad1, pad2;
    }

    private const int Halftone = 4;
    private const uint Srccopy = 0x00CC0020;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    /// <summary>
    /// Captura a região de tela (rx0,ry0,w,h) redimensionada p/ (bw,bh) e devolve os bytes BGRA (top-down,
    /// alpha=255). null se algo do GDI falhar. StretchBlt faz o upscale direto (sem dependência externa).
    /// </summary>
    public static byte[]? CaptureBgra(int rx0, int ry0, int w, int h, int bw, int bh)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, bw, bh);
        IntPtr old = SelectObject(mem, bmp);
        byte[]? outBytes = null;
        try
        {
            SetStretchBltMode(mem, Halftone);
            if (!StretchBlt(mem, 0, 0, bw, bh, screen, rx0, ry0, w, h, Srccopy)) return null;

            var bi = new BITMAPINFO
            {
                biSize = 40, biWidth = bw, biHeight = -bh,   // negativo = top-down
                biPlanes = 1, biBitCount = 32, biCompression = BiRgb,
            };
            var bytes = new byte[bw * bh * 4];
            if (GetDIBits(mem, bmp, 0, (uint)bh, bytes, ref bi, DibRgbColors) == 0) return null;
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;   // GDI zera o alpha -> força opaco
            outBytes = bytes;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
        return outBytes;
    }

    private const uint PwRenderFullContent = 2;   // PrintWindow: força o conteúdo composto por GPU (senão vem preto)

    /// <summary>
    /// Captura a JANELA (PrintWindow, não screen-grab) upscalada por <paramref name="scale"/> -> bytes BGRA top-down.
    /// Necessário pro overlay/popup do jogo, que é click-through: um screen-grab pegaria o que está por cima.
    /// Devolve também a origem da janela (screen top-left) pra converter caixa OCR -> coords de tela.
    /// </summary>
    public static byte[]? CaptureWindowScaledBgra(IntPtr hwnd, double scale, out int bw, out int bh, out POINT origin)
    {
        bw = bh = 0; origin = default;
        if (!GetWindowRect(hwnd, out var rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w < 50 || h < 50) return null;
        origin = new POINT { X = rc.Left, Y = rc.Top };
        bw = (int)(w * scale); bh = (int)(h * scale);

        IntPtr wdc = GetWindowDC(hwnd);
        if (wdc == IntPtr.Zero) return null;
        IntPtr natDc = CreateCompatibleDC(wdc), natBmp = CreateCompatibleBitmap(wdc, w, h);
        IntPtr bigDc = CreateCompatibleDC(wdc), bigBmp = CreateCompatibleBitmap(wdc, bw, bh);
        IntPtr oNat = SelectObject(natDc, natBmp), oBig = SelectObject(bigDc, bigBmp);
        byte[]? outBytes = null;
        try
        {
            if (!PrintWindow(hwnd, natDc, PwRenderFullContent)) return null;
            SetStretchBltMode(bigDc, Halftone);
            if (!StretchBlt(bigDc, 0, 0, bw, bh, natDc, 0, 0, w, h, Srccopy)) return null;

            var bi = new BITMAPINFO { biSize = 40, biWidth = bw, biHeight = -bh, biPlanes = 1, biBitCount = 32, biCompression = BiRgb };
            var bytes = new byte[bw * bh * 4];
            if (GetDIBits(bigDc, bigBmp, 0, (uint)bh, bytes, ref bi, DibRgbColors) == 0) return null;
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            outBytes = bytes;
        }
        finally
        {
            SelectObject(natDc, oNat); SelectObject(bigDc, oBig);
            DeleteObject(natBmp); DeleteObject(bigBmp);
            DeleteDC(natDc); DeleteDC(bigDc);
            ReleaseDC(hwnd, wdc);
        }
        return outBytes;
    }

    /// <summary>Acha a janela do jogo pelo PID (mais robusto que por título): visível, com título e &gt;200×200.</summary>
    public static IntPtr GameWindowByPid(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != (uint)pid || !IsWindowVisible(h) || GetWindowTextLength(h) <= 0) return true;
            if (GetWindowRect(h, out var r) && (r.Right - r.Left) > 200 && (r.Bottom - r.Top) > 200) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
