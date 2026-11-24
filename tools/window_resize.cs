// window_resize.cs — 把目标窗口调整到指定尺寸并截图,用于验证缩放不重叠
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

class WinResize
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    struct RECT { public int L, T, R, B; }
    static IntPtr _found = IntPtr.Zero;
    static int _pid;

    static bool Cb(IntPtr h, IntPtr lp)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if ((int)pid == _pid && IsWindowVisible(h))
        {
            RECT r; GetWindowRect(h, out r);
            if (r.R - r.L > 200 && r.B - r.T > 200) { _found = h; return false; }
        }
        return true;
    }

    static int Main(string[] args)
    {
        if (args.Length < 5) { Console.WriteLine("用法: WinResize.exe <pid> <w> <h> <out.png> <waitMs>"); return 2; }
        _pid = int.Parse(args[0]);
        int w = int.Parse(args[1]), h = int.Parse(args[2]);
        string path = args[3];
        int wait = int.Parse(args[4]);
        EnumWindows(new EnumProc(Cb), IntPtr.Zero);
        if (_found == IntPtr.Zero) { Console.WriteLine("未找到窗口"); return 1; }
        ShowWindow(_found, 9);              // SW_RESTORE
        System.Threading.Thread.Sleep(200);
        MoveWindow(_found, 40, 40, w, h, true);
        System.Threading.Thread.Sleep(wait);
        RECT r; GetWindowRect(_found, out r);
        int ww = r.R - r.L, hh = r.B - r.T;
        using (Bitmap bmp = new Bitmap(ww, hh))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            IntPtr dc = g.GetHdc();
            PrintWindow(_found, dc, 3);
            g.ReleaseHdc(dc);
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("OK " + ww + "x" + hh + " → " + path);
        return 0;
    }
}
