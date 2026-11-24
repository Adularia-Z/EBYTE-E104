// multi_device.cs — 多模组会话管理(≤5 路):每路独立串口+协议会话,支持主从自动分配与点对点连接
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace E104
{
    /// <summary>一个已接入的模组:串口 + 协议会话 + 角色/配置快照</summary>
    public class DeviceSession
    {
        public string PortName;
        public int Baud;
        public SerialIo Io;
        public Session Se;
        public string Mac = "-";
        public string Name = "-";
        public string Version = "-";
        public int Role = -1;                 // -1 未知,0从/1主/2主从/3Beacon
        public bool Ready = false;            // 握手完成
        public string LastError = "";
        public int Index = 0;                 // 0..4
        /// <summary>本设备的参数快照(由应答回填),与其它模组隔离</summary>
        public readonly Dictionary<string, string> State = new Dictionary<string, string>();
        /// <summary>本设备的连接 handle(从 CONNECTD 事件解析)</summary>
        public int LastHandle = -1;

        public string RoleText
        {
            get
            {
                if (Role < 0) return "未知";
                string[] n = { "从(0)", "主(1)", "主从(2)", "Beacon(3)" };
                return Role < n.Length ? n[Role] : "?";
            }
        }
        public string Title { get { return "M" + (Index + 1) + " " + PortName + " · " + RoleText; } }
    }

    /// <summary>多模组管理器:最多 5 路;统一泵/tick;主从分配与点对点连接</summary>
    public class MultiDevice
    {
        public const int MaxDevices = 5;
        public readonly List<DeviceSession> Devices = new List<DeviceSession>();

        private DeviceSession _active = null;      // 按对象引用追踪,移除其它设备不会改目标

        public DeviceSession Active
        {
            get
            {
                if (_active != null && Devices.Contains(_active)) return _active;
                _active = Devices.Count > 0 ? Devices[0] : null;
                return _active;
            }
            set
            {
                if (value != null && Devices.Contains(value)) _active = value;
            }
        }

        /// <summary>兼容旧的按下标设置(仅用于 UI 列表点击)</summary>
        public int ActiveIndex
        {
            get { DeviceSession a = Active; return a == null ? -1 : a.Index; }
            set
            {
                if (value >= 0 && value < Devices.Count) _active = Devices[value];
            }
        }

        public int Count { get { return Devices.Count; } }
        public bool CanAdd { get { return Devices.Count < MaxDevices; } }

        /// <summary>所有已接入模组的主循环(25ms)</summary>
        public void PumpAll()
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                DeviceSession d = Devices[i];
                try { if (d.Se != null) d.Se.Pump(); } catch { }
            }
        }

        public void DisposeAll()
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                try { Devices[i].Se.AbortAll("断开"); } catch { }
                try { if (Devices[i].Io != null) Devices[i].Io.Dispose(); } catch { }
            }
            Devices.Clear();
            _active = null;
        }

        public DeviceSession Add(string port, int baud, Action<string> onError)
        {
            if (!CanAdd) return null;
            SerialIo io = new SerialIo();
            io.OnError = onError;
            io.Open(port, baud);
            DeviceSession d = new DeviceSession();
            d.PortName = port; d.Baud = baud; d.Io = io;
            d.Se = new Session(io);
            d.Index = Devices.Count;
            Devices.Add(d);
            if (_active == null) _active = d;      // 首个设备自动成为目标
            return d;
        }

        /// <summary>
        /// 移除设备。规则:
        ///  - 移除的不是目标 → 目标保持不变(不因下标变化而跳走)
        ///  - 移除的正是目标 → 优先选同端口的新会话,否则选列表第一个,并标记目标已失效
        /// </summary>
        public bool Remove(DeviceSession d)
        {
            if (d == null || !Devices.Contains(d)) return false;
            bool wasActive = ReferenceEquals(_active, d);
            try { d.Se.AbortAll("断开"); } catch { }
            try { if (d.Io != null) d.Io.Dispose(); } catch { }
            Devices.Remove(d);
            for (int i = 0; i < Devices.Count; i++) Devices[i].Index = i;
            if (wasActive)
            {
                _active = Devices.Count > 0 ? Devices[0] : null;
                return true;                       // 通知调用方:目标已被移除
            }
            return false;
        }
    }

    /// <summary>主从分配方案</summary>
    public class RolePlan
    {
        public DeviceSession Master;
        public List<DeviceSession> Slaves = new List<DeviceSession>();
        public bool Ok;
        public string Message = "";
    }

    public static class RolePlanner
    {
        /// <summary>
        /// 主从自动分配:
        ///  - 全从角色:信号最强(RSSI 无法读)按端口排序,取第一个设为主
        ///  - 已有一台主:其余设从
        ///  - 主从一体:可选
        /// </summary>
        public static RolePlan PlanMasterSlave(List<DeviceSession> devs, bool preferCombined)
        {
            RolePlan p = new RolePlan();
            if (devs == null || devs.Count < 2)
            {
                p.Message = "至少需要 2 个模组才能分配主从";
                return p;
            }
            List<DeviceSession> sorted = new List<DeviceSession>(devs);
            sorted.Sort(delegate(DeviceSession a, DeviceSession b)
            {
                int ra = a.Role < 0 ? 99 : a.Role;
                int rb = b.Role < 0 ? 99 : b.Role;
                if (ra != rb) return ra.CompareTo(rb);
                return a.Index.CompareTo(b.Index);
            });
            DeviceSession master = null;
            for (int i = 0; i < sorted.Count; i++)
                if (sorted[i].Role == 1 || sorted[i].Role == 2) { master = sorted[i]; break; }
            if (master == null) master = sorted[0];

            p.Master = master;
            for (int i = 0; i < devs.Count; i++)
                if (devs[i] != master) p.Slaves.Add(devs[i]);
            p.Ok = true;
            p.Message = string.Format("主: {0}  |  从: {1} 台 ({2}){3}",
                master.PortName, p.Slaves.Count,
                string.Join(", ", p.Slaves.Select(x => x.PortName).ToArray()),
                preferCombined && master.Role != 2 ? "  · 可选主从一体(角色2)" : "");
            return p;
        }

        /// <summary>点对点:任选两台,一台主一台从</summary>
        public static RolePlan PlanPointToPoint(DeviceSession a, DeviceSession b)
        {
            RolePlan p = new RolePlan();
            if (a == null || b == null || a == b)
            {
                p.Message = "请选择两台不同的模组";
                return p;
            }
            // 若其一已是主,则以其为主;否则第一台为主
            if (b.Role == 1 || b.Role == 2) { p.Master = b; p.Slaves.Add(a); }
            else { p.Master = a; p.Slaves.Add(b); }
            p.Ok = true;
            p.Message = string.Format("点对点: 主 {0} → 从 {1}", p.Master.PortName, p.Slaves[0].PortName);
            return p;
        }
    }
}
