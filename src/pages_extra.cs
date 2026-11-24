// pages_extra.cs — 广播 / 从机服务 / 主机连接(扫描·连接·收发·观察者) / 串口功耗 / 认证 / AT 终端
//                + 交互式两步指令状态机 + 特殊查询回填
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
        // ---- 页面控件引用 ----
        DarkCombo advOnCmb, advTypeCmb, advdFmtCmb, adveFmtCmb, sendFmtCmb;
        TextBox advIvTxt, advdDataTxt, adveLenTxt, adveToTxt, adveDataTxt;
        TextBox svcBase;
        RadioButton svc16, svc128;
        ListView scanLv;
        RichTextBox rtbRecv, rtbObs;
        TextBox connMacTxt, sendHandleTxt, sendDataTxt, sendToTxt;
        TextBox autoMacTxt; DarkCombo autoOnCmb; Label autoStateLbl;
        DarkCombo obsModeCmb;
        bool _obsAutoScroll = true;
        DarkCombo slpUartCmb, slpBleCmb;
        TextBox ciTxt; Label ciMsLbl;
        TextBox authTxt;
        TextBox termTxt;
        List<string> _termHist = new List<string>(); int _termHistIdx = -1;

        // ---- 交互式两步指令状态 ----
        string _awaitInput = null;          // "EXT" / "SEND"
        byte[] _sendPending = null;
        FlatBtn btnExtSend, btnBleSend, btnScanStart, btnScanStop;

        // 每页"读取本页当前值"覆盖的参数键
        static readonly string[][] PageKeys = new string[][] {
            /*0 总览*/   new string[] { "ROLE","NAME","MAC","VERSION","PWR","ADV","PHY","BAUD","CONN_INTERVAL","SLEEP","WDOG","LOGMSG" },
            /*1 基础信息*/ new string[] { "ROLE","NAME","MAC","VERSION" },
            /*2 射频*/   new string[] { "PWR","PHY","LE_CODED","SCAN_PHY" },
            /*3 广播*/   new string[] { "ADV","ADVDATA","ADV_EXT","BEACON" },
            /*4 服务*/   new string[] { "SERVICE" },
            /*5 主机*/   new string[] { "SCAN","SCAN_PHY","SCANCMP","CONNECT_LIST","TRM_HANDLE","AUTO_CNT","OBSERVER" },
            /*6 串口*/   new string[] { "BAUD","CONN_INTERVAL","LOGMSG","WDOG","SLEEP" },
            /*7 认证*/   new string[] { "UP_AUTH" },
            /*8 多机*/   new string[] { "ROLE","NAME","MAC","VERSION" },
            /*9 终端*/   new string[] { "ROLE","NAME","MAC","VERSION" },
        };

        public void BuildAllPages()
        {
            // 每页顶部统一操作条(Dock=Top,须在卡片之前加入以占据最上)
            string[] titles = new string[] { "总览", "基础信息", "射频物理层", "广播配置",
                                             "从机服务", "主机连接", "串口与功耗", "认证与安全", "多机协同", "AT 终端" };
            for (int i = 0; i < _pages.Count; i++)
                AddPageToolbar(PageStack(i), titles[i], PageKeys[i]);

            BuildOverview();
            BuildBasic();
            BuildRf();
            BuildAdv();
            BuildService();
            BuildMaster();
            BuildUart();
            BuildAuth();
            BuildMulti();
            BuildTerm();
        }

        // =============== 广播配置(3) ===============
        private void BuildAdv()
        {
            // ---- ADV ----
            Card c = MkCard(3, "广播参数 ADV(6.3.6)", "广播开关 / 广播类型 / 广播间隔ms");
            RowStack rs = new RowStack(c.Body);
            FlowRow r1 = rs.AddRow(0);
            Label l1 = r1.AddFixedLabel("广播参数", 118, false); DarkTips.Set(l1, Tips["ADV"]);
            r1.AddLabel("开关", true, Tips["ADV"], "ADV");
            advOnCmb = new DarkCombo(); advOnCmb.Width = 50;
            advOnCmb.Items.Add("0"); advOnCmb.Items.Add("1"); advOnCmb.SelectedIndex = 1;
            r1.Add(advOnCmb, 16);
            r1.AddLabel("类型(0不可连/1可连)", true, Tips["ADV"], "ADV");
            advTypeCmb = new DarkCombo(); advTypeCmb.Width = 50;
            advTypeCmb.Items.Add("0"); advTypeCmb.Items.Add("1"); advTypeCmb.SelectedIndex = 1;
            r1.Add(advTypeCmb, 16);
            r1.AddLabel("间隔(20~10240)", true, Tips["ADV"], "ADV");
            advIvTxt = new TextBox(); Theme.StyleText(advIvTxt); advIvTxt.Width = 70; advIvTxt.Text = "200";
            r1.Add(advIvTxt, 16);
            FlatBtn bq = new FlatBtn("查询", BtnKind.Normal); bq.Width = 62;
            r1.Add(bq, 6); bq.Click += delegate { QueryOne("ADV"); };
            FlatBtn bs = new FlatBtn("下发", BtnKind.Accent); bs.Width = 62;
            r1.Add(bs, 0);
            bs.Click += delegate
            { SendAndWait("AT+ADV=" + advOnCmb.Text + "," + advTypeCmb.Text + "," + advIvTxt.Text, "ADV"); };
            rs.EndRow(4);
            CardEnd(c, rs);

            // ---- ADVDATA ----
            Card c2 = MkCard(3, "自定义广播数据 ADVDATA(6.3.7)", "厂商自定义字段 ≤26 字节;_CUR 变体不写 Flash");
            RowStack rs2 = new RowStack(c2.Body);
            FlowRow r2 = rs2.AddRow(0);
            Label lf = r2.AddFixedLabel("数据格式", 118, false); DarkTips.Set(lf, Tips["ADVDATA"]);
            advdFmtCmb = new DarkCombo(); advdFmtCmb.Width = 54;
            advdFmtCmb.Items.Add("0"); advdFmtCmb.Items.Add("1"); advdFmtCmb.SelectedIndex = 0;
            r2.Add(advdFmtCmb, 6);
            r2.AddLabel("(0=ASCII 1=HEX)", true, Tips["ADVDATA"], "ADVDATA");
            advdDataTxt = new TextBox(); Theme.StyleText(advdDataTxt); advdDataTxt.Width = 150; advdDataTxt.Text = "ebyte";
            r2.Add(advdDataTxt, 16);
            FlatBtn bq2 = new FlatBtn("查询", BtnKind.Normal); bq2.Width = 62;
            r2.Add(bq2, 6); bq2.Click += delegate { QueryOne("ADVDATA"); };
            bset = new FlatBtn("下发·保存", BtnKind.Accent); bset.Width = 88;
            r2.Add(bset, 6);
            bcur = new FlatBtn("下发·本帧_CUR", BtnKind.Normal); bcur.Width = 112;
            r2.Add(bcur, 6);
            bclr = new FlatBtn("恢复默认", BtnKind.Normal); bclr.Width = 82;
            r2.Add(bclr, 0);
            bclr.Click += delegate { SendAndWait("AT+ADVDATA=0", "ADVDATA"); };
            bset.Click += delegate { SendAdvData(false); };
            bcur.Click += delegate { SendAdvData(true); };
            rs2.EndRow(4);
            CardEnd(c2, rs2);

            // ---- ADV_EXT(交互式) ----
            Card c3 = MkCard(3, "扩展广播 ADV_EXT(6.3.9,≤251字节)", "两步下发:启动 → 模块提示 → 输入 N 字节");
            RowStack rs3 = new RowStack(c3.Body);
            FlowRow r3 = rs3.AddRow(0);
            Label ln = r3.AddFixedLabel("扩展广播", 118, false); DarkTips.Set(ln, Tips["ADV_EXT"]);
            r3.AddLabel("长度(0~251)", true, Tips["ADV_EXT"], "ADV_EXT");
            adveLenTxt = new TextBox(); Theme.StyleText(adveLenTxt); adveLenTxt.Width = 58; adveLenTxt.Text = "10";
            r3.Add(adveLenTxt, 16);
            r3.AddLabel("输入超时(1~5000ms)", true, Tips["ADV_EXT"], "ADV_EXT");
            adveToTxt = new TextBox(); Theme.StyleText(adveToTxt); adveToTxt.Width = 66; adveToTxt.Text = "5000";
            r3.Add(adveToTxt, 16);
            FlatBtn bq3 = new FlatBtn("查询", BtnKind.Normal); bq3.Width = 62;
            r3.Add(bq3, 6); bq3.Click += delegate { QueryOne("ADV_EXT"); };
            FlatBtn bgo = new FlatBtn("启动·保存", BtnKind.Accent); bgo.Width = 92;
            r3.Add(bgo, 6);
            FlatBtn bgoc = new FlatBtn("启动·_CUR", BtnKind.Normal); bgoc.Width = 92;
            r3.Add(bgoc, 6);
            FlatBtn bcl0 = new FlatBtn("清除(=0)", BtnKind.Normal); bcl0.Width = 84;
            r3.Add(bcl0, 0);
            bcl0.Click += delegate { SendAndWait("AT+ADV_EXT=0", "ADV_EXT"); };
            bgo.Click += delegate { StartExtAdv(false); };
            bgoc.Click += delegate { StartExtAdv(true); };
            rs3.EndRow(4);

            FlowRow r3b = rs3.AddRow(0);
            Label ln2 = r3b.AddFixedLabel("广播数据", 118, false); DarkTips.Set(ln2, Tips["ADV_EXT"]);
            adveFmtCmb = new DarkCombo(); adveFmtCmb.Width = 82;
            adveFmtCmb.Items.Add("ascii"); adveFmtCmb.Items.Add("hex"); adveFmtCmb.SelectedIndex = 0;
            r3b.Add(adveFmtCmb, 6);
            adveDataTxt = new TextBox(); Theme.StyleText(adveDataTxt); adveDataTxt.Width = 300;
            r3b.Add(adveDataTxt, 16);
            btnExtSend = new FlatBtn("发送数据", BtnKind.Accent); btnExtSend.Width = 106; btnExtSend.Enabled = false;
            r3b.Add(btnExtSend, 0);
            btnExtSend.Click += delegate { SendExtData(); };
            rs3.EndRow(4);
            CardEnd(c3, rs3);

            // ---- BEACON ----
            Card c4 = MkCard(3, "Beacon 参数 BEACON(6.3.10)", "CompanyID / Major / Minor / 1m参考RSSI / 32位UUID");
            RowStack rs4 = new RowStack(c4.Body);
            string[] bn = new string[] { "CompanyID", "Major", "Minor", "RSSI@1m", "UUID(32hex)" };
            string[] bd = new string[] { "4C00", "2775", "848F", "-48", "FDA50693A4E24FB1AFCFC6EB07647825" };
            int[] bw = new int[] { 76, 72, 72, 72, 300 };
            TextBox[] bts = new TextBox[5];
            FlowRow rb = rs4.AddRow(0);
            Label lbk = rb.AddFixedLabel("Beacon 参数", 118, false); DarkTips.Set(lbk, Tips["BEACON"]);
            for (int i = 0; i < 5; i++)
            {
                rb.AddLabel(bn[i], true, Tips["BEACON"], "BEACON");
                TextBox bt = new TextBox(); Theme.StyleText(bt); bt.Width = bw[i]; bt.Text = bd[i];
                bts[i] = bt;
                rb.Add(bt, i < 4 ? 16 : 0);
            }
            _beaconTb = bts;
            rs4.EndRow(4);

            FlowRow rb2 = rs4.AddRow(118);
            FlatBtn bqb = new FlatBtn("查询 Beacon", BtnKind.Normal); bqb.Width = 100;
            rb2.Add(bqb, 6); bqb.Click += delegate { QueryOne("BEACON"); };
            FlatBtn bsb = new FlatBtn("下发 Beacon", BtnKind.Accent); bsb.Width = 100;
            rb2.Add(bsb, 0);
            bsb.Click += delegate
            {
                StringBuilder cmdb = new StringBuilder("AT+BEACON=");
                for (int i = 0; i < 5; i++) { if (i > 0) cmdb.Append(','); cmdb.Append(bts[i].Text.Trim()); }
                SendAndWait(cmdb.ToString(), "BEACON");
            };
            rs4.EndRow(4);
            CardEnd(c4, rs4);
        }
        TextBox[] _beaconTb;
        FlatBtn bset, bcur, bclr;

        private void SendAdvData(bool cur)
        {
            string fmt = advdFmtCmb.Text.Trim();
            string data = advdDataTxt.Text.Trim();
            if (fmt == "1" && !Regex.IsMatch(data, "^[0-9A-Fa-f]+$"))
            { MessageBox.Show("HEX 数据非法(纯十六进制)", "提示"); return; }
            SendAndWait("AT+ADVDATA" + (cur ? "_CUR" : "") + "=" + fmt + "," + data, "ADVDATA");
        }

        private void StartExtAdv(bool cur)
        {
            if (_se == null) { MessageBox.Show("请先连接串口", "提示"); return; }
            int n, to;
            if (!int.TryParse(adveLenTxt.Text.Trim(), out n) || n < 0 || n > 251 ||
                !int.TryParse(adveToTxt.Text.Trim(), out to) || to < 1 || to > 5000)
            { MessageBox.Show("长度 0~251;超时 1~5000ms", "提示"); return; }

            _awaitInput = "EXT";
            _twoStepSession = _se;
            _twoStepExpect = n;
            btnExtSend.Enabled = false;

            string cmd = "AT+ADV_EXT" + (cur ? "_CUR" : "") + "=" + n + "," + to;
            AtRequest req = _se.QueryTwoStep(cmd, delegate(byte[] _)
            {
                // 收到 INPUT EXT ADV:n 提示回调 → 点亮发送按钮
                _twoStepExpect = (_se != null && _se.PendingInputNeeded > 0) ? _se.PendingInputNeeded : n;
                btnExtSend.Enabled = true;
                btnBleSend.Enabled = false;
                stReply.Text = "⏳ 扩展广播待输入 " + _twoStepExpect + " 字节 → 填写数据后按[发送数据]";
                Log("sys", "扩展广播:已收到输入提示,等待 " + _twoStepExpect + " 字节");
            }, to + 3000);

            // 句柄变化即视为失败/超时 → 清理状态
            _extTwoStepReq = req;
        }

        AtRequest _extTwoStepReq = null;
        Session _twoStepSession = null;
        int _twoStepExpect = -1;

        private void SendExtData()
        {
            Session se = _twoStepSession ?? _se;
            if (se == null) { MessageBox.Show("串口未连接", "提示"); return; }
            byte[] data = ParseDataBox(adveDataTxt, adveFmtCmb);
            if (data == null) return;

            int need = _twoStepExpect;
            if (need < 0 && se.PendingInputNeeded > 0) need = se.PendingInputNeeded;
            if (need < 0)
            {
                MessageBox.Show("当前没有待发送的扩展广播请求,请先点[启动·保存]或[启动·_CUR]", "提示");
                return;
            }

            if (data.Length != need)
            {
                if (MessageBox.Show("需要 " + need + " 字节,当前 " + data.Length + " 字节。截断/补齐后发送?",
                    "长度不符", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                byte[] t = new byte[need];
                Array.Copy(data, t, Math.Min(need, data.Length));
                data = t;
            }

            bool ok = se.SendTwoStepPayload(data);
            if (!ok)
            {
                MessageBox.Show("提交失败:两步请求已结束或超时,请重新启动", "提示");
                btnExtSend.Enabled = false;
                return;
            }
            _twoStepExpect = -1;
            _awaitInput = null;
            btnExtSend.Enabled = false;
            stReply.Text = "✔ 扩展广播数据已发送,等待模块 +OK";
            Log("sys", "扩展广播:已提交 " + data.Length + " 字节,等待最终确认");
        }

        private byte[] ParseDataBox(TextBox tb, Control fmt)
        {
            string s = tb.Text.Trim();
            if (s.Length == 0) { MessageBox.Show("数据为空", "提示"); return null; }
            try
            {
                if (fmt.Text == "hex")
                {
                    if (!Regex.IsMatch(s, "^[0-9A-Fa-f]+$") || s.Length % 2 != 0)
                    { MessageBox.Show("HEX 非法(偶数个十六进制字符)", "提示"); return null; }
                    byte[] d = new byte[s.Length / 2];
                    for (int i = 0; i < d.Length; i++) d[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
                    return d;
                }
                return Encoding.ASCII.GetBytes(s);
            }
            catch { MessageBox.Show("数据解析失败", "提示"); return null; }
        }

        private void FillAdv()
        {
            string v = Str(_state, "ADV");
            Match m = Regex.Match(v, @"^\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
            if (!m.Success) return;
            advOnCmb.Text = m.Groups[1].Value; advTypeCmb.Text = m.Groups[2].Value; advIvTxt.Text = m.Groups[3].Value;
        }

        private void FillBeacon()
        {
            string v = Str(_state, "BEACON");
            string[] parts = v.Split(',');
            for (int i = 0; i < parts.Length && i < 5; i++) if (_beaconTb[i] != null) _beaconTb[i].Text = parts[i].Trim();
        }

        // =============== 从机服务(4) ===============
        private void BuildService()
        {
            Card c = MkCard(4, "从角色服务 UUID SERVICE(6.3.11)", "16位/128位服务;重启生效;基础UUID第3、4字节被替换");
            RowStack rs = new RowStack(c.Body);

            rs.RowBegin(0);
            rs.RowTitle("UUID 位数", Tips["SERVICE"]);
            svc16 = new DarkRadio(); svc16.Text = "16位(默认)"; svc16.Checked = true;
            svc16.CheckedChanged += delegate { SvcModeChanged(); };
            rs.RowControl(svc16, 14);
            FitDark(svc16);
            svc128 = new DarkRadio(); svc128.Text = "128位";
            svc128.CheckedChanged += delegate { SvcModeChanged(); };
            rs.RowControl(svc128, 0);
            FitDark(svc128);
            // 当前选择回显(更明确)
            _svcModeLbl = new Label();
            _svcModeLbl.ForeColor = Theme.AccentHi;
            _svcModeLbl.AutoSize = true;
            _svcModeLbl.Text = "当前: 16位";
            rs.RowGap(18);
            rs.RowControl(_svcModeLbl, 0);
            rs.RowEnd(6);

            string[] fn = new string[] { "服务UUID", "接收通道", "发送通道", "AT通道" };
            string[] fd = new string[] { "FFF0", "FFF1", "FFF2", "FFF3" };
            TextBox[] fts = new TextBox[4];
            rs.RowBegin(0);
            rs.RowTitle("通道UUID", Tips["SERVICE"]);
            for (int i = 0; i < 4; i++)
            {
                rs.RowLabel(fn[i], Tips["SERVICE"]);
                TextBox ft = new TextBox(); Theme.StyleText(ft); ft.Width = 72; ft.Text = fd[i];
                fts[i] = ft;
                rs.RowControl(ft, i < 3 ? 14 : 0);
            }
            rs.RowEnd(6);
            _svcTb = fts;

            rs.RowBegin(0);
            rs.RowLabel("基础UUID(32hex)", Tips["SERVICE"]);
            svcBase = new TextBox(); Theme.StyleText(svcBase); svcBase.Width = 320;
            svcBase.Text = "9ECADC240EE5A9E093F3A3B50000406E";
            rs.RowControl(svcBase, 0);
            rs.RowEnd(6);

            rs.RowBegin(0);
            FlatBtn bq = rs.RowButton("查询 SERVICE", 112, BtnKind.Normal, Tips["SERVICE"]);
            bq.Click += delegate { QueryOne("SERVICE"); };
            FlatBtn bs = rs.RowButton("下发 SERVICE", 112, BtnKind.Accent, Tips["SERVICE"]);
            bs.Click += delegate
            {
                string cmd;
                if (svc128.Checked)
                {
                    if (!Regex.IsMatch(svcBase.Text.Trim(), "^[0-9A-Fa-f]{32}$"))
                    { MessageBox.Show("基础UUID需 32 位十六进制", "提示"); return; }
                    cmd = string.Format("AT+SERVICE=1,{0},{1},{2},{3},{4}",
                        fts[0].Text.Trim(), fts[1].Text.Trim(), fts[2].Text.Trim(), fts[3].Text.Trim(), svcBase.Text.Trim());
                }
                else
                    cmd = string.Format("AT+SERVICE=0,{0},{1},{2},{3}",
                        fts[0].Text.Trim(), fts[1].Text.Trim(), fts[2].Text.Trim(), fts[3].Text.Trim());
                if (MessageBox.Show(cmd + Environment.NewLine + "重启后生效,继续?", "确认", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    SendAndWait(cmd, "SERVICE");
            };
            rs.RowEnd(2);
            CardEnd(c, rs);
            SvcModeChanged();
        }
        TextBox[] _svcTb;
        Label _svcModeLbl;
        private void SvcModeChanged()
        {
            if (svcBase != null) svcBase.Enabled = svc128 != null && svc128.Checked;
            if (_svcModeLbl != null && svc128 != null)
                _svcModeLbl.Text = svc128.Checked ? "当前: 128位" : (svc16 != null && svc16.Checked ? "当前: 16位" : "当前: -");
        }

        private void FillService()
        {
            string v = Str(_state, "SERVICE");
            string[] parts = v.Split(',');
            if (parts.Length >= 1)
            {
                if (parts[0].Trim() == "1") { svc128.Checked = true; }
                else { svc16.Checked = true; }
            }
            for (int i = 1; i < parts.Length && i <= 4; i++)
                if (_svcTb[i - 1] != null) _svcTb[i - 1].Text = parts[i].Trim();
            if (parts.Length >= 6 && svcBase != null) svcBase.Text = parts[5].Trim();
        }

        // =============== 主机连接(5) ===============
        private void BuildMaster()
        {
            // 角色提示 + 强制
            Card c0 = MkCard(5, "角色限制", "本页指令仅主角色(1单主 / 2主从一体)有效");
            RowStack rs0 = new RowStack(c0.Body);
            DarkCheck force = new DarkCheck(); force.Text = "强制允许(角色不符仍可发送,实测 +ERR=4)"; FitDark(force);
            force.CheckedChanged += delegate
            {
                _roleOverride = force.Checked;
                ApplyRole(_role);
            };
            rs0.Add(force, 0, 260);
            rs0.NewRow(30);
            CardEnd(c0, rs0);

            // ---- SCAN ----
            Card c1 = MkCard(5, "主角色扫描 SCAN(6.3.12) + 过滤 SCANCMP(6.3.33)", "达20台或超时自动停止;双击行发起连接");
            RowStack rs = new RowStack(c1.Body);

            rs.RowBegin(0);
            rs.RowTitle("扫描", Tips["SCAN"]);
            rs.RowLabel("超时(秒)", Tips["SCAN"]);
            _scanToTxt = new TextBox(); Theme.StyleText(_scanToTxt); _scanToTxt.Width = 50; _scanToTxt.Text = "10";
            rs.RowControl(_scanToTxt, 12);
            _scanNameChk = new DarkCheck(); _scanNameChk.Text = "显示名称"; FitDark(_scanNameChk); _scanNameChk.Checked = true;
            rs.RowControl(_scanNameChk, 12, Tips["SCAN"]);
            btnScanStart = rs.RowButton("开始扫描", 100, BtnKind.Accent, Tips["SCAN"]);
            btnScanStart.Click += delegate { StartScan(); };
            btnScanStop = rs.RowButton("停止", 74, BtnKind.Normal, Tips["SCAN"]);
            btnScanStop.Click += delegate
            {
                // 先停止收集(避免 AT+SCAN=0 的应答混入列表),再下发停止指令
                if (_se != null)
                {
                    _se.ScanActive = false;
                    _scanDeadline = DateTime.MinValue;
                    _se.Query("AT+SCAN=0", null, 2000, 300);
                    stReply.Text = "扫描已停止(" + scanLv.Items.Count + " 台)";
                }
            };
            FlatBtn bclr = rs.RowButton("清空列表", 84, BtnKind.Normal, null);
            bclr.Click += delegate { scanLv.Items.Clear(); };
            rs.RowEnd(6);

            rs.RowBegin(0);
            rs.RowTitle("过滤", Tips["SCANCMP"]);
            _scCmpOn = new DarkCombo(); _scCmpOn.Width = 46;
            _scCmpOn.Items.Add("0"); _scCmpOn.Items.Add("1"); _scCmpOn.SelectedIndex = 0;
            rs.RowControl(_scCmpOn, 10, Tips["SCANCMP"]);
            _scCmpName = new TextBox(); Theme.StyleText(_scCmpName); _scCmpName.Width = 120; _scCmpName.Text = "BT53";
            rs.RowControl(_scCmpName, 12, Tips["SCANCMP"]);
            FlatBtn bscq = rs.RowButton("查询过滤", 84, BtnKind.Normal, Tips["SCANCMP"]);
            bscq.Click += delegate { QueryOne("SCANCMP"); };
            FlatBtn bscs = rs.RowButton("下发过滤", 84, BtnKind.Accent, Tips["SCANCMP"]);
            bscs.Click += delegate { SendAndWait("AT+SCANCMP=" + _scCmpOn.Text + "," + _scCmpName.Text, "SCANCMP"); };
            rs.RowEnd(6);

            scanLv = new ListView();
            scanLv.View = View.Details; scanLv.FullRowSelect = true; scanLv.HideSelection = false;
            scanLv.BorderStyle = BorderStyle.FixedSingle; scanLv.Height = 170;
            scanLv.BackColor = Color.FromArgb(24, 24, 27); scanLv.ForeColor = Theme.TxT; scanLv.Font = Theme.FMonoS;
            scanLv.Columns.Add("序号", 48); scanLv.Columns.Add("MAC", 130);
            scanLv.Columns.Add("RSSI", 55); scanLv.Columns.Add("名称", 250);
            rs.Host.Controls.Add(scanLv);
            scanLv.SetBounds(14, rs.Y, 820, 170);
            rs.Host.Resize += delegate
            { scanLv.Width = Math.Max(400, rs.Host.ClientSize.Width - 28); };
            rs.NewRow(176);
            scanLv.DoubleClick += delegate { ConnectSelected(); };
            rs.RowBegin(0);
            FlatBtn bcon = rs.RowButton("连接选中行", 108, BtnKind.Accent, Tips["CONNECT"]);
            bcon.Click += delegate { ConnectSelected(); };
            rs.RowEnd(2);
            CardEnd(c1, rs);

            // ---- CONNECT / TRM / DISCON ----
            Card c2 = MkCard(5, "连接管理", "CONNECT(6.3.14) · CONNECT_LIST(6.3.16) · DISCON(6.3.17) · TRM_HANDLE(6.3.20)");
            RowStack rs2 = new RowStack(c2.Body);

            rs2.RowBegin(0);
            rs2.RowTitle("连接", Tips["CONNECT"]);
            rs2.RowLabel("MAC", Tips["CONNECT"]);
            connMacTxt = new TextBox(); Theme.StyleText(connMacTxt); connMacTxt.Width = 156;
            connMacTxt.Font = Theme.FMono; connMacTxt.Text = "C1:02:03:04:05:06";
            rs2.RowControl(connMacTxt, 12, Tips["CONNECT"]);
            FlatBtn bcn = rs2.RowButton("发起连接", 80, BtnKind.Accent, Tips["CONNECT"]);
            bcn.Click += delegate { SendConnect(connMacTxt.Text); };
            FlatBtn bcl = rs2.RowButton("已连接列表", 80, BtnKind.Normal, Tips["CONNECT_LIST"]);
            bcl.Click += delegate { QueryOne("CONNECT_LIST"); };
            rs2.RowEnd(6);

            rs2.RowBegin(0);
            rs2.RowTitle("透传", Tips["TRM_HANDLE"]);
            rs2.RowLabel("handle", Tips["TRM_HANDLE"]);
            _trmTxt = new TextBox(); Theme.StyleText(_trmTxt); _trmTxt.Width = 50; _trmTxt.Text = "1";
            rs2.RowControl(_trmTxt, 12, Tips["TRM_HANDLE"]);
            FlatBtn btq = rs2.RowButton("查询", 80, BtnKind.Normal, Tips["TRM_HANDLE"]);
            btq.Click += delegate { QueryOne("TRM_HANDLE"); };
            FlatBtn bts = rs2.RowButton("设置", 80, BtnKind.Accent, Tips["TRM_HANDLE"]);
            bts.Click += delegate { SendAndWait("AT+TRM_HANDLE=" + _trmTxt.Text, "TRM_HANDLE"); };
            rs2.RowGap(14);
            FlatBtn bdis = rs2.RowButton("断开全部", 80, BtnKind.Danger, Tips["DISCON"]);
            bdis.Click += delegate
            {
                if (MessageBox.Show("断开全部连接?", "确认", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    SendAndWait("AT+DISCON", null);
            };
            rs2.RowEnd(2);
            CardEnd(c2, rs2);

            // ---- SEND(交互式) + 收包 ----
            Card c3 = MkCard(5, "指令发送数据 SEND(6.3.15) 与接收", "无连接返回 +ERR=3;接收打印 +RECEIVED:handle,len");
            RowStack rs3 = new RowStack(c3.Body);

            rs3.RowBegin(0);
            rs3.RowTitle("发送", Tips["SEND"]);
            rs3.RowLabel("handle", Tips["SEND"]);
            sendHandleTxt = new TextBox(); Theme.StyleText(sendHandleTxt); sendHandleTxt.Width = 46; sendHandleTxt.Text = "1";
            rs3.RowControl(sendHandleTxt, 12, Tips["SEND"]);
            rs3.RowLabel("数据", Tips["SEND"]);
            sendDataTxt = new TextBox(); Theme.StyleText(sendDataTxt); sendDataTxt.Width = 180;
            rs3.RowControl(sendDataTxt, 10, Tips["SEND"]);
            sendFmtCmb = new DarkCombo(); sendFmtCmb.Width = 78;
            sendFmtCmb.Items.Add("ascii"); sendFmtCmb.Items.Add("hex"); sendFmtCmb.SelectedIndex = 0;
            rs3.RowControl(sendFmtCmb, 12, Tips["SEND"]);
            rs3.RowLabel("超时", Tips["SEND"]);
            sendToTxt = new TextBox(); Theme.StyleText(sendToTxt); sendToTxt.Width = 58; sendToTxt.Text = "2000";
            rs3.RowControl(sendToTxt, 10, Tips["SEND"]);
            rs3.RowEnd(6);

            rs3.RowBegin(0);
            FlatBtn bgo = rs3.RowButton("启动 SEND", 80, BtnKind.Accent, Tips["SEND"]);
            bgo.Click += delegate { StartSend(); };
            btnBleSend = rs3.RowButton("发送数据", 80, BtnKind.Accent, Tips["SEND"]);
            btnBleSend.Enabled = false;
            btnBleSend.Click += delegate { SendBleData(); };
            rs3.RowEnd(6);

            rtbRecv = new RichTextBox();
            rtbRecv.BackColor = Color.FromArgb(24, 24, 27); rtbRecv.ForeColor = Theme.RxCol;
            rtbRecv.Font = Theme.FMono; rtbRecv.BorderStyle = BorderStyle.None; rtbRecv.ReadOnly = true;
            rtbRecv.Height = 96; rtbRecv.DetectUrls = false;
            rs3.Host.Controls.Add(rtbRecv);
            rtbRecv.SetBounds(14, rs3.Y, 820, 96);
            rs3.Host.Resize += delegate { rtbRecv.Width = Math.Max(400, rs3.Host.ClientSize.Width - 28); };
            rs3.NewRow(102);
            CardEnd(c3, rs3);

            // ---- AUTO_CNT / DEV_DEL ----
            Card c4 = MkCard(5, "自动重连 AUTO_CNT(6.3.18) · 删除 DEV_DEL(6.3.19)", "重启生效;删除不存在的 MAC 实测 +ERR=2");
            RowStack rs4 = new RowStack(c4.Body);

            rs4.RowBegin(0);
            rs4.RowTitle("重连列表", Tips["AUTO_CNT"]);
            autoStateLbl = new Label(); autoStateLbl.ForeColor = Theme.AccentHi; autoStateLbl.AutoSize = true;
            autoStateLbl.Text = "-";
            rs4.RowControl(autoStateLbl, 12, Tips["AUTO_CNT"]);
            FlatBtn baq = rs4.RowButton("查询", 80, BtnKind.Normal, Tips["AUTO_CNT"]);
            baq.Click += delegate { QueryOne("AUTO_CNT"); };
            rs4.RowGap(10);
            rs4.RowLabel("开关", Tips["AUTO_CNT"]);
            autoOnCmb = new DarkCombo(); autoOnCmb.Width = 46;
            autoOnCmb.Items.Add("0"); autoOnCmb.Items.Add("1"); autoOnCmb.SelectedIndex = 1;
            rs4.RowControl(autoOnCmb, 12, Tips["AUTO_CNT"]);
            rs4.RowLabel("MAC", Tips["AUTO_CNT"]);
            autoMacTxt = new TextBox(); Theme.StyleText(autoMacTxt); autoMacTxt.Width = 148; autoMacTxt.Font = Theme.FMono;
            rs4.RowControl(autoMacTxt, 10, Tips["AUTO_CNT"]);
            rs4.RowEnd(6);

            rs4.RowBegin(0);
            FlatBtn badd = rs4.RowButton("开启+该MAC", 80, BtnKind.Accent, Tips["AUTO_CNT"]);
            badd.Click += delegate
            { SendAndWait("AT+AUTO_CNT=" + autoOnCmb.Text + "," + autoMacTxt.Text, "AUTO_CNT"); };
            FlatBtn bsub = rs4.RowButton("仅设开关", 80, BtnKind.Normal, Tips["AUTO_CNT"]);
            bsub.Click += delegate { SendAndWait("AT+AUTO_CNT=" + autoOnCmb.Text, "AUTO_CNT"); };
            FlatBtn bdel = rs4.RowButton("删除该MAC", 80, BtnKind.Normal, Tips["DEV_DEL"]);
            bdel.Click += delegate { SendAndWait("AT+DEV_DEL=" + autoMacTxt.Text, "AUTO_CNT"); };
            FlatBtn ball = rs4.RowButton("清空列表(ALL)", 80, BtnKind.Danger, Tips["DEV_DEL"]);
            ball.Click += delegate
            {
                if (MessageBox.Show("删除全部自动重连设备?", "确认", MessageBoxButtons.OKCancel) == DialogResult.OK)
                    SendAndWait("AT+DEV_DEL=ALL", "AUTO_CNT");
            };
            rs4.RowEnd(2);
            CardEnd(c4, rs4);

            // ---- OBSERVER ----
            Card c5 = MkCard(5, "观察者 OBSERVER(6.3.21)", "持续监听广播;模式2需先 SCAN_PHY=1;不保存");
            RowStack rs5 = new RowStack(c5.Body);

            rs5.RowBegin(0);
            rs5.RowTitle("观察者", Tips["OBSERVER"]);
            rs5.RowLabel("模式", Tips["OBSERVER"]);
            obsModeCmb = new DarkCombo(); obsModeCmb.Width = 56;
            obsModeCmb.Items.Add("0"); obsModeCmb.Items.Add("1"); obsModeCmb.Items.Add("2"); obsModeCmb.SelectedIndex = 1;
            rs5.RowControl(obsModeCmb, 10, Tips["OBSERVER"]);
            FlatBtn boo = rs5.RowButton("启动", 80, BtnKind.Accent, Tips["OBSERVER"]);
            boo.Click += delegate
            {
                string md = obsModeCmb.Text;
                if (md == "2") Log("sys", "提示: 扩展广播观察者需先 SCAN_PHY=1,否则仅返回 +OK 不启动");
                if (_se != null) _se.ObserverActive = (md != "0");
                SendAndWait("AT+OBSERVER=" + md, null);
                if (_obsStateLbl != null) _obsStateLbl.Text = "运行中";
            };
            FlatBtn bst = rs5.RowButton("停止", 80, BtnKind.Normal, Tips["OBSERVER"]);
            bst.Click += delegate
            {
                if (_se != null) _se.ObserverActive = false;
                SendAndWait("AT+OBSERVER=0", null);
                if (_obsStateLbl != null) _obsStateLbl.Text = "已停止";
            };
            FlatBtn boq = rs5.RowButton("查询", 80, BtnKind.Normal, Tips["OBSERVER"]);
            boq.Click += delegate { QueryOne("OBSERVER"); };
            rs5.RowGap(10);
            _obsStateLbl = new Label(); _obsStateLbl.ForeColor = Theme.Warn; _obsStateLbl.AutoSize = true;
            _obsStateLbl.Text = "停止";
            rs5.RowControl(_obsStateLbl, 0, Tips["OBSERVER"]);
            rs5.RowEnd(6);

            rs5.RowBegin(0);
            _obsPauseBtn = rs5.RowButton("暂停滚动", 80, BtnKind.Normal, null);
            _obsPauseBtn.Click += delegate
            { _obsAutoScroll = !_obsAutoScroll; _obsPauseBtn.Text = _obsAutoScroll ? "暂停滚动" : "自动滚动"; };
            FlatBtn boc = rs5.RowButton("清空", 80, BtnKind.Normal, null);
            boc.Click += delegate { rtbObs.Clear(); };
            rs5.RowEnd(6);

            rtbObs = new RichTextBox();
            rtbObs.BackColor = Color.FromArgb(24, 24, 27); rtbObs.ForeColor = Theme.Dim;
            rtbObs.Font = Theme.FMonoS; rtbObs.BorderStyle = BorderStyle.None; rtbObs.ReadOnly = true;
            rtbObs.Height = 110; rtbObs.DetectUrls = false;
            rs5.Host.Controls.Add(rtbObs);
            rtbObs.SetBounds(14, rs5.Y, 820, 110);
            rs5.Host.Resize += delegate { rtbObs.Width = Math.Max(400, rs5.Host.ClientSize.Width - 28); };
            rs5.NewRow(116);
            CardEnd(c5, rs5);
        }
        TextBox _scanToTxt, _scCmpName, _trmTxt;
        CheckBox _scanNameChk;
        DarkCombo _scCmpOn;
        Label _obsStateLbl;
        FlatBtn _obsPauseBtn;
        DateTime _scanDeadline = DateTime.MinValue;   // 扫描收集窗口截止时间

        private void StartScan()
        {
            int secs;
            if (!int.TryParse(_scanToTxt.Text.Trim(), out secs) || secs < 1 || secs > 600)
            { MessageBox.Show("超时 1~600 秒", "提示"); return; }
            if (_se == null) { MessageBox.Show("请先连接串口", "提示"); return; }
            Session scanSe = _se;                    // 绑定发起扫描的会话
            scanLv.Items.Clear();
            scanSe.ScanActive = true;
            string show = _scanNameChk.Checked ? "1" : "0";
            string cmd = "AT+SCAN=1," + secs + "," + show;

            // 关键:扫描期间不能因"首条应答后静默"而提前结束 ——
            // 用整段扫描时长作为收集窗口,靠 ScanActive + 超时/停止按钮收尾
            _scanDeadline = DateTime.Now.AddSeconds(secs);
            scanSe.Query(cmd, delegate(List<string> lines, bool okk)
            {
                // 该回调在扫描窗口结束后触发
                scanSe.ScanActive = false;
                _scanDeadline = DateTime.MinValue;
                stReply.Text = "扫描结束(" + scanLv.Items.Count + " 台)";
                Log("sys", "扫描结束,共 " + scanLv.Items.Count + " 台");
            }, secs * 1000 + 4000, secs * 1000 + 2000);   // 静默阈值 = 整个扫描时长
            stReply.Text = "⏳ 扫描中…(最长 " + secs + " 秒)";
        }

        public void ScanAddRow(int idx, string mac, string rssi, string name)
        {
            if (scanLv == null || scanLv.IsDisposed) return;
            foreach (ListViewItem it in scanLv.Items)
                if (it.SubItems[1].Text == mac)
                { it.SubItems[2].Text = rssi; if (name.Length > 0) it.SubItems[3].Text = name; return; }
            ListViewItem ni = new ListViewItem(idx.ToString());
            ni.SubItems.Add(mac); ni.SubItems.Add(rssi); ni.SubItems.Add(name);
            scanLv.Items.Add(ni);
        }

        private void ConnectSelected()
        {
            if (scanLv.SelectedItems.Count == 0) { MessageBox.Show("先在列表中选择设备行", "提示"); return; }
            string mac = scanLv.SelectedItems[0].SubItems[1].Text;
            connMacTxt.Text = mac;
            SendConnect(mac);
        }

        private void SendConnect(string macTxt)
        {
            string mac = (macTxt ?? "").Trim().ToUpper();
            if (!Regex.IsMatch(mac, "^([0-9A-F]{2}:){5}[0-9A-F]{2}$"))
            { MessageBox.Show("需要 6 字节 MAC,例 C1:02:03:04:05:06(手册示例仅 5 字节会被判 +ERR=2)", "提示"); return; }
            if (_se == null) return;
            stReply.Text = "⏳ 连接 " + mac + "…(超时10s)";
            _se.Query("AT+CONNECT=," + mac, delegate(List<string> lines, bool okk) { }, 12000, 400);
        }

        private void StartSend()
        {
            if (_se == null) { MessageBox.Show("请先连接串口", "提示"); return; }
            int h;
            if (!int.TryParse(sendHandleTxt.Text.Trim(), out h) || h < 1 || h > 8)
            { MessageBox.Show("handle 范围 1~8", "提示"); return; }
            byte[] data = ParseDataBox(sendDataTxt, sendFmtCmb);
            if (data == null) return;
            int to;
            if (!int.TryParse(sendToTxt.Text.Trim(), out to) || to < 1 || to > 5000) to = 500;

            _sendPending = data;
            _awaitInput = "SEND";
            _twoStepSession = _se;
            _twoStepExpect = data.Length;
            btnBleSend.Enabled = false;
            btnExtSend.Enabled = false;

            string cmd = "AT+SEND=" + h + "," + data.Length + "," + to;
            _se.QueryTwoStep(cmd, delegate(byte[] _)
            {
                _twoStepExpect = (_se != null && _se.PendingInputNeeded > 0)
                                 ? _se.PendingInputNeeded : data.Length;
                btnBleSend.Enabled = true;
                stReply.Text = "⏳ SEND 待输入 " + _twoStepExpect + " 字节 → 按[发送数据]";
                Log("sys", "SEND:已收到输入提示,等待 " + _twoStepExpect + " 字节(handle=" + h + ")");
            }, to + 3000);
        }

        private void SendBleData()
        {
            Session se = _twoStepSession ?? _se;
            if (se == null) { MessageBox.Show("串口未连接", "提示"); return; }
            if (_sendPending == null) { MessageBox.Show("先[启动 SEND]等模块提示", "提示"); return; }
            if (_twoStepExpect < 0 && se.PendingInputNeeded > 0) _twoStepExpect = se.PendingInputNeeded;

            int need = _twoStepExpect;
            if (need < 0)
            {
                MessageBox.Show("当前没有待发送的 SEND 请求,请先[启动 SEND]", "提示");
                return;
            }
            byte[] data = _sendPending;
            if (data.Length != need)
            {
                if (MessageBox.Show("需要 " + need + " 字节,当前 " + data.Length + " 字节。截断/补齐后发送?",
                    "长度确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                byte[] t = new byte[need];
                Array.Copy(data, t, Math.Min(need, data.Length));
                data = t;
            }

            bool ok = se.SendTwoStepPayload(data);
            if (!ok)
            {
                MessageBox.Show("提交失败:两步请求已结束或超时,请重新启动", "提示");
                btnBleSend.Enabled = false;
                return;
            }
            _twoStepExpect = -1;
            _awaitInput = null;
            _sendPending = null;
            btnBleSend.Enabled = false;
            stReply.Text = "✔ SEND 数据已提交,等待模块 +OK";
        }

        private void FillAutoCnt()
        {
            if (autoStateLbl != null) autoStateLbl.Text = Str(_state, "AUTO_CNT");
        }

        public void OnReceivedLine(string line)
        {
            if (rtbRecv == null || rtbRecv.IsDisposed) return;
            rtbRecv.SelectionStart = rtbRecv.TextLength;
            rtbRecv.SelectionColor = Theme.RxCol;
            rtbRecv.AppendText(DateTime.Now.ToString("HH:mm:ss ") + line + "\n");
            rtbRecv.AppendText(AppendAfterReceived());
            rtbRecv.ScrollToCaret();
        }
        // +RECEIVED 后,模块下一行给出实际数据 — 缓存行在 dispatcher 中会作为普通 RX 打印;此处留界面标记
        private string AppendAfterReceived() { return ""; }

        public void ObserverAddLine(string line)
        {
            if (rtbObs == null || rtbObs.IsDisposed) return;
            if (rtbObs.TextLength > 120000) rtbObs.Clear();
            rtbObs.SelectionStart = rtbObs.TextLength;
            rtbObs.SelectionColor = Theme.Dim;
            rtbObs.AppendText(line + "\n");
            if (_obsAutoScroll) rtbObs.ScrollToCaret();
        }

        public void OnInputPrompt(int n)
        {
            string whoText = "";
            if (n >= 0) whoText = "(" + n + " 字节)";
            if (_awaitInput == "EXT")
            { btnExtSend.Enabled = true; stReply.Text = "⏳ 扩展广播待输入 " + n + " 字节 → 按[发送数据]"; }
            else if (_awaitInput == "SEND")
            { btnBleSend.Enabled = true; stReply.Text = "⏳ SEND 待输入 " + n + " 字节 → 按[发送数据]"; }
            else stReply.Text = "⏳ 模块等待数据输入 " + whoText;
        }

        // =============== 串口与功耗(6) ===============
        private void BuildUart()
        {
            Card c = MkCard(6, "串口波特率 BAUD(6.3.23)", "重启生效,掉电保存;修改后需复位并用新波特率重连");
            RowStack rs = new RowStack(c.Body);
            DarkCombo cb = new DarkCombo(); cb.Width = 100;
            cb.Items.AddRange(new object[] { "1200","2400","4800","9600","14400","19200","38400","57600","115200","230400","460800","921600" });
            cb.SelectedIndex = 8;
            ParamRow(rs, "BAUD", "波特率", cb, 100, true, "范围 1200~921600;默认 115200;本工具修改后需等模块重启完成再重连");
            CardEnd(c, rs);

            Card c2 = MkCard(6, "连接间隔 CONN_INTERVAL(6.3.24)", "参数×1.25ms;默认 16 → 20ms");
            RowStack rs2 = new RowStack(c2.Body);
            ciTxt = new TextBox(); Theme.StyleText(ciTxt); ciTxt.Width = 120; ciTxt.Text = "16";
            ciMsLbl = new Label(); ciMsLbl.ForeColor = Theme.AccentHi; ciMsLbl.AutoSize = true; ciMsLbl.Text = "= 20.00 ms";
            ciTxt.TextChanged += delegate
            {
                double p;
                if (double.TryParse(ciTxt.Text.Trim(), out p)) ciMsLbl.Text = "= " + (p * 1.25).ToString("0.00") + " ms";
                else ciMsLbl.Text = "= ?";
            };
            FlatBtn bq2 = new FlatBtn("查询", BtnKind.Normal); bq2.Width = 62;
            bq2.Click += delegate { QueryOne("CONN_INTERVAL"); };
            FlatBtn bs2 = new FlatBtn("下发", BtnKind.Accent); bs2.Width = 62;
            bs2.Click += delegate
            {
                int p;
                if (!int.TryParse(ciTxt.Text.Trim(), out p) || p < 6 || p > 3200)
                { MessageBox.Show("参数范围 6~3200(100ms=80)", "提示"); return; }
                SendAndWait("AT+CONN_INTERVAL=" + p, "CONN_INTERVAL");
            };
            Label ciLbl = new Label(); ciLbl.Text = "连接间隔"; ciLbl.ForeColor = Theme.TxT; ciLbl.Font = Theme.F; ciLbl.AutoSize = true;
            Label ciCurKey = new Label(); ciCurKey.Text = "毫秒"; ciCurKey.ForeColor = Theme.Dim; ciCurKey.AutoSize = true;
            Label ciCurVal = Theme.Cur(); ciCurVal.AutoSize = true;
            _cur["CUR_CONN_INTERVAL"] = ciCurVal;
            DarkTips.Set(ciLbl, Tips["CONN_INTERVAL"]);
            DarkTips.Set(ciTxt, Tips["CONN_INTERVAL"]);
            rs2.AddGridRow(ciLbl, ciTxt, ciCurKey, ciMsLbl, bq2, bs2);
            CardEnd(c2, rs2);

            Card c3 = MkCard(6, "状态输出 LOGMSG(6.3.27) 与看门狗 WDOG(6.3.29)", "模块事件打印开关;看门狗关闭可降 2~3uA");
            RowStack rs3 = new RowStack(c3.Body);
            DarkCombo cl = new DarkCombo(); cl.Width = 70;
            cl.Items.Add("0"); cl.Items.Add("1"); cl.SelectedIndex = 1;
            ParamRow(rs3, "LOGMSG", "状态打印", cl, 70, true,
                "1=开启(默认):连接/断开/POWERUP/RECEIVE TIMEOUT 等事件打印,日志区橙色显示;0=全部静默");
            DarkCombo cw = new DarkCombo(); cw.Width = 70;
            cw.Items.Add("0"); cw.Items.Add("1"); cw.SelectedIndex = 0;
            ParamRow(rs3, "WDOG", "看门狗", cw, 70, true,
                "重启生效,掉电保存;默认开启(您模块实测当前为 0=关闭)");
            CardEnd(c3, rs3);

            Card c4 = MkCard(6, "串口/BLE 开关 SLEEP(6.3.28)", "配对开关;关闭串口后需 CTS(接RTS)上升沿唤醒");
            RowStack rs4 = new RowStack(c4.Body);
            slpUartCmb = new DarkCombo(); slpUartCmb.Width = 150;
            slpUartCmb.Items.Add("串口1 / BLE1"); slpUartCmb.Items.Add("串口1 / BLE0");
            slpUartCmb.Items.Add("串口0 / BLE1"); slpUartCmb.Items.Add("串口0 / BLE0");
            slpUartCmb.SelectedIndex = 0;
            slpBleCmb = null;   // 合并到单个下拉,避免两行错位
            Label lUart = new Label(); lUart.Text = "串口/BLE 开关"; lUart.ForeColor = Theme.TxT; lUart.Font = Theme.F; lUart.AutoSize = true;
            DarkTips.Set(lUart, Tips["SLEEP"]);
            DarkTips.Set(slpUartCmb, Tips["SLEEP"]);
            Label cvU = Theme.Cur(); cvU.AutoSize = true;
            Label kU = new Label(); kU.Text = "当前"; kU.ForeColor = Theme.Dim; kU.AutoSize = true;
            FlatBtn bq4 = new FlatBtn("查询", BtnKind.Normal); bq4.Width = 80; bq4.Height = 26;
            bq4.Click += delegate { QueryOne("SLEEP"); };
            FlatBtn bs4 = new FlatBtn("下发", BtnKind.Accent); bs4.Width = 80; bs4.Height = 26;
            bs4.Click += delegate
            {
                int idx = slpUartCmb.SelectedIndex;
                string u = (idx == 0 || idx == 1) ? "1" : "0";
                string b = (idx == 0 || idx == 2) ? "1" : "0";
                string cmd = "AT+SLEEP=" + u + "," + b;
                if (u == "0")
                {
                    DialogResult r = MessageBox.Show("⚠ 关闭串口后模块不再收发 AT 指令!" + Environment.NewLine +
                        "需 CTS(接 USB 转串口的 RTS)上升沿唤醒 —— 可用顶栏 [RTS唤醒] 按钮。" + Environment.NewLine + Environment.NewLine +
                        cmd + Environment.NewLine + "继续?", "危险操作确认", MessageBoxButtons.OKCancel);
                    if (r != DialogResult.OK) return;
                }
                SendAndWait(cmd, "SLEEP");
            };
            rs4.AddGridRow(lUart, slpUartCmb, kU, cvU, bq4, bs4);
            _cur["CUR_SLEEP"] = cvU;
            _sleepBleCur = null;
            CardEnd(c4, rs4);
        }
        Label _sleepBleCur;

        private void FillSleep()
        {
            string v = Str(_state, "SLEEP");
            Match m = Regex.Match(v, "([01])\\s*,\\s*([01])");
            if (!m.Success || slpUartCmb == null) return;
            string u = m.Groups[1].Value, b = m.Groups[2].Value;
            int idx;
            if (u == "1" && b == "1") idx = 0;
            else if (u == "1" && b == "0") idx = 1;
            else if (u == "0" && b == "1") idx = 2;
            else idx = 3;
            slpUartCmb.SelectedIndex = idx;
        }

        private void FillPhy()
        {
            string v = Str(_state, "PHY");
            int n;
            if (!int.TryParse((v ?? "").Trim(), out n)) return;
            int[] bits = new int[] { 1, 2, 4, 8 };
            for (int i = 0; i < 4; i++)
                if (phyChecks[i] != null) phyChecks[i].Checked = (n & bits[i]) != 0;
        }

        private void FillConnIv()
        {
            string v = Str(_state, "CONN_INTERVAL").Trim();
            int n;
            if (int.TryParse(v, out n) && ciTxt != null) { ciTxt.Text = v; }
        }

        // =============== 认证与安全(7) ===============
        private void BuildAuth()
        {
            Card c = MkCard(7, "APP 配置认证密码 UP_AUTH(6.3.26)", "手机连模块后 6 位密码;仅串口可改");
            RowStack rs = new RowStack(c.Body);
            TextBox tp = new TextBox(); Theme.StyleText(tp); tp.Width = 90; tp.Text = "123456";
            ParamRow(rs, "UP_AUTH", "密码(6位数字)", tp, 90, true,
                "立即生效,掉电保存;手机 APP 需该密码通过 AT+AUTH 认证后才能下发无线指令");
            CardEnd(c, rs);

            Card c2 = MkCard(7, "APP 配置认证 AUTH(6.3.25)", "必须大写;实际由手机 APP 配置通道发送");
            RowStack rs2 = new RowStack(c2.Body);

            // 与上一张卡片同一套网格列:名称/输入/当前/值/查询/下发 全部对齐
            Label aLbl = new Label();
            aLbl.Text = "AUTH 认证"; aLbl.ForeColor = Theme.TxT; aLbl.Font = Theme.F; aLbl.AutoSize = true;
            DarkTips.Set(aLbl, Tips["AUTH"]);

            authTxt = new TextBox(); Theme.StyleText(authTxt); authTxt.Width = 96; authTxt.Text = "123456";
            DarkTips.Set(authTxt, Tips["AUTH"]);

            Label aKey = new Label(); aKey.Text = "当前"; aKey.ForeColor = Theme.Dim; aKey.AutoSize = true;
            Label aVal = Theme.Cur(); aVal.AutoSize = true; aVal.Text = "仅APP";
            DarkTips.Set(aVal, "AUTH 认证仅在手机 APP 配置通道有效\n串口发送 AT+AUTH 会返回 +ERR=4(实测正常现象)");
            _cur["CUR_AUTH"] = aVal;

            FlatBtn bq2 = new FlatBtn("查询", BtnKind.Normal); bq2.Width = 80; bq2.Height = 26;
            bq2.Anchor = AnchorStyles.Right;
            bq2.Click += delegate { QueryOne("AUTH"); DarkTips.Set(aVal, Tips["AUTH"]); };

            // 按钮文案较长,按文字实测宽度,避免被截断
            FlatBtn bgo = new FlatBtn("串口发送", BtnKind.Accent);
            bgo.Height = 26;
            bgo.Width = Math.Max(96, TextRenderer.MeasureText("串口发送", Theme.F).Width + 26);
            bgo.Anchor = AnchorStyles.Right;
            DarkTips.Set(bgo, Tips["AUTH"]);
            bgo.Click += delegate { SendAndWait("AT+AUTH=" + authTxt.Text, null); };

            rs2.AddGridRow(aLbl, authTxt, aKey, aVal, bq2, bgo);
            CardEnd(c2, rs2);
        }

        // =============== AT 终端 / 串口助手发送区(8) ===============
        DarkCombo termFmtCmb, termMacCmb;
        CheckBox newLineChk, termLoopChk;
        TextBox termItvTxt;
        System.Windows.Forms.Timer _loopTimer;

        private void BuildTerm()
        {
            Card c = MkCard(8, "自由发送 / AT 终端", "ASCII / HEX · 可不加回车(透传) · 定时重发 · 宏指令 · 历史↑↓");
            RowStack rs = new RowStack(c.Body);
            Label li = new Label(); li.Text = "内容"; li.ForeColor = Theme.Dim; li.AutoSize = true;
            rs.Add(li, 14, 36);
            termTxt = new TextBox(); Theme.StyleText(termTxt); termTxt.Width = 300; termTxt.Font = Theme.FMono;
            rs.Put(termTxt, 54, 0);
            termTxt.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { TermGo(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; TermUp(); }
                else if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; TermDown(); }
            };
            Label lfmt = new Label(); lfmt.Text = "格式"; lfmt.ForeColor = Theme.Dim; lfmt.AutoSize = true;
            rs.Put(lfmt, 366, 6);
            termFmtCmb = new DarkCombo(); termFmtCmb.Width = 72;
            termFmtCmb.Items.Add("ascii"); termFmtCmb.Items.Add("hex"); termFmtCmb.SelectedIndex = 0;
            rs.Put(termFmtCmb, 398, 0);
            newLineChk = new DarkCheck(); newLineChk.Text = "加\\r\\n"; FitDark(newLineChk); newLineChk.Checked = true;
            rs.Put(newLineChk, 478, 1);
            FlatBtn bgo = new FlatBtn("发送", BtnKind.Accent); bgo.Width = 66;
            rs.Put(bgo, 570, 0); bgo.Click += delegate { TermGo(); };
            rs.NewRow(30);

            // 定时重发 + 宏
            termLoopChk = new DarkCheck(); termLoopChk.Text = "定时重发 间隔"; FitDark(termLoopChk);
            rs.Add(termLoopChk, 14, 100);
            termItvTxt = new TextBox(); Theme.StyleText(termItvTxt); termItvTxt.Width = 56; termItvTxt.Text = "1000";
            rs.Put(termItvTxt, 122, 0);
            Label lms = new Label(); lms.Text = "ms"; lms.ForeColor = Theme.Dim; lms.AutoSize = true;
            rs.Put(lms, 184, 6);
            Label lmac = new Label(); lmac.Text = "宏"; lmac.ForeColor = Theme.Dim; lmac.AutoSize = true;
            rs.Put(lmac, 216, 6);
            termMacCmb = new DarkCombo(); termMacCmb.Width = 210;
            termMacCmb.Items.AddRange(new object[] {
                "AT", "AT+VERSION?", "AT+ROLE?", "AT+NAME?", "AT+MAC?", "AT+PWR?",
                "AT+SCAN=1,10,0", "AT+SCAN=0", "AT+OBSERVER=1", "AT+OBSERVER=0",
                "AT+CONNECT_LIST?", "AT+TRM_HANDLE?", "AT+AUTO_CNT?", "AT+DISCON",
                "AT+RESET", "AT+RESTORE"
            });
            termMacCmb.SelectedIndex = 0;
            rs.Put(termMacCmb, 234, 0);
            FlatBtn bmac = new FlatBtn("发送宏", BtnKind.Normal); bmac.Width = 74;
            rs.Put(bmac, 452, 0);
            bmac.Click += delegate
            {
                if (termMacCmb.SelectedItem == null) return;
                termTxt.Text = termMacCmb.SelectedItem.ToString();
                TermGo();
            };
            FlatBtn bfill = new FlatBtn("仅填入", BtnKind.Ghost); bfill.Width = 68;
            rs.Put(bfill, 534, 0);
            bfill.Click += delegate { if (termMacCmb.SelectedItem != null) { termTxt.Text = termMacCmb.SelectedItem.ToString(); termTxt.Focus(); termTxt.SelectAll(); } };
            rs.NewRow(30);

            termLoopChk.CheckedChanged += delegate
            {
                if (termLoopChk.Checked)
                {
                    int ms;
                    if (!int.TryParse(termItvTxt.Text.Trim(), out ms) || ms < 100) ms = 1000;
                    termItvTxt.Text = ms.ToString();
                    if (_loopTimer == null)
                    {
                        _loopTimer = new System.Windows.Forms.Timer();
                        _loopTimer.Tick += delegate { TermGo(); };
                    }
                    _loopTimer.Interval = ms;
                    _loopTimer.Start();
                    Log("sys", "定时重发已启动: 每 " + ms + " ms");
                }
                else if (_loopTimer != null)
                {
                    _loopTimer.Stop();
                    Log("sys", "定时重发已停止");
                }
            };

            rs.Hint("ASCII+加回车=标准AT指令(带应答等待);ASCII 不勾回车=透传原始数据;HEX=纯十六进制字节发送(严格校验);↑↓ 翻历史");

            int qx = 14;
            string[][] quick = new string[][] {
                new string[] { "AT", "AT" }, new string[] { "AT+ROLE?", "AT+ROLE?" },
                new string[] { "连接列表", "AT+CONNECT_LIST?" }, new string[] { "看门狗", "AT+WDOG?" },
                new string[] { "版本", "AT+VERSION?" }, new string[] { "重连列表", "AT+AUTO_CNT?" }
            };
            Label qtt = new Label(); qtt.Text = "快捷:"; qtt.ForeColor = Theme.Dim; qtt.AutoSize = true;
            rs.Put(qtt, 0, 6);
            qx = 52;
            for (int i = 0; i < quick.Length; i++)
            {
                FlatBtn qb = new FlatBtn(quick[i][0], BtnKind.Normal); qb.Width = 86; qb.Height = 26;
                rs.Put(qb, qx, 0);
                string cc = quick[i][1];
                qb.Click += delegate { if (_se != null) { _se.Query(cc, null, 3000, 300); } };
                qx += 94;
            }
            rs.NewRow(30);
            CardEnd(c, rs);
        }

        private static byte[] ParseHexString(string s)
        {
            s = Regex.Replace(s, @"\s+", "");
            if (s.Length == 0 || s.Length % 2 != 0 || !Regex.IsMatch(s, "^[0-9A-Fa-f]+$")) return null;
            byte[] d = new byte[s.Length / 2];
            for (int i = 0; i < d.Length; i++) d[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return d;
        }

        private void TermGo()
        {
            if (_se == null) { MessageBox.Show("请先连接串口", "提示"); return; }
            string s = termTxt.Text.Trim();
            if (s.Length == 0) return;
            _termHist.Insert(0, s);
            if (_termHist.Count > 50) _termHist.RemoveAt(50);
            _termHistIdx = -1;

            bool hex = (termFmtCmb.Text == "hex");
            if (hex)
            {
                byte[] d = ParseHexString(s);
                if (d == null) { MessageBox.Show("HEX 非法:需要偶数个十六进制字符", "提示"); termLoopChk.Checked = false; return; }
                _se.SendRaw(d, "HEX发送");
                termTxt.SelectAll();
                return;
            }
            if (!newLineChk.Checked)
            {
                _se.SendRaw(Encoding.ASCII.GetBytes(s), "透传发送");
                termTxt.SelectAll();
                return;
            }
            // 标准 AT 指令(带回车)
            if (s.Equals("AT+RESET", StringComparison.OrdinalIgnoreCase) || s.Equals("AT+RESTORE", StringComparison.OrdinalIgnoreCase))
            {
                if (termLoopChk.Checked) { termLoopChk.Checked = false; }
                SendAndWait(s, null);
            }
            else if (s.Equals("AT+SCAN=0", StringComparison.OrdinalIgnoreCase))
            { _se.ScanActive = false; _se.Query(s, null, 2000, 300); }
            else
                _se.Query(s, null, 4000, 350);
            termTxt.SelectAll();
        }
        private void TermUp()
        {
            if (_termHist.Count == 0) return;
            _termHistIdx++;
            if (_termHistIdx >= _termHist.Count) _termHistIdx = _termHist.Count - 1;
            termTxt.Text = _termHist[_termHistIdx];
            termTxt.SelectionStart = termTxt.TextLength;
        }
        private void TermDown()
        {
            if (_termHist.Count == 0 || _termHistIdx < 0) { termTxt.Text = ""; return; }
            _termHistIdx--;
            if (_termHistIdx < 0) termTxt.Text = "";
            else { termTxt.Text = _termHist[_termHistIdx]; termTxt.SelectionStart = termTxt.TextLength; }
        }
    }
}
