// test_multi.cs — 无头验证多模组主从分配/点对点规划逻辑
using System;
using System.Collections.Generic;

namespace E104
{
    static class TestMulti
    {
        static int _fail = 0;
        static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) _fail++;
        }

        static DeviceSession Dev(int i, string port, int role)
        {
            DeviceSession d = new DeviceSession();
            d.Index = i; d.PortName = port; d.Role = role;
            d.Mac = "AA:BB:CC:DD:EE:0" + i; d.Name = "DEV" + i;
            return d;
        }

        static int Main()
        {
            // 1) 两台全新(角色未知) → 第一台为主
            List<DeviceSession> a = new List<DeviceSession> { Dev(0, "COM3", -1), Dev(1, "COM4", -1) };
            RolePlan p = RolePlanner.PlanMasterSlave(a, false);
            Check("两台未知角色:主=COM3", p.Ok && p.Master.PortName == "COM3" && p.Slaves.Count == 1);

            // 2) 已有一台主 → 保持其为主
            List<DeviceSession> b = new List<DeviceSession> { Dev(0, "COM3", 0), Dev(1, "COM4", 1), Dev(2, "COM5", 0) };
            p = RolePlanner.PlanMasterSlave(b, false);
            Check("已有主 COM4:保持主不变", p.Ok && p.Master.PortName == "COM4" && p.Slaves.Count == 2);

            // 3) 5 台全从 → 第一台为主,其余 4 从
            List<DeviceSession> c = new List<DeviceSession>();
            for (int i = 0; i < 5; i++) c.Add(Dev(i, "COM" + (3 + i), 0));
            p = RolePlanner.PlanMasterSlave(c, false);
            Check("5 台全从:1 主 + 4 从", p.Ok && p.Master.PortName == "COM3" && p.Slaves.Count == 4);

            // 4) 单台 → 拒绝
            p = RolePlanner.PlanMasterSlave(new List<DeviceSession> { Dev(0, "COM3", 0) }, false);
            Check("单台拒绝分配", !p.Ok);

            // 5) 点对点:正常
            DeviceSession x = Dev(0, "COM3", 0), y = Dev(1, "COM4", 1);
            p = RolePlanner.PlanPointToPoint(x, y);
            Check("点对点:已是主的 COM4 当主", p.Ok && p.Master.PortName == "COM4" && p.Slaves[0].PortName == "COM3");

            // 6) 点对点:同一台 → 拒绝
            p = RolePlanner.PlanPointToPoint(x, x);
            Check("点对点同台拒绝", !p.Ok);

            // 7) 上限
            MultiDevice md = new MultiDevice();
            Check("上限 5", MultiDevice.MaxDevices == 5 && md.CanAdd && md.Count == 0);

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? "==== 多机逻辑全部通过 ====" : ("==== 失败 " + _fail + " 项 ===="));
            return _fail == 0 ? 0 : 1;
        }
    }
}
