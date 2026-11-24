// program.cs — 入口:DPI 感知 / 深色标题栏(DWM) / 自绘图标
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace E104
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [STAThread]
        static void Main()
        {
            // 全局崩溃记录(便于定位启动/运行期异常)
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            { WriteCrash("UI线程", e.Exception); ShowCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            { WriteCrash("后台线程", e.ExceptionObject as Exception); };

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm f = null;
            try
            {
                f = new MainForm();

                // 调试/测试:--page:N 直接切到指定页(0~8);延迟到窗口显示后应用
                try
                {
                    string[] args = Environment.GetCommandLineArgs();
                    int wantPage = -1;
                    bool autoConn = false;
                    // 同时支持 --page:N 参数与环境变量 E104_PAGE(参数在部分启动方式下会丢失)
                    string envPage = Environment.GetEnvironmentVariable("E104_PAGE");
                    int ep;
                    if (!string.IsNullOrEmpty(envPage) && int.TryParse(envPage, out ep)) wantPage = ep;
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] != null && args[i].StartsWith("--page:", StringComparison.Ordinal))
                        {
                            int pg;
                            if (int.TryParse(args[i].Substring(7), out pg)) wantPage = pg;
                        }
                        if (args[i] != null && args[i] == "--autoconnect") autoConn = true;
                    }
                    if (Environment.GetEnvironmentVariable("E104_AUTOCONNECT") == "1") autoConn = true;
                    // 先连接;页面切换延后到应用自身定时器(避免被角色门控覆盖)
                    if (autoConn)
                        f.Shown += delegate { f.BeginInvoke((MethodInvoker)delegate { f.Connect(); }); };
                    if (wantPage >= 0)
                    {
                        int target = wantPage;
                        f.Shown += delegate
                        {
                            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                            t.Interval = 2000;
                            t.Tick += delegate
                            {
                                t.Stop(); t.Dispose();
                                f.SwitchPage(target);
                            };
                            t.Start();
                        };
                    }
                }
                catch { }

                // 深色标题栏 (Win10 20H1+ / Win11)
                try
                {
                    int one = 1;
                    DwmSetWindowAttribute(f.Handle, 20, ref one, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(f.Handle, 19, ref one, 4);   // 旧版本号
                }
                catch { }

                // 自绘应用图标(蓝色圆 + 信号弧 + “智”字)
                try
                {
                    using (Bitmap bmp = new Bitmap(32, 32))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 122, 204)))
                            g.FillEllipse(b, 1, 1, 30, 30);
                        using (Pen p = new Pen(Color.White, 2.4f))
                        {
                            g.DrawArc(p, 7, 12, 8, 12, 210, 130);
                            g.DrawArc(p, 17, 12, 8, 12, 20, 130);
                        }
                        using (Font fnt = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold))
                        using (SolidBrush tb = new SolidBrush(Color.White))
                            g.DrawString("智", fnt, tb, 18, 3);
                        f.Icon = Icon.FromHandle(bmp.GetHicon());
                    }
                }
                catch { }
            }
            catch (Exception ex) { WriteCrash("构造 MainForm", ex); ShowCrash(ex); }

            Application.Run(f);
        }

        static string CrashPath()
        {
            try { return System.IO.Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "crash.log"); }
            catch { return "crash.log"; }
        }

        static void WriteCrash(string where, Exception ex)
        {
            try
            {
                using (System.IO.StreamWriter w = new System.IO.StreamWriter(CrashPath(), true, System.Text.Encoding.UTF8))
                {
                    w.WriteLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + where + "] ====");
                    w.WriteLine((ex == null) ? "(无异常对象)" : ex.ToString());
                    w.WriteLine();
                }
            }
            catch { }
        }

        static void ShowCrash(Exception ex)
        {
            try
            {
                MessageBox.Show((ex == null ? "未知错误" : ex.ToString()) + "\n\n详情已写入 crash.log",
                    "智家伴蓝牙串口助手 - 异常", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
