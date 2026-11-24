// serial_io.cs — 串口引擎 + 协议会话层(C#5;.NET Framework 4.8 内置 System.IO.Ports)
// 会话逻辑已在真机验证(Python 版),本文件为其等价移植:
//   行组装 / 应答等待器(静默+超时) / 20ms 防粘连 / 交互式输入提示 / 流式路由 / 事件横幅 / 键值回填
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace E104
{
    // ============ 串口 IO ============
    public class SerialIo : IDisposable
    {
        private SerialPort _sp;
        private Thread _thr;
        private volatile bool _open = false;
        private readonly object _wlock = new object();
        private DateTime _lastWrite = DateTime.MinValue;
        public ConcurrentQueue<byte> RxQueue = new ConcurrentQueue<byte>();
        public Action<string> OnError;              // 读线程异常(拔出等)

        public bool IsOpen { get { return _open; } }
        public string PortName { get; private set; }
        public int Baud { get; private set; }

        public static string[] ListPorts()
        {
            string[] arr = SerialPort.GetPortNames();
            List<string> lst = new List<string>();
            foreach (string s in arr) if (s.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) lst.Add(s.ToUpper());
            lst.Sort(delegate(string a, string b) { return int.Parse(Regex.Replace(a, @"\D", "")).CompareTo(int.Parse(Regex.Replace(b, @"\D", ""))); });
            return lst.ToArray();
        }

        public void Open(string name, int baud)
        {
            PortName = name; Baud = baud;
            _sp = new SerialPort(name, baud, Parity.None, 8, StopBits.One);
            _sp.ReadTimeout = 50;
            _sp.WriteTimeout = 2000;
            _sp.DtrEnable = true;
            _sp.RtsEnable = true;
            _sp.ReadBufferSize = 262144;
            _sp.Open();
            _sp.DiscardInBuffer();
            _open = true;
            _thr = new Thread(ReaderLoop);
            _thr.IsBackground = true;
            _thr.Name = "serial-rx";
            _thr.Start();
        }

        private void ReaderLoop()
        {
            byte[] buf = new byte[8192];
            while (_open)
            {
                try
                {
                    if (!_sp.IsOpen) { Fail("串口已关闭"); return; }
                    int n = _sp.BytesToRead;
                    if (n <= 0) { Thread.Sleep(8); continue; }
                    if (n > buf.Length) n = buf.Length;
                    n = _sp.Read(buf, 0, n);
                    for (int i = 0; i < n; i++) RxQueue.Enqueue(buf[i]);
                }
                catch (Exception ex) { Fail(ex.Message); return; }
            }
        }

        private void Fail(string msg)
        {
            _open = false;
            if (OnError != null) OnError(msg);
        }

        /// <summary>写裸字节(无间隔保护,用于交互式数据)</summary>
        public void WriteRaw(byte[] data)
        {
            lock (_wlock)
            {
                if (!_open) throw new IOException("串口未打开");
                _sp.Write(data, 0, data.Length);
            }
        }

        /// <summary>写 AT 指令:自动加 CRLF,防粘连最小 20ms 帧间隔(手册 5.7)</summary>
        public void WriteCmd(string cmd)
        {
            lock (_wlock)
            {
                if (!_open) throw new IOException("串口未打开");
                double gap = (DateTime.UtcNow - _lastWrite).TotalMilliseconds;
                if (gap < 20.0) Thread.Sleep((int)(20.0 - gap) + 1);
                _lastWrite = DateTime.UtcNow;
                byte[] body = Encoding.ASCII.GetBytes(cmd);
                byte[] frame = new byte[body.Length + 2];
                Array.Copy(body, frame, body.Length);
                frame[body.Length] = 13; frame[body.Length + 1] = 10;
                _sp.Write(frame, 0, frame.Length);
            }
        }

        /// <summary>RTS 拉低→拉高脉冲(模块 CTS 上升沿唤醒)</summary>
        public void RtsPulse(int times)
        {
            for (int i = 0; i < times; i++)
            {
                _sp.RtsEnable = false;
                Thread.Sleep(150);
                _sp.RtsEnable = true;
                Thread.Sleep(250);
            }
        }

        public void Dispose()
        {
            _open = false;
            try { if (_sp != null && _sp.IsOpen) { _sp.DiscardInBuffer(); _sp.Close(); } }
            catch { }
        }
    }

    // ============ 协议会话 ============
    public class CmdWaiter
    {
        public string CmdText;
        public List<string> Lines = new List<string>();
        public DateTime Deadline;
        public DateTime LastRx;
        public int QuietMs;
        public Action<List<string>, bool> Cb;
    }

    public class Session
    {
        public SerialIo Io;
        private readonly List<CmdWaiter> _waiters = new List<CmdWaiter>();
        private readonly List<byte> _linebuf = new List<byte>();
        public volatile bool ScanActive = false;
        public volatile bool ObserverActive = false;
        public int PendingInputNeeded = -1;      // INPUT 提示后等待的字节数
        public long TxCount, RxCount;

        // ---- 事件钩子(UI 都在此层之上) ----
        public Action<string, string> OnLog;                       // tag,text
        public Action<string> OnLastReply;
        public Action<int> OnPrompt;                               // INPUT …:n
        public Action<string> OnReceived;                          // +RECEIVED 行
        public Action<int, string, string, string> OnScanRow;      // idx,mac,rssi,name
        public Action<string> OnObserverLine;
        public Action<string> OnLinkEvent;
        public Action<string, string> OnStateValue;               // key,value
        public Action<string> OnErrCode;
        public Action<byte[]> OnRawRx;                             // 原始接收字节流(串口助手 HEX 视图)
        public Action<byte[]> OnRawTx;                             // 原始发送字节帧
        public long TxBytes, RxBytes;                              // 字节计数(串口助手风格)

        private static readonly Regex ReKv = new Regex(@"^\+([A-Za-z_0-9]+)=(.*)$", RegexOptions.Compiled);
        private static readonly Regex ReScanRow = new Regex(@"^(\d+)\s+([0-9A-F:]{17})\s+(-?\d+)[ \t]*(.*)$", RegexOptions.Compiled);
        private static readonly Regex RePrompt = new Regex(@"^INPUT (?:EXT ADV|BLE DATA):(\d+)$", RegexOptions.Compiled);
        // "+RECEIVED:<handle>,<len>"
        private static readonly Regex ReReceived = new Regex(@"^\+RECEIVED:\s*(\d+)\s*,\s*(\d+)\s*$", RegexOptions.Compiled);

        // BLE 数据三行协议的中间状态
        private bool _rxPending = false;
        private string _rxHandle = "", _rxLen = "";

        private static readonly string[] EventWords = new string[]
        { "CONNECTD", "CONNECTED", "DISCONNECTED", "DISCONNECT", "RECEIVE TIMEOUT", "MODULE POWERUP", "CONNECT TIMEOUT" };

        public Session(SerialIo io) { Io = io; }

        public void Send(string cmd)
        {
            byte[] body = Encoding.ASCII.GetBytes(cmd + "\r\n");
            Io.WriteCmd(cmd);
            TxCount++; TxBytes += body.Length;
            if (OnRawTx != null) OnRawTx(body);
            if (OnLog != null) OnLog("tx", cmd + "\\r\\n");
        }

        public void SendRaw(byte[] data, string note)
        {
            Io.WriteRaw(data);
            TxCount++; TxBytes += data.Length;
            if (OnRawTx != null) OnRawTx(data);
            StringBuilder sb = new StringBuilder();
            bool printable = true;
            foreach (byte b in data) if (b < 0x20 || b >= 0x7f) { printable = false; break; }
            sb.Append("[" + note + "] ").Append(data.Length).Append("字节: ");
            if (printable) sb.Append(Encoding.ASCII.GetString(data)); else HexOf(data, sb);
            if (OnLog != null) OnLog("tx", sb.ToString());
        }
        private static void HexOf(byte[] data, StringBuilder sb)
        {
            for (int i = 0; i < data.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
        }

        public CmdWaiter Query(string cmd, Action<List<string>, bool> cb, int timeoutMs, int quietMs)
        {
            CmdWaiter w = new CmdWaiter();
            w.CmdText = cmd; w.Cb = cb;
            w.QuietMs = quietMs;
            w.Deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_waiters) { _waiters.Add(w); }
            Send(cmd);
            return w;
        }

        /// <summary>由宿主定时器驱动(本方法须在宿主线程调用;UI=WinForms Timer 线程)</summary>
        public void Pump()
        {
            // 原始字节流:整批取出后上报(串口助手 HEX 视图)
            byte b;
            List<byte> drained = null;
            while (Io.RxQueue.TryDequeue(out b))
            {
                RxCount++;
                if (drained == null) drained = new List<byte>(256);
                drained.Add(b);
                _linebuf.Add(b);
            }
            if (drained != null)
            {
                byte[] chunk = drained.ToArray();
                RxBytes += chunk.Length;
                if (OnRawRx != null) OnRawRx(chunk);
            }
            while (true)
            {
                int idx = _linebuf.IndexOf((byte)'\n');
                if (idx < 0) break;
                byte[] raw = new byte[idx];
                _linebuf.CopyTo(0, raw, 0, idx);
                _linebuf.RemoveRange(0, idx + 1);
                int end = raw.Length; while (end > 0 && (raw[end - 1] == 13 || raw[end - 1] == 10)) end--;
                if (end == 0) continue;
                string line = Encoding.UTF8.GetString(raw, 0, end).Trim();
                if (line.Length > 0) DispatchLine(line);
            }
            // 结算等待器
            DateTime now = DateTime.UtcNow;
            List<CmdWaiter> done = new List<CmdWaiter>();
            lock (_waiters)
            {
                for (int i = 0; i < _waiters.Count; i++)
                {
                    CmdWaiter w = _waiters[i];
                    bool got = w.Lines.Count > 0;
                    bool idle = (now - w.LastRx).TotalMilliseconds >= w.QuietMs;
                    if (got && idle && w.LastRx > DateTime.MinValue) done.Add(w);
                    else if (now > w.Deadline) done.Add(w);
                }
                for (int i = 0; i < done.Count; i++) _waiters.Remove(done[i]);
            }
            for (int i = 0; i < done.Count; i++)
            {
                CmdWaiter w = done[i];
                bool ok = w.Lines.Count > 0;
                if (w.Cb != null)
                {
                    List<string> snap = new List<string>(w.Lines);
                    try { w.Cb(snap, ok); } catch { }
                }
            }
        }

        public void DrainWaitForComplete() { }   // 兼容占位

        private void DispatchLine(string line)
        {
            if (OnLastReply != null) OnLastReply(line);

            Match mp = RePrompt.Match(line);
            if (mp.Success) PendingInputNeeded = int.Parse(mp.Groups[1].Value);

            // ---- BLE 数据接收三行协议(实测 7413-1-16) ----
            //   第1行: +RECEIVED:<handle>,<len>
            //   第2行: BLE DATA
            //   第3行: <实际数据>            ← 必须与提示一起上报,否则用户看不到内容
            if (_rxPending || line.StartsWith("+RECEIVED:", StringComparison.Ordinal))
            {
                Match mr = ReReceived.Match(line);
                if (mr.Success)
                {
                    _rxPending = true;
                    _rxHandle = mr.Groups[1].Value;
                    _rxLen = mr.Groups[2].Value;
                    if (OnLog != null) OnLog("rx", line);
                    return;                        // 提示行已记录,等待正文
                }
                if (_rxPending && string.Equals(line, "BLE DATA", StringComparison.Ordinal))
                {
                    if (OnLog != null) OnLog("rx", line);   // 中间标记行
                    return;                        // 继续等待真正的数据行
                }
                if (_rxPending)
                {
                    // 这一行就是数据正文:合并成一条完整记录上报
                    _rxPending = false;
                    string merged = "+RECEIVED:" + _rxHandle + "," + _rxLen + "  " + line;
                    if (OnLog != null) OnLog("rx", "  ↳ " + line);
                    if (OnReceived != null) OnReceived(merged);
                    // 不 return:仍走下方等待器/状态回填逻辑
                }
            }

            if (line.StartsWith("+ERR=", StringComparison.Ordinal))
            {
                if (OnLog != null) OnLog("err", line);
                if (OnErrCode != null) OnErrCode(line);
            }
            else if (StartsWithAnyEvent(line))
            {
                if (OnLog != null) OnLog("event", line);
                if (OnLinkEvent != null) OnLinkEvent(line);
            }
            else
            {
                // +RECEIVED 提示行已在上面处理,避免重复打印
                if (!line.StartsWith("+RECEIVED:", StringComparison.Ordinal))
                {
                    if (OnLog != null) OnLog("rx", line);
                }
            }

            if (ScanActive)
            {
                Match ms = ReScanRow.Match(line);
                if (ms.Success && OnScanRow != null)
                    OnScanRow(int.Parse(ms.Groups[1].Value), ms.Groups[2].Value, ms.Groups[3].Value, ms.Groups[4].Value.Trim());
            }
            if (ObserverActive && line.StartsWith("+MAC:", StringComparison.Ordinal) && OnObserverLine != null)
                OnObserverLine(line);

            lock (_waiters)
            {
                for (int i = 0; i < _waiters.Count; i++)
                {
                    _waiters[i].Lines.Add(line);
                    if (_waiters[i].LastRx == DateTime.MinValue) _waiters[i].LastRx = DateTime.UtcNow;
                }
            }

            if (!line.StartsWith("+ERR=", StringComparison.Ordinal))
            {
                Match kv = ReKv.Match(line);
                if (kv.Success && OnStateValue != null)
                    OnStateValue(kv.Groups[1].Value.ToUpperInvariant(), kv.Groups[2].Value.Trim());
            }
        }

        private static bool StartsWithAnyEvent(string line)
        {
            if (line.StartsWith("+", StringComparison.Ordinal)) return false;
            for (int i = 0; i < EventWords.Length; i++)
                if (line.IndexOf(EventWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
