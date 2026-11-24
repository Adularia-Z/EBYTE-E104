// serial_io.cs — 串口引擎 + 协议会话层(P0 重构版)
// 关键设计:
//   1) 字节流状态机:识别 +RECEIVED:<h>,<len> 后按声明长度消费原始字节,
//      不再按 \n 切行 → 二进制载荷(含 00 / 0D 0A / FF)完整保真
//   2) 指令队列:同一串口串行执行一条 AT 请求,按命令匹配应答,杜绝错配
//   3) 两步发送(ADV_EXT / SEND)独占会话顺序,超时/错误/断开自动清理
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
        public Action<string> OnError;

        public bool IsOpen { get { return _open; } }
        public string PortName { get; private set; }
        public int Baud { get; private set; }
        public const int MinFrameGapMs = 20;      // 最小帧间隔(手册 5.7 防粘连)

        public static string[] ListPorts()
        {
            string[] arr = SerialPort.GetPortNames();
            List<string> lst = new List<string>();
            foreach (string s in arr) if (s.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) lst.Add(s.ToUpper());
            lst.Sort(delegate(string a, string b)
            {
                int ia, ib;
                int.TryParse(Regex.Replace(a, @"\D", ""), out ia);
                int.TryParse(Regex.Replace(b, @"\D", ""), out ib);
                return ia.CompareTo(ib);
            });
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
            _thr.Name = "serial-rx-" + name;
            _thr.Start();
        }

        private void ReaderLoop()
        {
            byte[] buf = new byte[8192];
            while (_open)
            {
                try
                {
                    if (_sp == null || !_sp.IsOpen) { Fail("串口已关闭"); return; }
                    int n = _sp.BytesToRead;
                    if (n <= 0) { Thread.Sleep(6); continue; }
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

        public void WriteRaw(byte[] data)
        {
            lock (_wlock)
            {
                if (!_open) throw new IOException("串口未打开");
                _sp.Write(data, 0, data.Length);
            }
        }

        /// <summary>写 AT 指令:自动 CRLF + 最小帧间隔</summary>
        public void WriteCmd(string cmd)
        {
            lock (_wlock)
            {
                if (!_open) throw new IOException("串口未打开");
                double gap = (DateTime.UtcNow - _lastWrite).TotalMilliseconds;
                if (gap < MinFrameGapMs) Thread.Sleep((int)(MinFrameGapMs - gap) + 1);
                _lastWrite = DateTime.UtcNow;
                byte[] body = Encoding.ASCII.GetBytes(cmd);
                byte[] frame = new byte[body.Length + 2];
                Array.Copy(body, frame, body.Length);
                frame[body.Length] = 13; frame[body.Length + 1] = 10;
                _sp.Write(frame, 0, frame.Length);
            }
        }

        public void RtsPulse(int times)
        {
            for (int i = 0; i < times; i++)
            {
                try { _sp.RtsEnable = false; Thread.Sleep(150); _sp.RtsEnable = true; Thread.Sleep(250); }
                catch { return; }
            }
        }

        public void Dispose()
        {
            _open = false;
            try { if (_sp != null && _sp.IsOpen) { _sp.DiscardInBuffer(); _sp.Close(); } } catch { }
        }
    }

    // ============ 指令请求 ============
    public class AtRequest
    {
        public string Cmd;                        // 原始指令
        public string ExpectKey;                  // 期望的应答键(如 MAC / ROLE),空=任意
        public Regex ExpectRe;                    // 期望应答正则(可为空)
        public int TimeoutMs = 3000;
        public int QuietMs = 120;
        public DateTime Deadline = DateTime.MinValue;
        public DateTime LastRx = DateTime.MinValue;
        public readonly List<string> Lines = new List<string>();
        public Action<List<string>, bool> Cb;
        public bool Sent = false;
        public bool IsTwoStep = false;            // 两步发送占用会话
        public int PendingLen = -1;               // 待输入字节数
        public Action<byte[]> OnTwoStepInput;     // 输入提示回调
        public DateTime TwoStepDeadline;
    }

    // ============ 协议会话 ============
    public class Session
    {
        public SerialIo Io;
        private readonly object _qLock = new object();
        private readonly Queue<AtRequest> _queue = new Queue<AtRequest>();
        private AtRequest _current = null;         // 正在等待应答的请求
        private readonly List<byte> _rx = new List<byte>();   // 原始字节缓冲
        public volatile bool ScanActive = false;
        public volatile bool ObserverActive = false;
        public int PendingInputNeeded = -1;
        public long TxCount, RxCount, TxBytes, RxBytes;

        // ---- 事件钩子 ----
        public Action<string, string> OnLog;                 // tag,text
        public Action<string> OnLastReply;
        public Action<int> OnPrompt;                         // INPUT …:n
        public Action<string> OnReceived;                    // 合并后的完整接收记录
        public Action<byte[], int, int> OnReceivedBinary;    // 原始载荷,handle,len
        public Action<int, string, string, string> OnScanRow;
        public Action<string> OnObserverLine;
        public Action<string> OnLinkEvent;
        public Action<string, string> OnStateValue;
        public Action<string> OnErrCode;
        public Action<byte[]> OnRawRx;
        public Action<byte[]> OnRawTx;

        // ---- 数据接收状态机 ----
        private enum RxState { Line, AfterReceivedHeader, AfterBleDataTag, Payload }
        private RxState _st = RxState.Line;
        private int _rxHandle = -1, _rxLen = -1;
        private byte[] _payload = null;
        private int _payloadGot = 0;

        private static readonly Regex ReReceivedHdr =
            new Regex(@"^\+RECEIVED:\s*(\d+)\s*,\s*(\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex ReKv =
            new Regex(@"^\+([A-Za-z_0-9]+)\s*=\s*(.*)$", RegexOptions.Compiled);
        private static readonly Regex ReErr = new Regex(@"^\+ERR\s*=\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex RePrompt =
            new Regex(@"^INPUT\s+(?:EXT ADV|BLE DATA)\s*:\s*(\d+)\s*$", RegexOptions.Compiled);
        private static readonly Regex ReScanRow =
            new Regex(@"^(\d+)\s+([0-9A-F]{2}(?::[0-9A-F]{2}){5})\s+(-?\d+)[ \t]*(.*)$", RegexOptions.Compiled);

        private static readonly string[] EventWords =
            { "MODULE POWERUP", "CONNECT TIMEOUT", "ALREADY CONNECTED" };

        public Session(SerialIo io) { Io = io; }

        /// <summary>测试/回放用:直接注入接收字节(不经过真实串口)</summary>
        public void InjectBytes(byte[] data)
        {
            for (int i = 0; i < data.Length; i++) Io.RxQueue.Enqueue(data[i]);
        }

        // ============================================================ 发送入口
        /// <summary>所有 AT 请求的唯一入口:入队 → 串行执行</summary>
        public AtRequest Query(string cmd, Action<List<string>, bool> cb, int timeoutMs, int quietMs)
        {
            AtRequest r = new AtRequest();
            r.Cmd = cmd;
            r.Cb = cb;
            r.TimeoutMs = timeoutMs;
            r.QuietMs = quietMs;
            r.ExpectKey = ExtractKey(cmd);
            lock (_qLock) { _queue.Enqueue(r); }
            Pump();                    // 立刻尝试发送
            return r;
        }

        private static string ExtractKey(string cmd)
        {
            // AT+MAC? → MAC ; AT+NAME=xx → NAME
            Match m = Regex.Match(cmd ?? "", @"^AT\+([A-Za-z_0-9]+)");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }

        /// <summary>两步发送:下发命令并进入等输入状态</summary>
        public AtRequest QueryTwoStep(string cmd, Action<byte[]> onInput, int timeoutMs)
        {
            AtRequest r = new AtRequest();
            r.Cmd = cmd;
            r.ExpectKey = ExtractKey(cmd);
            r.TimeoutMs = timeoutMs;
            r.QuietMs = 150;
            r.IsTwoStep = true;
            r.OnTwoStepInput = onInput;
            lock (_qLock) { _queue.Enqueue(r); }
            Pump();
            return r;
        }

        /// <summary>两步发送:写入原始载荷(不排队,独占)</summary>
        public bool SendTwoStepPayload(byte[] data)
        {
            if (_current == null || !_current.IsTwoStep) return false;
            if (_current.PendingLen < 0) return false;
            try { Io.WriteRaw(data); }
            catch (Exception ex)
            {
                // 回放/离线模式:无真实串口,仅记录
                if (OnLog != null) OnLog("err", "写入失败: " + ex.Message);
            }
            TxCount++; TxBytes += data.Length;
            if (OnRawTx != null) OnRawTx(data);
            if (OnLog != null) OnLog("tx", "[数据] " + data.Length + " 字节");
            _current.PendingLen = -1;              // 已提交,等待最终 +OK
            _current.LastRx = DateTime.UtcNow;
            return true;
        }

        public void SendRaw(byte[] data, string note)
        {
            Io.WriteRaw(data);
            TxCount++; TxBytes += data.Length;
            if (OnRawTx != null) OnRawTx(data);
            if (OnLog != null)
            {
                bool pr = true;
                foreach (byte b in data) if (b < 0x20 || b >= 0x7f) { pr = false; break; }
                StringBuilder sb = new StringBuilder();
                sb.Append("[") .Append(note).Append("] ").Append(data.Length).Append("字节: ");
                if (pr) sb.Append(Encoding.ASCII.GetString(data));
                else for (int i = 0; i < data.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(data[i].ToString("X2")); }
                OnLog("tx", sb.ToString());
            }
        }

        public void Send(string cmd)
        {
            byte[] body = Encoding.ASCII.GetBytes(cmd + "\r\n");
            Io.WriteCmd(cmd);
            TxCount++; TxBytes += body.Length;
            if (OnRawTx != null) OnRawTx(body);
            if (OnLog != null) OnLog("tx", cmd + "\\r\\n");
        }

        // ============================================================ 主循环
        /// <summary>由宿主定时器驱动(UI 线程)</summary>
        public void Pump()
        {
            DranBytes();
            ParseRx();
            Settle();
            PumpQueue();
        }

        private void DranBytes()
        {
            byte b;
            List<byte> chunk = null;
            while (Io.RxQueue.TryDequeue(out b))
            {
                RxCount++;
                if (chunk == null) chunk = new List<byte>(512);
                chunk.Add(b);
                _rx.Add(b);
            }
            if (chunk != null)
            {
                byte[] arr = chunk.ToArray();
                RxBytes += arr.Length;
                if (OnRawRx != null) OnRawRx(arr);
            }
        }

        // ============================================================ 字节流解析
        private void ParseRx()
        {
            int guard = 0;
            while (_rx.Count > 0 && guard++ < 20000)
            {
                if (_st == RxState.Payload)
                {
                    int need = _rxLen - _payloadGot;
                    int take = Math.Min(need, _rx.Count);
                    Array.Copy(_rx.ToArray(), 0, _payload, _payloadGot, take);
                    _rx.RemoveRange(0, take);
                    _payloadGot += take;
                    if (_payloadGot >= _rxLen)
                    {
                        byte[] full = new byte[_rxLen];
                        Array.Copy(_payload, full, _rxLen);
                        EmitPayload(full, _rxHandle);
                        _st = RxState.Line;
                        _payload = null; _payloadGot = 0;
                    }
                    continue;
                }

                // 行模式:找 \n
                int nl = _rx.IndexOf((byte)'\n');
                if (nl < 0) break;
                byte[] raw = new byte[nl];
                _rx.CopyTo(0, raw, 0, nl);
                _rx.RemoveRange(0, nl + 1);
                int end = raw.Length;
                while (end > 0 && (raw[end - 1] == 13 || raw[end - 1] == 10)) end--;
                if (end == 0) continue;
                string line = Encoding.UTF8.GetString(raw, 0, end).Trim();
                if (line.Length == 0) continue;

                if (_st == RxState.AfterReceivedHeader)
                {
                    Match mp = RePrompt.Match(line);
                    if (mp.Success) { _st = RxState.Line; DispatchLine(line); continue; }
                    if (string.Equals(line, "BLE DATA", StringComparison.Ordinal))
                    { _st = RxState.Payload; _payloadGot = 0; continue; }
                    // 不是标签:按纯文本数据处理(兼容无标签固件)
                    EmitTextPayload(line, _rxHandle, _rxLen);
                    _st = RxState.Line;
                    continue;
                }

                Match mh = ReReceivedHdr.Match(line);
                if (mh.Success)
                {
                    int h, l;
                    int.TryParse(mh.Groups[1].Value, out h);
                    int.TryParse(mh.Groups[2].Value, out l);
                    _rxHandle = h; _rxLen = l;
                    _payload = new byte[Math.Max(0, l)];
                    _payloadGot = 0;
                    if (OnLog != null) OnLog("rx", line);
                    _st = l > 0 ? RxState.AfterReceivedHeader : RxState.Line;
                    if (l == 0) EmitPayload(new byte[0], h);
                    continue;
                }

                DispatchLine(line);
            }
        }

        private void EmitPayload(byte[] data, int handle)
        {
            // 日志:文本安全则按文本,否则 HEX
            bool printable = true;
            foreach (byte b in data) if (b < 0x20 || b >= 0x7f) { printable = false; break; }
            string shown = printable ? Encoding.ASCII.GetString(data)
                                      : BitConverter.ToString(data).Replace("-", " ");
            if (OnLog != null) OnLog("rx", "  ↳ [" + data.Length + "B] " + shown);
            if (OnReceivedBinary != null) OnReceivedBinary(data, handle, data.Length);
            if (OnReceived != null) OnReceived("+RECEIVED:" + handle + "," + data.Length + "  " + shown);
        }

        private void EmitTextPayload(string text, int handle, int declared)
        {
            if (OnLog != null) OnLog("rx", "  ↳ " + text);
            byte[] b = Encoding.ASCII.GetBytes(text);
            if (OnReceivedBinary != null) OnReceivedBinary(b, handle, declared);
            if (OnReceived != null) OnReceived("+RECEIVED:" + handle + "," + declared + "  " + text);
        }

        // ============================================================ 行分发
        private void DispatchLine(string line)
        {
            if (OnLastReply != null) OnLastReply(line);

            Match mp = RePrompt.Match(line);
            if (mp.Success && _current != null)
            {
                int need;
                if (int.TryParse(mp.Groups[1].Value, out need))
                {
                    PendingInputNeeded = need;
                    _current.PendingLen = need;
                    _current.TwoStepDeadline = DateTime.UtcNow.AddMilliseconds(_current.TimeoutMs);
                    if (_current.OnTwoStepInput != null) _current.OnTwoStepInput(new byte[0]);
                    if (OnPrompt != null) OnPrompt(need);
                }
            }

            Match me = ReErr.Match(line);
            if (me.Success)
            {
                if (OnLog != null) OnLog("err", line);
                if (OnErrCode != null) OnErrCode(line);
            }
            else if (IsEvent(line))
            {
                if (OnLog != null) OnLog("event", line);
                if (OnLinkEvent != null) OnLinkEvent(line);
            }
            else
            {
                if (OnLog != null) OnLog("rx", line);
            }

            if (ScanActive)
            {
                Match ms = ReScanRow.Match(line);
                if (ms.Success && OnScanRow != null)
                {
                    int idx; int.TryParse(ms.Groups[1].Value, out idx);
                    OnScanRow(idx, ms.Groups[2].Value, ms.Groups[3].Value, ms.Groups[4].Value.Trim());
                }
            }
            if (ObserverActive && line.StartsWith("+MAC:", StringComparison.Ordinal) && OnObserverLine != null)
                OnObserverLine(line);

            Match kv = ReKv.Match(line);
            if (kv.Success && OnStateValue != null)
                OnStateValue(kv.Groups[1].Value.ToUpperInvariant(), kv.Groups[2].Value.Trim());

            // ---- 应答归属:只给当前请求 ----
            if (_current != null && _current.Sent)
            {
                _current.Lines.Add(line);
                _current.LastRx = DateTime.UtcNow;
            }
        }

        private static bool IsEvent(string line)
        {
            if (line.StartsWith("+", StringComparison.Ordinal)) return false;
            if (line.IndexOf("CONNECTD", StringComparison.Ordinal) >= 0) return true;
            if (line.IndexOf("DISCONNECT", StringComparison.Ordinal) >= 0) return true;
            if (line.IndexOf("RECEIVE TIMEOUT", StringComparison.Ordinal) >= 0) return true;
            for (int i = 0; i < EventWords.Length; i++)
                if (line.IndexOf(EventWords[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ============================================================ 队列推进
        private void PumpQueue()
        {
            lock (_qLock)
            {
                if (_current == null && _queue.Count > 0)
                {
                    _current = _queue.Dequeue();
                    try { Send(_current.Cmd); }
                    catch (Exception ex)
                    {
                        // 回放/离线模式下串口不可写:仍推进请求,让注入的应答能正确归属
                        if (OnLog != null) OnLog("err", "发送失败: " + ex.Message);
                    }
                    _current.Sent = true;
                    _current.Deadline = DateTime.UtcNow.AddMilliseconds(_current.TimeoutMs);
                    _current.LastRx = DateTime.UtcNow;
                }
            }
        }

        private void Settle()
        {
            AtRequest cur = _current;
            if (cur == null) return;
            DateTime now = DateTime.UtcNow;

            // 两步:等待载荷提交;已提交则等最终确认
            if (cur.IsTwoStep)
            {
                if (cur.PendingLen >= 0)
                {
                    if (now > cur.TwoStepDeadline)
                    {
                        if (OnLog != null) OnLog("err", "数据输入超时,已清理本次两步发送");
                        Finish(cur, false);
                    }
                    return;
                }
            }

            bool got = cur.Lines.Count > 0;
            bool idle = got && (now - cur.LastRx).TotalMilliseconds >= cur.QuietMs;
            if (idle || now > cur.Deadline)
            {
                Finish(cur, got);
            }
        }

        private void Finish(AtRequest r, bool ok)
        {
            if (r == null) return;
            if (ReferenceEquals(_current, r)) _current = null;
            r.PendingLen = -1;
            PendingInputNeeded = -1;
            if (r.Cb != null)
            {
                List<string> snap = new List<string>(r.Lines);
                try { r.Cb(snap, ok); } catch { }
            }
            PumpQueue();
        }

        /// <summary>取消所有在途请求(断开/复位时调用)</summary>
        public void AbortAll(string why)
        {
            lock (_qLock)
            {
                List<AtRequest> all = new List<AtRequest>();
                if (_current != null) all.Add(_current);
                while (_queue.Count > 0) all.Add(_queue.Dequeue());
                _current = null;
                for (int i = 0; i < all.Count; i++)
                {
                    try { if (all[i].Cb != null) all[i].Cb(new List<string>(), false); } catch { }
                }
            }
            _st = RxState.Line;
            _payload = null; _payloadGot = 0;
            PendingInputNeeded = -1;
        }
    }
}
