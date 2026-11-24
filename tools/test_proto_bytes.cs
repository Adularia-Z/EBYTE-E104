// test_proto_bytes.cs — 字节流状态机离线回放验证(无需硬件)
// 覆盖「二进制接收 / 应答归属 / 串行队列 / 两步发送」四个关键场景
using System;
using System.Collections.Generic;
using System.Text;

namespace E104
{
    static class TestProtoBytes
    {
        static int _pass, _fail;

        static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + name + (detail != null ? "   → " + detail : ""));
            if (ok) _pass++; else _fail++;
        }

        static string Hex(byte[] d)
        {
            if (d == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < d.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2")); }
            return sb.ToString();
        }

        /// <summary>可注入接收字节的假串口</summary>
        class FakeIo : SerialIo
        {
            public void Push(byte[] data)
            {
                for (int i = 0; i < data.Length; i++) RxQueue.Enqueue(data[i]);
            }
            public void PushAscii(string s) { Push(Encoding.ASCII.GetBytes(s)); }
        }

        static void Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            try
            {
                Case1_Binary();
                Case2_Fragment();
                Case3_Attribution();
                Case4_Queue();
                Case5_TwoStep();
            }
            catch (Exception ex)
            {
                Console.WriteLine("  测试异常: " + ex.Message);
                _fail++;
            }

            Console.WriteLine();
            Console.WriteLine(_fail == 0
                ? "======== 字节流验证全部通过 ========"
                : ("======== 失败 " + _fail + " 项 (通过 " + _pass + ") ========"));
        }

        // ---------- 场景1:二进制载荷完整保真 ----------
        static void Case1_Binary()
        {
            Console.WriteLine("\n[场景1] 二进制载荷(含 00 / 0D 0A / FF)");
            FakeIo io = new FakeIo();
            Session se = new Session(io);
            se.OnLog = delegate { };
            byte[] got = null; int h = -1, l = -1;
            se.OnReceivedBinary = delegate(byte[] d, int hh, int ll) { got = d; h = hh; l = ll; };

            byte[] payload = new byte[] { 0x41, 0x00, 0x0D, 0x0A, 0xFF, 0x5A };
            se.InjectBytes(Encoding.ASCII.GetBytes("+RECEIVED:1,6\r\nBLE DATA\r\n"));
            se.Pump();
            se.InjectBytes(payload);
            se.Pump();

            bool same = got != null && got.Length == payload.Length;
            if (same) for (int i = 0; i < payload.Length; i++) if (got[i] != payload[i]) { same = false; break; }
            Check("载荷字节完全一致", same, Hex(got));
            Check("handle 正确", h == 1, h.ToString());
            Check("声明长度正确", l == 6, l.ToString());
        }

        // ---------- 场景2:分片到达 ----------
        static void Case2_Fragment()
        {
            Console.WriteLine("\n[场景2] 分片到达(逐字节 Pump)");
            FakeIo io = new FakeIo();
            Session se = new Session(io);
            se.OnLog = delegate { };
            byte[] got = null;
            se.OnReceivedBinary = delegate(byte[] d, int hh, int ll) { got = d; };

            byte[] p = new byte[] { 0x10, 0x0D, 0x0A, 0x20, 0x00, 0x7F, 0xFE };
            se.InjectBytes(Encoding.ASCII.GetBytes("+RECEIVED:2,7\r\nBLE DATA\r\n"));
            se.Pump();
            for (int i = 0; i < p.Length; i++)
            {
                se.InjectBytes(new byte[] { p[i] });
                se.Pump();
            }
            bool ok = got != null && got.Length == p.Length;
            if (ok) for (int i = 0; i < p.Length; i++) if (got[i] != p[i]) { ok = false; break; }
            Check("分片到达仍完整", ok, Hex(got));
        }

        // ---------- 场景3:载荷后的 AT 应答归属 ----------
        static void Case3_Attribution()
        {
            Console.WriteLine("\n[场景3] 载荷之后的 AT 应答仍正确归属");
            FakeIo io = new FakeIo();
            Session se = new Session(io);
            se.OnLog = delegate { };
            List<string> lines = new List<string>();
            se.Query("AT+MAC?", delegate(List<string> l, bool ok) { lines.AddRange(l); }, 2000, 100);
            se.Pump();

            // 先来一段无关二进制载荷
            io.PushAscii("+RECEIVED:1,3\r\nBLE DATA\r\n");
            io.Push(new byte[] { 0x00, 0x01, 0x02 });
            se.Pump();
            // 再来真正的 MAC 应答
            io.PushAscii("+MAC=4C:5B:B3:C7:33:16\r\n");
            for (int i = 0; i < 30; i++) { se.Pump(); System.Threading.Thread.Sleep(8); }

            string joined = string.Join(" | ", lines.ToArray());
            Check("MAC 应答被正确匹配", joined.Contains("4C:5B:B3:C7:33:16"), joined);
            Check("载荷未被误当应答", !joined.Contains("BLE DATA"), null);
        }

        // ---------- 场景4:串行队列 ----------
        static void Case4_Queue()
        {
            Console.WriteLine("\n[场景4] 串行队列:一条响应只结算对应请求");
            FakeIo io = new FakeIo();
            Session se = new Session(io);
            se.OnLog = delegate { };
            List<string> a = new List<string>(), b = new List<string>();
            se.Query("AT+NAME?", delegate(List<string> l, bool ok) { a.AddRange(l); }, 2000, 80);
            se.Query("AT+ROLE?", delegate(List<string> l, bool ok) { b.AddRange(l); }, 2000, 80);
            se.Pump();

            io.PushAscii("+NAME=CDEBYTE_3316\r\n");
            for (int i = 0; i < 25; i++) { se.Pump(); System.Threading.Thread.Sleep(8); }
            io.PushAscii("+ROLE=0\r\n");
            for (int i = 0; i < 25; i++) { se.Pump(); System.Threading.Thread.Sleep(8); }

            Check("NAME 只收到自己的应答", a.Count == 1 && a[0].Contains("+NAME"), string.Join("|", a.ToArray()));
            Check("ROLE 只收到自己的应答", b.Count == 1 && b[0].Contains("+ROLE"), string.Join("|", b.ToArray()));
        }

        // ---------- 场景5:两步发送 ----------
        static void Case5_TwoStep()
        {
            Console.WriteLine("\n[场景5] 两步发送(AT+SEND)");
            FakeIo io = new FakeIo();
            Session se = new Session(io);
            se.OnLog = delegate { };
            List<byte[]> sentPayloads = new List<byte[]>();
            AtRequest r = se.QueryTwoStep("AT+SEND=1,5,3000", delegate(byte[] d) { }, 4000);
            se.Pump();

            io.PushAscii("+OK\r\nINPUT BLE DATA:5\r\n");
            for (int i = 0; i < 15; i++) { se.Pump(); System.Threading.Thread.Sleep(8); }
            Check("收到输入提示长度 5", r.PendingLen == 5, r.PendingLen.ToString());

            bool submitted = se.SendTwoStepPayload(Encoding.ASCII.GetBytes("HELLO"));
            Check("载荷可提交", submitted, null);

            io.PushAscii("+OK\r\n");
            for (int i = 0; i < 25; i++) { se.Pump(); System.Threading.Thread.Sleep(8); }
            Check("两步结束后队列可继续", r.PendingLen == -1, r.PendingLen.ToString());
        }
    }
}
