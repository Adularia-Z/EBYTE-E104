// test_duplex.cs — 双模组数据互传验证(COM3 + COM4)
// 依据实测协议(7413-1-16),BLE 数据接收为三行:
//   +RECEIVED:<handle>,<len>  /  BLE DATA  /  <实际数据>
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace E104
{
    class Dev
    {
        public string Port;
        public SerialIo Io;
        public Session Se;
        public string Mac = "", Name = "", Ver = "";
        public int Role = -1;
        public readonly StringBuilder Wire = new StringBuilder();   // 原始字节
        public readonly List<byte[]> Packets = new List<byte[]>();   // 按声明长度切出的完整载荷
        public readonly List<int> PacketHandles = new List<int>();
        public Dev(string p) { Port = p; }
    }

    static class TestDuplex
    {
        static int _pass = 0, _fail = 0;

        static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail != null ? "   → " + detail : ""));
            if (ok) _pass++; else _fail++;
        }

        static List<string> Q(Dev d, string cmd, int tmo, int quiet)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            List<string> res = new List<string>();
            d.Se.Query(cmd, delegate(List<string> l, bool okk) { res.AddRange(l); done.Set(); }, tmo, quiet);
            DateTime end = DateTime.Now.AddMilliseconds(tmo + 2500);
            while (!done.WaitOne(0) && DateTime.Now < end) { PunpAll(d, null); Thread.Sleep(12); }
            DateTime g = DateTime.Now.AddMilliseconds(350);
            while (DateTime.Now < g) { PunpAll(d, null); Thread.Sleep(12); }
            return res;
        }

        static Dev _a, _b;
        static void PunpAll(Dev main, Dev other)
        {
            if (_a != null && _a.Se != null) _a.Se.Pump();
            if (_b != null && _b.Se != null) _b.Se.Pump();
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { PunpAll(null, null); Thread.Sleep(8); }
        }

        static string Drain(Dev d)
        {
            string s;
            lock (d.Wire) { s = d.Wire.ToString(); d.Wire.Length = 0; }
            return s;
        }

        /// <summary>等待并返回该设备收到的原始文本</summary>
        static string WaitTx(Dev d, int ms)
        {
            Pump(ms);
            return Drain(d);
        }

        static string First(List<string> l)
        {
            for (int i = 0; i < l.Count; i++) if (!string.IsNullOrEmpty(l[i])) return l[i];
            return "(无应答)";
        }
        static string Val(List<string> l, string key)
        {
            for (int i = 0; i < l.Count; i++)
            {
                Match m = Regex.Match(l[i] ?? "", @"^\+" + key + @"\s*=\s*(.+)$");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return null;
        }

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string[] ports = SerialIo.ListPorts();
            Console.WriteLine("可用串口: " + string.Join(", ", ports));

            // 支持显式指定: TestDuplex.exe COM5 COM6
            string portA = args.Length >= 2 ? args[0].ToUpperInvariant() : "COM3";
            string portB = args.Length >= 2 ? args[1].ToUpperInvariant() : "COM4";
            if (Array.IndexOf(ports, portA) < 0 || Array.IndexOf(ports, portB) < 0)
            {
                Console.WriteLine("SKIP: 需要 " + portA + " 与 " + portB + " 同时可用(可用参数指定: TestDuplex.exe COM5 COM6)");
                return 0;                        // SKIP 视为通过(缺硬件不算失败)
            }

            Dev A = new Dev(portA), B = new Dev(portB);
            _a = A; _b = B;
            try
            {
                foreach (Dev d in new Dev[] { A, B })
                {
                    d.Io = new SerialIo();
                    d.Io.Open(d.Port, 115200);
                    d.Se = new Session(d.Io);
                    d.Se.OnLog = delegate { };
                    Dev cap = d;
                    d.Se.OnRawRx = delegate(byte[] chunk)
                    {
                        lock (cap.Wire) cap.Wire.Append(Encoding.ASCII.GetString(chunk));
                    };
                    d.Se.OnReceivedBinary = delegate(byte[] payload, int handle, int declared)
                    {
                        lock (cap.Packets)
                        {
                            cap.Packets.Add(payload);
                            cap.PacketHandles.Add(handle);
                        }
                    };
                }

                // ===== 1. 握手与身份 =====
                Console.WriteLine("\n[1] 握手与身份");
                foreach (Dev d in new Dev[] { A, B })
                {
                    bool hs = false;
                    for (int t = 0; t < 4 && !hs; t++)
                    {
                        if (First(Q(d, "AT", 1500, 250)).Contains("+OK")) hs = true;
                        else Thread.Sleep(250);
                    }
                    Check(d.Port + " AT 握手", hs, null);
                    if (!hs) continue;
                    d.Role = int.Parse(Val(Q(d, "AT+ROLE?", 1200, 150), "ROLE") ?? "-1");
                    d.Mac = Val(Q(d, "AT+MAC?", 1200, 150), "MAC");
                    d.Name = Val(Q(d, "AT+NAME?", 1200, 150), "NAME");
                    d.Ver = Val(Q(d, "AT+VERSION?", 1200, 150), "VERSION");
                    Console.WriteLine(string.Format("      {0}: 角色={1}  MAC={2}  名称={3}", d.Port, d.Role, d.Mac, d.Name));
                }
                bool macOk = !string.IsNullOrEmpty(A.Mac) && !string.IsNullOrEmpty(B.Mac)
                             && Regex.IsMatch(A.Mac, "^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")
                             && Regex.IsMatch(B.Mac, "^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$");
                Check("两台 MAC 已读到且不同", macOk && A.Mac != B.Mac, A.Mac + " / " + B.Mac);
                if (!macOk) { Console.WriteLine("MAC 读取失败,终止"); return 3; }

                // ===== 2. 清理 =====
                Q(A, "AT+LOGMSG=1", 1500, 200);
                Q(B, "AT+LOGMSG=1", 1500, 200);
                Q(A, "AT+DISCON", 2500, 300);
                Q(B, "AT+DISCON", 2500, 300);
                Pump(1200);

                // ===== 3. 角色 =====
                Console.WriteLine("\n[3] 配置角色: COM3=主(1)  COM4=从(0)");
                if (A.Role != 1) Check("COM3 设为单主", First(Q(A, "AT+ROLE=1", 2500, 250)).Contains("+OK"), null);
                else Console.WriteLine("      COM3 已是单主");
                if (B.Role != 0) Check("COM4 设为单从", First(Q(B, "AT+ROLE=0", 2500, 250)).Contains("+OK"), null);
                else Console.WriteLine("      COM4 已是从角色");

                Q(A, "AT+RESET", 4000, 600);
                Q(B, "AT+RESET", 4000, 600);
                Pump(3500);
                foreach (Dev d in new Dev[] { A, B })
                    for (int t = 0; t < 5; t++)
                        if (First(Q(d, "AT", 1500, 250)).Contains("+OK")) break; else Thread.Sleep(400);

                A.Role = int.Parse(Val(Q(A, "AT+ROLE?", 1500, 200), "ROLE") ?? "-1");
                B.Role = int.Parse(Val(Q(B, "AT+ROLE?", 1500, 200), "ROLE") ?? "-1");
                Check("复位后 COM3 角色 = 1", A.Role == 1, "实际 " + A.Role);
                Check("复位后 COM4 角色 = 0", B.Role == 0, "实际 " + B.Role);

                // ===== 4. 扫描 =====
                Console.WriteLine("\n[4] 主机 COM3 扫描");
                List<string> scan = Q(A, "AT+SCAN=1,8,1", 12000, 800);
                int found = 0; bool bFound = false;
                foreach (string l in scan)
                {
                    Match m = Regex.Match(l ?? "", @"^(\d+)\s+([0-9A-F:]{17})", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        found++;
                        if (string.Equals(m.Groups[2].Value.ToUpperInvariant(), B.Mac.ToUpperInvariant())) bFound = true;
                    }
                }
                Console.WriteLine("      扫描到 " + found + " 台");
                Check("扫描到从机 COM4", bFound, bFound ? B.Mac : "未找到");
                Pump(1000);

                // ===== 5. 连接 =====
                Console.WriteLine("\n[5] 建立连接 COM3 → COM4");
                Drain(A); Drain(B);
                string con = string.Join(" | ", Q(A, "AT+CONNECT=," + B.Mac, 12000, 600).ToArray());
                Console.WriteLine("      应答: " + con);
                Pump(2200);
                string conWire = Drain(A);
                Check("连接成功(CONNECTD)", con.Contains("CONNECTD") || conWire.Contains("CONNECTD"), null);
                List<string> cl = Q(A, "AT+CONNECT_LIST?", 2000, 250);
                string clTxt = string.Join(" | ", cl.ToArray());
                Console.WriteLine("      连接列表: " + clTxt);
                Check("连接列表含 COM4", clTxt.ToUpperInvariant().Contains(B.Mac.ToUpperInvariant()), null);

                // ===== 6. 双向互传 =====
                Console.WriteLine("\n[6] 双向数据互传(依据实测三行协议)");

                // 6a 主 → 从(逐字节断言)
                string m1 = "MASTER_TO_SLAVE_" + DateTime.Now.ToString("HHmmss");
                Console.WriteLine("\n  [6a] COM3(主) → COM4(从)   发送 \"" + m1 + "\" (" + m1.Length + "B)");
                Drain(A); Drain(B);
                ClearPackets(A); ClearPackets(B);
                string r1 = string.Join(" | ", Q(A, "AT+SEND=1," + m1.Length + ",3000", 4500, 350).ToArray());
                bool p1 = r1.Contains("INPUT BLE DATA");
                Console.WriteLine("      主机提示: " + (p1 ? "INPUT BLE DATA ✔" : r1));
                if (p1) A.Se.SendRaw(Encoding.ASCII.GetBytes(m1), "主→从");
                WaitTx(B, 2500);
                byte[] exp1 = Encoding.ASCII.GetBytes(m1);
                byte[] got1 = LastPacket(B);
                Check("从机收到完整载荷", got1 != null, got1 == null ? "(无)" : Hex(got1));
                Check("载荷长度与声明一致", got1 != null && got1.Length == exp1.Length, got1 == null ? "-" : got1.Length + " vs " + exp1.Length);
                Check("载荷逐字节一致", ByteEquals(got1, exp1), got1 == null ? "-" : Hex(got1));

                // 6b 从 → 主(逐字节断言)
                string m2 = "SLAVE_TO_MASTER_" + DateTime.Now.ToString("HHmmss");
                Console.WriteLine("\n  [6b] COM4(从) → COM3(主)   发送 \"" + m2 + "\" (" + m2.Length + "B)");
                Drain(A); Drain(B);
                ClearPackets(A); ClearPackets(B);
                string r2 = string.Join(" | ", Q(B, "AT+SEND=1," + m2.Length + ",3000", 4500, 350).ToArray());
                bool p2 = r2.Contains("INPUT BLE DATA");
                Console.WriteLine("      从机提示: " + (p2 ? "INPUT BLE DATA ✔" : r2));
                if (p2) B.Se.SendRaw(Encoding.ASCII.GetBytes(m2), "从→主");
                WaitTx(A, 2500);
                byte[] exp2 = Encoding.ASCII.GetBytes(m2);
                byte[] got2 = LastPacket(A);
                Check("主机收到完整载荷", got2 != null, got2 == null ? "(无)" : Hex(got2));
                Check("载荷长度与声明一致", got2 != null && got2.Length == exp2.Length, got2 == null ? "-" : got2.Length + " vs " + exp2.Length);
                Check("载荷逐字节一致", ByteEquals(got2, exp2), got2 == null ? "-" : Hex(got2));

                // 6c 连续多包(逐字节比对)
                Console.WriteLine("\n  [6c] 连续 5 包双向可靠性(逐字节)");
                int okBoth = 0;
                for (int k = 1; k <= 5; k++)
                {
                    string pA = "A2B#" + k;
                    string pB = "B2A#" + k;
                    Drain(A); Drain(B); ClearPackets(A); ClearPackets(B);

                    // 主 → 从
                    string sp = string.Join(" ", Q(A, "AT+SEND=1," + pA.Length + ",3000", 4000, 350).ToArray());
                    if (sp.Contains("INPUT BLE DATA")) { A.Se.SendRaw(Encoding.ASCII.GetBytes(pA), "主→从"); Pump(600); }
                    WaitTx(B, 1200);
                    byte[] gotSlave = LastPacket(B);

                    // 从 → 主
                    string sq = string.Join(" ", Q(B, "AT+SEND=1," + pB.Length + ",3000", 4000, 350).ToArray());
                    if (sq.Contains("INPUT BLE DATA")) { B.Se.SendRaw(Encoding.ASCII.GetBytes(pB), "从→主"); Pump(600); }
                    WaitTx(A, 1200);
                    byte[] gotMaster = LastPacket(A);

                    bool okA = ByteEquals(gotSlave, Encoding.ASCII.GetBytes(pA));
                    bool okB = ByteEquals(gotMaster, Encoding.ASCII.GetBytes(pB));
                    if (okA && okB) okBoth++;
                    Console.WriteLine(string.Format("      第{0}包  主→从 {1}   从→主 {2}{3}", k,
                        okA ? "OK" : "失败", okB ? "OK" : "失败",
                        (okA && okB) ? "" : ("   [" + Hex(gotSlave) + " / " + Hex(gotMaster) + "]")));
                }
                Check("5 包双向逐字节一致", okBoth == 5, okBoth + "/5");

                // ===== 7. 收尾 =====
                Console.WriteLine("\n[7] 断开连接");
                Q(A, "AT+DISCON", 3000, 350);
                Pump(1500);
                List<string> cl2 = Q(A, "AT+CONNECT_LIST?", 3000, 300);
                string after = string.Join(" ", cl2.ToArray());
                bool queryOk = false;
                foreach (string l in cl2) if (l != null && (l.Contains("CONNECT_LIST") || l.Contains("NULL"))) queryOk = true;
                Check("断开后查询成功", queryOk, after);
                Check("断开后列表不含从机", !after.ToUpperInvariant().Contains(B.Mac.ToUpperInvariant()), after);
            }
            catch (Exception ex)
            {
                Console.WriteLine("异常: " + ex.Message);
                _fail++;
            }
            finally
            {
                try { A.Io.Dispose(); } catch { }
                try { B.Io.Dispose(); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? "======== 双机互传验证全部通过 ========"
                : ("======== 失败 " + _fail + " 项 (通过 " + _pass + ") ========"));
            return _fail == 0 ? 0 : 1;
        }

        static void ClearPackets(Dev d)
        {
            lock (d.Packets) { d.Packets.Clear(); d.PacketHandles.Clear(); }
        }

        static byte[] LastPacket(Dev d)
        {
            lock (d.Packets)
            {
                return d.Packets.Count > 0 ? d.Packets[d.Packets.Count - 1] : null;
            }
        }

        static bool ByteEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < d.Length && i < 40; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2")); }
            if (d.Length > 40) sb.Append(" …");
            return sb.ToString();
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(无数据)";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c >= 32 && c < 127) sb.Append(c);
                else sb.Append('?');
            }
            return sb.Append('"').ToString();
        }
    }
}
