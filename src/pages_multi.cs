// pages_multi.cs — 多机协同页(≤5 模组):设备列表 / 激活切换 / 主从自动分配 / 点对点连接
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace E104
{
    public partial class MainForm
    {
        ListView devLv;
        ComboBoxHelper ppA, ppB;
        DarkCheck chkCombined;
        Label lblPlan;
        RichTextBox rtbPlanLog;

        // DarkCombo 的轻量包装(仅记录两个下拉的选中项)
        public class ComboBoxHelper
        {
            public DarkCombo Cmb;
            public ComboBoxHelper(DarkCombo c) { Cmb = c; }
            public DeviceSession Selected
            {
                get
                {
                    if (Cmb == null || Cmb.SelectedIndex < 0) return null;
                    int k = Cmb.SelectedIndex;
                    return (k >= 0 && k < Owner.multi.Count) ? Owner.multi.Devices[k] : null;
                }
            }
            public MainForm Owner;
        }

        private void BuildMulti()
        {
            // ---------- 已接入模组 ----------
            Card c1 = MkCard(8, "已接入模组(最多 5 个)", "选择行后 [设为激活] 即可用其它页面调试该模组");
            RowStack rs = new RowStack(c1.Body);

            devLv = new ListView();
            devLv.View = View.Details; devLv.FullRowSelect = true; devLv.HideSelection = false;
            devLv.GridLines = false; devLv.BorderStyle = BorderStyle.FixedSingle;
            devLv.BackColor = Color.FromArgb(24, 24, 27); devLv.ForeColor = Theme.TxT; devLv.Font = Theme.FMonoS;
            devLv.OwnerDraw = true;
            devLv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                // 深色表头(系统默认是白底,与主题割裂)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 38, 43)))
                    e.Graphics.FillRectangle(b, e.Bounds);
                using (Pen p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.F,
                    e.Bounds, Color.FromArgb(200, 200, 208),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            devLv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { };
            devLv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                Color bg = (e.Item.Selected) ? Color.FromArgb(0, 60, 100) : Color.FromArgb(24, 24, 27);
                using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
                Color fg = (e.ColumnIndex == 0) ? Theme.AccentHi : Theme.TxT;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Theme.FMonoS,
                    e.Bounds, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            devLv.Columns.Add("编号", 60); devLv.Columns.Add("串口", 90);
            devLv.Columns.Add("角色", 90); devLv.Columns.Add("广播名", 150);
            devLv.Columns.Add("MAC", 170); devLv.Columns.Add("固件", 120);
            devLv.Columns.Add("状态", 100);
            devLv.Columns.Add("", 40);   // 尾部填充列,吃掉右侧留白(否则表头/底色露出系统白色)
            rs.Host.Controls.Add(devLv);
            devLv.SetBounds(14, rs.Y, 860, 96);
            rs.Host.Resize += delegate
            {
                devLv.Width = Math.Max(500, rs.Host.ClientSize.Width - 28);
                // 尾部列自适应剩余宽度
                int used = 0;
                for (int ci = 0; ci < devLv.Columns.Count - 1; ci++) used += devLv.Columns[ci].Width;
                int last = devLv.ClientSize.Width - used - 4;
                devLv.Columns[devLv.Columns.Count - 1].Width = Math.Max(10, last);
            };
            rs.NewRow(102);
            devLv.DoubleClick += delegate { SetActiveSelected(); };

            rs.RowBegin(0);
            FlatBtn bAct = rs.RowButton("设为激活", 96, BtnKind.Accent, "把选中模组切换为当前调试目标\n切换后所有页面(基础信息/射频/广播/...)都作用于该模组");
            bAct.Click += delegate { SetActiveSelected(); };
            FlatBtn bRe = rs.RowButton("刷新信息", 96, BtnKind.Normal, "重新读取选中模组的 ROLE/NAME/MAC/VERSION");
            bRe.Click += delegate { RefreshSelectedInfo(); };
            FlatBtn bRm = rs.RowButton("断开该模组", 108, BtnKind.Danger, "仅断开选中模组,其它模组保持连接");
            bRm.Click += delegate
            {
                DeviceSession d = SelectedDevice();
                if (d == null) { MessageBox.Show("请先在列表中选择一个模组", "提示"); return; }
                if (MessageBox.Show("断开 " + d.PortName + " ?", "确认", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                multi.Remove(d); SyncActiveRefs(); RefreshDeviceList();
                Log("sys", "已断开 " + d.PortName);
            };
            FlatBtn bAdd = rs.RowButton("接入新模组(用顶栏)", 148, BtnKind.Normal, "在顶栏选择串口后点[接入],可同时接入最多 5 个");
            bAdd.Click += delegate { SwitchPage(0); };
            rs.RowEnd(4);
            CardEnd(c1, rs);

            // ---------- 主从自动分配 ----------
            Card c2 = MkCard(8, "主从自动分配", "将一台设为主(1)、其余设为从(0),并引导点对点连通");
            RowStack rs2 = new RowStack(c2.Body);

            rs2.RowBegin(0);
            chkCombined = new DarkCheck(); chkCombined.Text = "主机使用主从一体(角色2,可同时被连接)"; FitDark(chkCombined);
            rs2.RowControl(chkCombined, 0, "角色2:既能连从设备,也能被手机等主设备连");
            rs2.RowEnd(6);

            rs2.RowBegin(0);
            FlatBtn bPlan = rs2.RowButton("① 预演分配方案", 132, BtnKind.Normal, "先算出主从分配结果,不写入模组");
            bPlan.Click += delegate
            {
                RolePlan p = RolePlanner.PlanMasterSlave(multi.Devices, chkCombined.Checked);
                lblPlan.Text = p.Message;
                PlanLog(p.Ok ? "预演成功: " + p.Message : "预演失败: " + p.Message);
            };
            FlatBtn bApply = rs2.RowButton("② 应用分配(写角色)", 148, BtnKind.Accent, "向各模组下发 AT+ROLE=1/0,并复位生效\n注意:切换角色会清空自动重连列表");
            bApply.Click += delegate { ApplyRolePlan(); };
            FlatBtn bConnect = rs2.RowButton("③ 点对点连接", 116, BtnKind.Accent, "主机下发 AT+SCAN 扫描,再按 MAC 连接从机");
            bConnect.Click += delegate { P2PConnectAll(); };
            rs2.RowGap(10);
            FlatBtn bRst = rs2.RowButton("全部恢复从角色", 128, BtnKind.Danger, "把每台都设回角色0(从),并复位");
            bRst.Click += delegate { RestoreAllSlave(); };
            rs2.RowEnd(6);

            lblPlan = new Label();
            lblPlan.ForeColor = Theme.AccentHi; lblPlan.AutoSize = true; lblPlan.Text = "尚未预演";
            rs2.RowBegin(0);
            rs2.RowControl(lblPlan, 0);
            rs2.RowEnd(4);

            rtbPlanLog = new RichTextBox();
            rtbPlanLog.BackColor = Color.FromArgb(24, 24, 27); rtbPlanLog.ForeColor = Theme.TxT;
            rtbPlanLog.Font = Theme.FMonoS; rtbPlanLog.BorderStyle = BorderStyle.None; rtbPlanLog.ReadOnly = true;
            rtbPlanLog.Height = 92; rtbPlanLog.DetectUrls = false;
            rs2.Host.Controls.Add(rtbPlanLog);
            rtbPlanLog.SetBounds(14, rs2.Y, 860, 92);
            rs2.Host.Resize += delegate { rtbPlanLog.Width = Math.Max(500, rs2.Host.ClientSize.Width - 28); };
            rs2.NewRow(98);
            CardEnd(c2, rs2);

            // ---------- 点对点 ----------
            Card c3 = MkCard(8, "点对点连接(任选两台)", "指定两台模组:一主一从,直接按 MAC 互连");
            RowStack rs3 = new RowStack(c3.Body);

            rs3.RowBegin(0);
            rs3.RowTitle("A 端", Tips["ROLE"]);
            ppA = new ComboBoxHelper(new DarkCombo());
            ppA.Cmb.Width = 220; ppA.Owner = this;
            rs3.RowControl(ppA.Cmb, 14);
            rs3.RowTitle("B 端", Tips["ROLE"]);
            ppB = new ComboBoxHelper(new DarkCombo());
            ppB.Cmb.Width = 220; ppB.Owner = this;
            rs3.RowControl(ppB.Cmb, 14);
            FlatBtn bGo = rs3.RowButton("连接 A→B", 116, BtnKind.Accent, "自动判断谁是主,让主机扫描并连接从机");
            bGo.Click += delegate { RunP2P(); };
            rs3.RowEnd(4);
            CardEnd(c3, rs3);
        }

        // ================= 辅助 =================
        public DeviceSession SelectedDevice()
        {
            if (devLv == null || devLv.SelectedItems.Count == 0) return null;
            int idx;
            if (!int.TryParse(devLv.SelectedItems[0].Tag as string, out idx)) return null;
            if (idx < 0 || idx >= multi.Count) return null;
            return multi.Devices[idx];
        }

        public void RefreshDeviceList()
        {
            if (devLv == null || devLv.IsDisposed) return;
            devLv.BeginUpdate();
            devLv.Items.Clear();
            for (int i = 0; i < multi.Count; i++)
            {
                DeviceSession d = multi.Devices[i];
                ListViewItem it = new ListViewItem("M" + (i + 1));
                it.SubItems.Add(d.PortName);
                it.SubItems.Add(d.RoleText);
                it.SubItems.Add(d.Name);
                it.SubItems.Add(d.Mac);
                it.SubItems.Add(d.Version);
                it.SubItems.Add(d.Ready ? (d == multi.Active ? "激活" : "就绪") : "未握手");
                it.Tag = i.ToString();
                if (d == multi.Active) it.BackColor = Color.FromArgb(0, 60, 100);
                devLv.Items.Add(it);
            }
            devLv.EndUpdate();

            // 点对点下拉同步
            SyncP2PCombos();
        }

        private void SyncP2PCombos()
        {
            if (ppA == null || ppB == null) return;
            int a = ppA.Cmb.SelectedIndex, b = ppB.Cmb.SelectedIndex;
            ppA.Cmb.Items.Clear(); ppB.Cmb.Items.Clear();
            for (int i = 0; i < multi.Count; i++)
            {
                string s = "M" + (i + 1) + " " + multi.Devices[i].PortName + " (" + multi.Devices[i].RoleText + ")";
                ppA.Cmb.Items.Add(s);
                ppB.Cmb.Items.Add(s);
            }
            if (ppA.Cmb.Items.Count > 0)
                ppA.Cmb.SelectedIndex = (a >= 0 && a < ppA.Cmb.Items.Count) ? a : 0;
            if (ppB.Cmb.Items.Count > 1)
                ppB.Cmb.SelectedIndex = (b >= 0 && b < ppB.Cmb.Items.Count) ? b : 1;
            else if (ppB.Cmb.Items.Count > 0)
                ppB.Cmb.SelectedIndex = 0;
        }

        private void SetActiveSelected()
        {
            DeviceSession d = SelectedDevice();
            if (d == null) { MessageBox.Show("请先在列表中选择一个模组", "提示"); return; }
            multi.ActiveIndex = d.Index;
            SyncActiveRefs();
            RefreshDeviceList();
            Log("sys", "当前激活模组切换为 M" + (d.Index + 1) + " " + d.PortName);
            // 刷新激活通道信息
            if (d.Se != null) ChainRead(new string[] { "ROLE", "NAME", "MAC", "VERSION" }, null);
        }

        private void RefreshSelectedInfo()
        {
            DeviceSession d = SelectedDevice();
            if (d == null) { MessageBox.Show("请先在列表中选择一个模组", "提示"); return; }
            string[] keys = new string[] { "ROLE", "NAME", "MAC", "VERSION" };
            ChainReadOn(d, keys, 0, delegate { RefreshDeviceList(); });
        }

        /// <summary>在指定设备上链式读取(不需要切换激活)</summary>
        private void ChainReadOn(DeviceSession d, string[] keys, int i, Action done)
        {
            if (d == null || d.Se == null) { if (done != null) done(); return; }
            if (i >= keys.Length) { RefreshDeviceList(); if (done != null) done(); return; }
            string k = keys[i];
            d.Se.Query("AT+" + k + "?", delegate(List<string> lines, bool ok)
            {
                for (int n = 0; n < lines.Count; n++)
                {
                    Match m = Regex.Match(lines[n] ?? "", @"^\+([A-Za-z_0-9]+)\s*=\s*(.*)$");
                    if (m.Success) TrackDeviceInfo(d, m.Groups[1].Value.ToUpper(), m.Groups[2].Value.Trim());
                }
                ChainReadOn(d, keys, i + 1, done);
            }, ChainTimeout, ChainQuiet);
        }

        // ================= 角色分配 =================
        private void PlanLog(string s)
        {
            if (rtbPlanLog == null) return;
            rtbPlanLog.AppendText(DateTime.Now.ToString("HH:mm:ss ") + s + "\n");
            rtbPlanLog.ScrollToCaret();
        }

        private void ApplyRolePlan()
        {
            RolePlan p = RolePlanner.PlanMasterSlave(multi.Devices, chkCombined.Checked);
            if (!p.Ok) { MessageBox.Show(p.Message, "提示"); return; }
            int masterRole = chkCombined.Checked ? 2 : 1;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("即将下发:");
            sb.AppendLine("  主: " + p.Master.PortName + " → AT+ROLE=" + masterRole);
            for (int i = 0; i < p.Slaves.Count; i++)
                sb.AppendLine("  从: " + p.Slaves[i].PortName + " → AT+ROLE=0");
            sb.AppendLine();
            sb.AppendLine("提示:切换角色后需复位生效,且会清空自动重连列表。");
            sb.Append("是否继续?");
            if (MessageBox.Show(sb.ToString(), "确认应用主从分配", MessageBoxButtons.OKCancel) != DialogResult.OK) return;

            List<DeviceSession> all = new List<DeviceSession>();
            all.Add(p.Master);
            all.AddRange(p.Slaves);
            int mi = 0;
            StepSetRole(all, 0, p.Master, masterRole, delegate
            {
                PlanLog("主 " + p.Master.PortName + " → ROLE=" + masterRole + " 完成");
                for (int i = 0; i < p.Slaves.Count; i++)
                {
                    DeviceSession s = p.Slaves[i];
                    SendCmdOn(s, "AT+ROLE=0", delegate(string r)
                    {
                        PlanLog("从 " + s.PortName + " → ROLE=0  " + r);
                        if (s == p.Slaves[p.Slaves.Count - 1]) AfterRoleApplied(all);
                    });
                }
            });
        }

        private void StepSetRole(List<DeviceSession> all, int i, DeviceSession master, int masterRole, Action done)
        {
            if (i > 0) { if (done != null) done(); return; }
            SendCmdOn(master, "AT+ROLE=" + masterRole, delegate(string r)
            {
                PlanLog("主 " + master.PortName + " → ROLE=" + masterRole + "  " + r);
                StepSetRole(all, 1, master, masterRole, done);
            });
        }

        /// <summary>角色写完后统一复位 + 回读</summary>
        private void AfterRoleApplied(List<DeviceSession> all)
        {
            PlanLog("全部角色已写入,开始复位…");
            for (int i = 0; i < all.Count; i++)
            {
                DeviceSession d = all[i];
                SendCmdOn(d, "AT+RESET", null, true);
            }
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 2500;
            int tick = 0;
            t.Tick += delegate
            {
                tick++;
                if (tick < 2) return;
                t.Stop(); t.Dispose();
                PlanLog("复位完成,回读角色…");
                for (int i = 0; i < all.Count; i++)
                {
                    DeviceSession d = all[i];
                    ChainReadOn(d, new string[] { "ROLE", "MAC", "NAME" }, 0, null);
                }
                RefreshDeviceList();
                PlanLog("完成。可点 [③ 点对点连接] 建立连接。");
            };
            t.Start();
        }

        private void RestoreAllSlave()
        {
            if (multi.Count == 0) { MessageBox.Show("尚未接入模组", "提示"); return; }
            if (MessageBox.Show("将把所有模组设为从角色(0)并复位,继续?", "确认",
                MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            for (int i = 0; i < multi.Count; i++)
            {
                DeviceSession d = multi.Devices[i];
                SendCmdOn(d, "AT+ROLE=0", delegate(string r) { PlanLog(d.PortName + " → ROLE=0 " + r); });
            }
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 2000; t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                for (int i = 0; i < multi.Count; i++) ChainReadOn(multi.Devices[i], new string[] { "ROLE" }, 0, null);
            };
            t.Start();
        }

        // ================= 点对点 =================
        private void RunP2P()
        {
            DeviceSession a = ppA != null ? ppA.Selected : null;
            DeviceSession b = ppB != null ? ppB.Selected : null;
            RolePlan p = RolePlanner.PlanPointToPoint(a, b);
            if (!p.Ok) { MessageBox.Show(p.Message, "提示"); return; }
            PlanLog(p.Message);
            P2PConnect(p.Master, p.Slaves[0]);
        }

        private void P2PConnectAll()
        {
            RolePlan p = RolePlanner.PlanMasterSlave(multi.Devices, chkCombined.Checked);
            if (!p.Ok) { MessageBox.Show(p.Message, "提示"); return; }
            for (int i = 0; i < p.Slaves.Count; i++) P2PConnect(p.Master, p.Slaves[i]);
        }

        /// <summary>主机扫描 → 找到从机 MAC → 发起连接</summary>
        private void P2PConnect(DeviceSession master, DeviceSession slave)
        {
            if (master == null || slave == null) return;
            if (string.IsNullOrEmpty(slave.Mac) || slave.Mac == "-")
            {
                PlanLog("从机 " + slave.PortName + " 尚未读到 MAC,先读取…");
                ChainReadOn(slave, new string[] { "MAC", "ROLE" }, 0, delegate { P2PConnect(master, slave); });
                return;
            }
            PlanLog("主机 " + master.PortName + " 连接从机 " + slave.PortName + " (" + slave.Mac + ") …");
            string cmd = "AT+CONNECT=," + slave.Mac;
            master.Se.Query(cmd, delegate(List<string> lines, bool ok)
            {
                string r = string.Join(" | ", lines.ToArray());
                PlanLog("  " + cmd + " → " + r);
                if (r.Contains("CONNECTD") || r.Contains("CONNECTED"))
                    PlanLog("✔ 已连接 " + slave.Mac);
                else if (r.Contains("TIMEOUT"))
                    PlanLog("✘ 连接超时(检查从机是否在广播、主从距离/波特率)");
                else
                    PlanLog("✘ 连接失败(详见 +ERR 错误码)");
            }, 13000, 400);
        }

        /// <summary>在指定设备下发指令(不依赖激活通道)</summary>
        private void SendCmdOn(DeviceSession d, string cmd, Action<string> done)
        { SendCmdOn(d, cmd, done, false); }

        private void SendCmdOn(DeviceSession d, string cmd, Action<string> done, bool isReset)
        {
            if (d == null || d.Se == null) { if (done != null) done("设备不可用"); return; }
            d.Se.Query(cmd, delegate(List<string> lines, bool ok)
            {
                string r = lines.Count > 0 ? lines[0] : "(无应答)";
                if (done != null) done(r);
            }, isReset ? 4500 : 3000, isReset ? 600 : 260);
        }
    }
}
