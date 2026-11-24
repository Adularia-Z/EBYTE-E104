// click_nav.cs — 通过模拟点击左侧导航按钮切换到指定页(调试专用)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class ClickNav
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    struct RECT { public int L, T, R, B; }
    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("用法: ClickNav.exe <pid> <导航序号0-9> <out.png>"); return 2; }
        int pid = int.Parse(args[0]);
        int nav = int.Parse(args[1]);
        string outPng = args[2];
        Process p = Process.GetProcessById(pid);
        IntPtr h = p.MainWindowHandle;
        if (h == IntPtr.Zero) { Console.WriteLine("无主窗口"); return 1; }
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(400);

        RECT r; GetWindowRect(h, out r);
        // 窗口含标题栏(约31px)与边框;导航起点在客户区内 y=40,每项高 40
        int titleBar = 31;
        int x = r.L + 100;
        int y = r.T + titleBar + 40 + nav * 40 + 20;
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(700);

        int w = r.R - r.L, ht = r.B - r.T;
        using (Bitmap bmp = new Bitmap(w, ht))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, ht));
            bmp.Save(outPng, ImageFormat.Png);
        }
        Console.WriteLine("OK 点击导航 " + nav + " 并截图 → " + outPng);
        return 0;
    }
}
