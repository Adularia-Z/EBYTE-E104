// screenshot.cs — 抓取指定进程主窗口截图(自用/测试辅助)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

class Shot
{
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")]
    static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr h, int cmd);

    delegate bool EnumProc(IntPtr h, IntPtr lp);
    struct RECT { public int L, T, R, B; }

    static IntPtr _found = IntPtr.Zero;
    static string _title = "";

    static bool Cb(IntPtr h, IntPtr lp)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if ((int)pid == _pid && IsWindowVisible(h))
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            RECT r;
            GetWindowRect(h, out r);
            if (r.R - r.L > 100 && r.B - r.T > 100)
            {
                _found = h; _title = sb.ToString();
                return false;
            }
        }
        return true;
    }

    static int _pid;

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: screenshot.exe <进程名(不含.exe)|PID> <输出.png> [是否前台:0/1]");
            return 2;
        }
        string nameOrPid = args[0]; string path = args[1];
        bool fg = args.Length > 2 && args[2] == "1";
        Process target = null;
        int pidI;
        if (int.TryParse(nameOrPid, out pidI))
        {
            try { target = Process.GetProcessById(pidI); } catch { }
        }
        else
        {
            Process[] ps = Process.GetProcessesByName(nameOrPid);
            if (ps.Length > 0) target = ps[0];
        }
        if (target == null) { Console.WriteLine("找不到进程: " + nameOrPid); return 1; }
        _pid = target.Id;
        EnumWindows(new EnumProc(Cb), IntPtr.Zero);
        if (_found == IntPtr.Zero) { Console.WriteLine("未找到可见主窗口(进程在但可能无窗口) pid=" + _pid); return 1; }
        if (fg) { try { SetForegroundWindow(_found); System.Threading.Thread.Sleep(300); } catch { } }
        RECT r; GetWindowRect(_found, out r);
        int w = r.R - r.L, hgt = r.B - r.T;
        if (w <= 0 || hgt <= 0) { Console.WriteLine("窗口尺寸异常"); return 1; }
        using (Bitmap bmp = new Bitmap(w, hgt))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(_found, hdc, 2 | 1);   // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
            bmp.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("OK \"" + _title + "\" " + w + "x" + hgt + " → " + path);
        return 0;
    }
}
