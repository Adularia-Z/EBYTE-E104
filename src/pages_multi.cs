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
        /// <summary>链路事件缓存(由 OnLinkEvent 填充,供连接判定使用)</summary>
        static readonly List<string> _linkEvents = new List<string>();
        public static void PushLinkEvent(string evt)
        {
            if (string.IsNullOrEmpty(evt)) return;
            lock (_linkEvents)
            {
                _linkEvents.Add(evt);
                if (_linkEvents.Count > 200) _linkEvents.RemoveRange(0, 100);
            }
        }
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

            // 逐台串行(主在前,从依次),每台:写角色 → 复位 → 回读校验
            List<DeviceSession> all = new List<DeviceSession>();
            all.Add(p.Master);
            all.AddRange(p.Slaves);

            PlanLog("=== 开始主从分配(逐台执行)===");
            ApplyMixedRoles(all, 0, p.Master, masterRole, delegate
            {
                PlanLog("=== 主从分配完成,可执行 [③ 点对点连接] ===");
                RefreshDeviceList();
            });
        }

        /// <summary>逐台应用角色:主用 masterRole,其余用 0</summary>
        private void ApplyMixedRoles(List<DeviceSession> list, int idx, DeviceSession master, int masterRole, Action done)
        {
            if (idx >= list.Count)
            {
                if (done != null) done();
                return;
            }
            DeviceSession d = list[idx];
            int role = (d == master) ? masterRole : 0;
            string cmd = "AT+ROLE=" + role;
            PlanLog("[" + (idx + 1) + "/" + list.Count + "] " + d.PortName + " → " + cmd);

            d.Se.Query(cmd, delegate(List<string> lines, bool ok)
            {
                bool ack = false;
                foreach (string l in lines) if (l != null && l.Trim() == "+OK") ack = true;
                if (!ack)
                {
                    PlanLog("  ✘ 写入失败: " + string.Join(" | ", lines.ToArray()));
                    ApplyMixedRoles(list, idx + 1, master, masterRole, done);
                    return;
                }
                PlanLog("  ✔ 已写入,复位生效…");
                d.Se.Query("AT+RESET", delegate(List<string> rl, bool rok)
                {
                    Delay(3200, delegate
                    {
                        HandshakeThenReadRole(d, 0, role, delegate(bool okRole)
                        {
                            PlanLog("  " + (okRole ? "✔" : "✘") + " 回读 " + d.PortName
                                    + " ROLE=" + (d.Role >= 0 ? d.Role.ToString() : "?")
                                    + (okRole ? "" : "(期望 " + role + ")"));
                            ApplyMixedRoles(list, idx + 1, master, masterRole, done);
                        });
                    });
                }, 4500, 600);
            }, 3000, 260);
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

            List<DeviceSession> all = new List<DeviceSession>(multi.Devices);
            PlanLog("=== 全部恢复从角色(逐台:写角色 → 复位 → 回读)===");
            ApplyRoleOneByOne(all, 0, 0, null);
        }

        /// <summary>
        /// 逐台串行写角色并复位回读:
        ///  写 AT+ROLE=n → 等 +OK → AT+RESET → 等 MODULE POWERUP / 固定延时 → 握手重试 → 回读 ROLE
        /// 每台完成后再处理下一台,避免同一串口并发。
        /// </summary>
        private void ApplyRoleOneByOne(List<DeviceSession> list, int idx, int role, Action done)
        {
            if (list == null || idx >= list.Count)
            {
                PlanLog("=== 全部完成 ===");
                RefreshDeviceList();
                if (done != null) done();
                return;
            }
            DeviceSession d = list[idx];
            string cmd = "AT+ROLE=" + role;
            PlanLog("[" + (idx + 1) + "/" + list.Count + "] " + d.PortName + " → " + cmd);

            d.Se.Query(cmd, delegate(List<string> lines, bool ok)
            {
                bool ack = false;
                foreach (string l in lines) if (l != null && l.Trim() == "+OK") ack = true;
                if (!ack)
                {
                    PlanLog("  ✘ 写入失败: " + string.Join(" | ", lines.ToArray()));
                    ApplyRoleOneByOne(list, idx + 1, role, done);
                    return;
                }
                PlanLog("  ✔ 角色已写入,复位生效…");

                d.Se.Query("AT+RESET", delegate(List<string> rl, bool rok)
                {
                    // 复位后等待重启完成再握手回读
                    Delay(3200, delegate
                    {
                        HandshakeThenReadRole(d, 0, role, delegate(bool okRole)
                        {
                            PlanLog("  " + (okRole ? "✔" : "✘") + " 回读校验 " + d.PortName
                                    + " ROLE=" + (d.Role >= 0 ? d.Role.ToString() : "?"));
                            ApplyRoleOneByOne(list, idx + 1, role, done);
                        });
                    });
                }, 4500, 600);
            }, 3000, 260);
        }

        /// <summary>复位后重试握手并回读角色</summary>
        private void HandshakeThenReadRole(DeviceSession d, int attempt, int expectRole, Action<bool> cb)
        {
            if (attempt > 6) { if (cb != null) cb(false); return; }
            d.Se.Query("AT", delegate(List<string> lines, bool ok)
            {
                bool ack = false;
                foreach (string l in lines) if (l != null && l.Trim() == "+OK") ack = true;
                if (!ack)
                {
                    Delay(500, delegate { HandshakeThenReadRole(d, attempt + 1, expectRole, cb); });
                    return;
                }
                d.Se.Query("AT+ROLE?", delegate(List<string> rl, bool ok2)
                {
                    int got = -1;
                    foreach (string l in rl)
                    {
                        Match m = Regex.Match(l ?? "", @"^\+ROLE\s*=\s*([0-3])");
                        if (m.Success) { int.TryParse(m.Groups[1].Value, out got); break; }
                    }
                    d.Role = got;
                    if (cb != null) cb(got == expectRole);
                }, 2000, 200);
            }, 1500, 220);
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
            PlanLog("=== 依次连接 " + p.Slaves.Count + " 台从机 ===");
            ConnectSlavesSequential(p.Master, p.Slaves, 0);
        }

        /// <summary>串行连接:前一台结果明确(成功/失败/超时)后再连下一台</summary>
        private void ConnectSlavesSequential(DeviceSession master, List<DeviceSession> slaves, int idx)
        {
            if (slaves == null || idx >= slaves.Count)
            {
                PlanLog("=== 连接流程结束 ===");
                RefreshDeviceList();
                return;
            }
            PlanLog("[" + (idx + 1) + "/" + slaves.Count + "] ");
            P2PConnect(master, slaves[idx], delegate(bool ok)
            {
                // 无论成功与否,都等这一步结束后再继续
                ConnectSlavesSequential(master, slaves, idx + 1);
            });
        }

        /// <summary>主机扫描 → 找到从机 MAC → 发起连接</summary>
        /// <summary>
        /// 点对点连接。严格判定:
        ///  ① AT+CONNECT 回 +OK 后显示"连接中"
        ///  ② 等待目标 MAC 的 CONNECTD 事件(精确匹配,排除 DISCONNECTED 误命中)
        ///  ③ 用 AT+CONNECT_LIST? 核实链路与 handle
        ///  ④ 成功后留 1 秒服务发现时间
        /// </summary>
        private void P2PConnect(DeviceSession master, DeviceSession slave, Action<bool> done = null)
        {
            if (master == null || slave == null) { if (done != null) done(false); return; }
            if (string.IsNullOrEmpty(slave.Mac) || slave.Mac == "-")
            {
                PlanLog("从机 " + slave.PortName + " 尚未读到 MAC,先读取…");
                ChainReadOn(slave, new string[] { "MAC", "ROLE" }, 0, delegate { P2PConnect(master, slave, done); });
                return;
            }

            string mac = slave.Mac.ToUpperInvariant();
            PlanLog("主机 " + master.PortName + " → 从机 " + slave.PortName + " (" + mac + ") 连接中…");
            string cmd = "AT+CONNECT=," + mac;

            // 捕获带 +OK 的首个应答,随后单独核实
            master.Se.Query(cmd, delegate(List<string> lines, bool ok)
            {
                string r = string.Join(" | ", lines.ToArray());
                PlanLog("  " + cmd + " → " + (r.Length > 0 ? r : "(无应答)"));

                bool okAck = false;
                foreach (string l in lines) if (l != null && l.Trim() == "+OK") okAck = true;

                if (!okAck)
                {
                    if (r.Contains("+ERR"))
                    {
                        PlanLog("  ✘ 连接被拒(错误码见状态栏):" + r);
                    }
                    else
                    {
                        PlanLog("  ✘ 未收到 +OK(可能超时或链路异常)");
                    }
                    if (done != null) done(false);
                    return;
                }

                // ② 等目标 MAC 的连接事件(Session 已在扫描;用 CONNECTD 精确匹配)
                PlanLog("  +OK 已收到,等待连接事件…");
                WaitConnectEvent(master, mac, 12000, delegate(bool evtOk, string evt)
                {
                    if (evt.Length > 0) PlanLog("  事件: " + evt);
                    // ③ 无论事件如何,都用连接列表核实(事件可能丢失)
                    VerifyLink(master, mac, delegate(bool linked, int handle)
                    {
                        if (linked)
                        {
                            master.LastHandle = handle;
                            PlanLog("  ✔ 已连接 " + mac + (handle >= 0 ? " (handle=" + handle + ")" : ""));
                            // ④ 给服务发现留时间
                            Delay(1000, delegate { if (done != null) done(true); });
                        }
                        else
                        {
                            PlanLog("  ✘ 未建立链路(事件 " + (evtOk ? "成功" : "失败") + ",连接列表无此设备)");
                            if (done != null) done(false);
                        }
                    });
                });
            }, 14000, 600);
        }

        /// <summary>等待目标 MAC 的 CONNECTD 事件(精确匹配,排除 DISCONNECTED)</summary>
        private void WaitConnectEvent(DeviceSession master, string mac, int timeoutMs, Action<bool, string> cb)
        {
            DateTime end = DateTime.Now.AddMilliseconds(timeoutMs);
            string found = "";
            DelayRunner runner = null;
            runner = new DelayRunner(200, delegate
            {
                lock (_linkEvents)
                {
                    for (int i = 0; i < _linkEvents.Count; i++)
                    {
                        string e = _linkEvents[i];
                        if (e != null && MatchesConnectEvent(e, mac)) { found = e; _linkEvents.RemoveAt(i); break; }
                    }
                }
                if (found.Length > 0) { runner.Stop(); cb(true, found); return; }
                if (DateTime.Now >= end) { runner.Stop(); cb(false, ""); }
            });
            runner.Start();
        }

        /// <summary>
        /// 精确判断"连接成功"事件:
        ///   XX:.. CONNECTD C*n   /  CONNECTD P*n   (注意 DISCONNECTED / DISCONNECTD 不算)
        /// </summary>
        public static bool MatchesConnectEvent(string evt, string mac)
        {
            if (string.IsNullOrEmpty(evt) || string.IsNullOrEmpty(mac)) return false;
            string u = evt.ToUpperInvariant();
            string m = mac.ToUpperInvariant();
            if (u.IndexOf(m, StringComparison.Ordinal) < 0) return false;
            if (u.IndexOf("DISCONNECT", StringComparison.Ordinal) >= 0) return false;   // 断开事件
            if (u.IndexOf("DISCONNECTD", StringComparison.Ordinal) >= 0) return false;
            if (u.IndexOf("TIMEOUT", StringComparison.Ordinal) >= 0) return false;
            return u.IndexOf("CONNECTD", StringComparison.Ordinal) >= 0
                || u.IndexOf("CONNECTED", StringComparison.Ordinal) >= 0;
        }

        /// <summary>从连接事件里解析 handle(如 "CONNECTD C*1" → 1)</summary>
        public static int ParseHandle(string evt)
        {
            if (string.IsNullOrEmpty(evt)) return -1;
            Match hm = Regex.Match(evt, @"CONNECT(?:D|ED)\s*[CP]\s*\*\s*(\d+)");
            if (hm.Success) { int h; int.TryParse(hm.Groups[1].Value, out h); return h; }
            return -1;
        }

        /// <summary>用 AT+CONNECT_LIST 核实链路是否存在</summary>
        private void VerifyLink(DeviceSession master, string mac, Action<bool, int> cb)
        {
            master.Se.Query("AT+CONNECT_LIST?", delegate(List<string> lines, bool ok)
            {
                bool linked = false; int handle = -1;
                string u = mac.ToUpperInvariant();
                for (int i = 0; i < lines.Count; i++)
                {
                    string l = lines[i];
                    if (string.IsNullOrEmpty(l)) continue;
                    if (l.ToUpperInvariant().IndexOf(u, StringComparison.Ordinal) < 0) continue;
                    linked = true;
                    Match hm = Regex.Match(l, @"^\s*(\d+)\s*P?\s*,");
                    if (hm.Success) { int h; int.TryParse(hm.Groups[1].Value, out h); handle = h; }
                    break;
                }
                PlanLog("  连接列表: " + string.Join(" | ", lines.ToArray()));
                cb(linked, handle);
            }, 3000, 250);
        }

        /// <summary>延迟执行(不阻塞 UI)</summary>
        private void Delay(int ms, Action a)
        {
            DelayRunner r = null;
            r = new DelayRunner(ms, delegate { r.Stop(); a(); });
            r.Start();
        }

        /// <summary>简单的定时器封装</summary>
        private class DelayRunner
        {
            private readonly System.Windows.Forms.Timer _t;
            public DelayRunner(int ms, EventHandler h)
            {
                _t = new System.Windows.Forms.Timer();
                _t.Interval = Math.Max(50, ms);
                _t.Tick += h;
            }
            public void Start() { _t.Start(); }
            public void Stop() { try { _t.Stop(); _t.Dispose(); } catch { } }
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
