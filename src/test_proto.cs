// test_proto.cs — 无头协议自检:对 COM3 真机验证 C# 版串口/会话层
using System;
using System.Collections.Generic;
using System.Threading;

namespace E104
{
    static class TestProto
    {
        static int _fails = 0;

        static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _fails++;
        }

        static List<string> QuerySync(Session se, string cmd, int tmo, int qui) { return QuerySync(se, cmd, tmo, qui, 600); }

        static List<string> QuerySync(Session se, string cmd, int tmo, int qui, int extraMs)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            List<string> res = new List<string>();
            se.Query(cmd, delegate(List<string> l, bool okk) { res.AddRange(l); done.Set(); }, tmo, qui);
            DateTime end = DateTime.Now.AddMilliseconds(tmo + 1500);
            while (!done.WaitOne(0) && DateTime.Now < end) { se.Pump(); Thread.Sleep(20); }
            while (!done.WaitOne(0)) { se.Pump(); Thread.Sleep(20); }
            DateTime g = DateTime.Now.AddMilliseconds(extraMs);
            while (DateTime.Now < g) { se.Pump(); Thread.Sleep(20); }
            return res;
        }

        [STAThread]
        static int Main()
        {
            string[] ports = SerialIo.ListPorts();
            Console.WriteLine("端口枚举: " + string.Join(",", ports));
            if (Array.IndexOf(ports, "COM3") < 0) { Console.WriteLine("COM3 不在,跳过硬件测试"); return 0; }

            SerialIo io = new SerialIo();
            io.Open("COM3", 115200);
            Session se = new Session(io);
            se.OnLog = delegate { };
            se.OnStateValue = delegate(string k, string v) { Console.WriteLine("  STATE " + k + " = " + v); };

            // 1) AT 握手
            List<string> r = QuerySync(se, "AT", 1500, 250);
            Check("AT 握手 +OK", Has(r, "+OK"));

            // 2) 链式查询(防粘连,单链)
            string[] keys = new string[] { "ROLE", "NAME", "MAC", "VERSION", "PWR", "ADV", "PHY",
                "BAUD", "CONN_INTERVAL", "SLEEP", "WDOG", "LOGMSG", "UP_AUTH", "SCANCMP",
                "ADVDATA", "LE_CODED", "BEACON", "SERVICE" };
            foreach (string k in keys) r = QuerySync(se, "AT+" + k + "?", 2200, 240);

            // 3) 设置路径:PWR=-5 → 查询 → 恢复
            r = QuerySync(se, "AT+PWR=-5", 2500, 280);
            Check("AT+PWR=-5 → +OK", Has(r, "+OK"));
            r = QuerySync(se, "AT+PWR?", 2200, 240);
            Check("PWR 查询 = -5", Has(r, "+PWR=-5"));
            r = QuerySync(se, "AT+PWR=0", 2500, 280);
            Check("PWR 恢复 0", Has(r, "+OK"));

            // 4) 从角色下发主角色指令 → +ERR=4
            r = QuerySync(se, "AT+SCAN?", 2500, 280);
            Check("从角色 SCAN? → +ERR=4", Has(r, "+ERR=4"));

            // 5) 交互式:ADV_EXT_CUR=3,3000 → 提示 → 发 3 字节 → +OK(即刻发送)
            r = QuerySync(se, "AT+ADV_EXT_CUR=3,3000", 4000, 250, 0);
            Check("ADV_EXT_CUR 提示 INPUT", Has(r, "INPUT EXT ADV:3"));
            se.SendRaw(new byte[] { 0x61, 0x62, 0x63 }, "test");
            List<string> r2 = new List<string>();
            ManualResetEvent d = new ManualResetEvent(false);
            se.OnLog = delegate(string tag, string text) { if (text != null && text.StartsWith("+OK")) { r2.Add(text); d.Set(); } };
            DateTime end = DateTime.Now.AddMilliseconds(2500);
            while (!d.WaitOne(0) && DateTime.Now < end) { se.Pump(); Thread.Sleep(20); }
            Check("交互式数据发送后 +OK", r2.Count > 0);
            se.OnLog = delegate { };

            // 6) 结束状态核查
            QuerySync(se, "AT+PWR?", 2200, 240);
            QuerySync(se, "AT+NAME?", 2200, 240);

            io.Dispose();
            Console.WriteLine();
            Console.WriteLine(_fails == 0 ? "==== 全部通过 ====" : ("==== 失败 " + _fails + " 项 ===="));
            return _fails == 0 ? 0 : 1;
        }

        static bool Has(List<string> lines, string needle)
        {
            for (int i = 0; i < lines.Count; i++)
                if (lines[i] != null && lines[i].Contains(needle)) return true;
            return false;
        }
    }
}
