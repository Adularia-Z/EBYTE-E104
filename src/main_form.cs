// main_form.cs — 主窗体:壳层(顶栏/导航/日志坞/状态栏) + 连接逻辑 + 总览/基础信息/射频 页
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace E104
{
    public partial class MainForm : Form
    {
        // ===== 常量(手册 v1.6) =====
        static readonly Dictionary<string, string> RoleNames = new Dictionary<string, string>
        { { "0", "从角色(单从)" }, { "1", "主角色(单主)" }, { "2", "主从一体" }, { "3", "Beacon" } };
        static readonly Dictionary<string, string> ErrNames = new Dictionary<string, string>
        { { "1", "指令不存在(拼写错误)" }, { "2", "参数错误" },
          { "3", "操作不允许 / 操作失败" }, { "4", "角色不支持该指令" } };

        // ===== 运行时 =====
        SerialIo _io = null;
        Session _se = null;
        public readonly MultiDevice multi = new MultiDevice();
        System.Windows.Forms.Timer _timer = null;
        readonly Dictionary<string, string> _state = new Dictionary<string, string>();
        readonly Dictionary<string, Label> _cur = new Dictionary<string, Label>();
        readonly Dictionary<string, Label> _ov = new Dictionary<string, Label>();
        string _role = "0";
        bool _roleOverride = false;

        // ===== 壳层控件 =====
        Panel topBar, statusPanel, logDock, navPanel, centerHost;
        DarkCombo cbPort, cbBaud;
        FlatBtn btnConn, btnDisc, btnRts;
        Chip chipConn;
        readonly List<NavBtn> _navBtns = new List<NavBtn>();
        readonly List<Panel> _pages = new List<Panel>();
        int _pageIdx = -1;
        RichTextBox rtbLog;
        // 日志区手动发送(串口助手)
        Panel logSendBar;
        TextBox txtLogSend, txtLogRepeatMs;
        DarkCombo cmbLogFmt;
        DarkCheck chkLogCrlf;
        FlatBtn btnLogSend, btnLogSendRepeat;
        readonly List<string> _logHist = new List<string>();
        int _logHistIdx = -1;
        System.Windows.Forms.Timer _logRepeatTimer = null;
        string _logRepeatText = null;
        CheckBox chkAutoScroll, chkHex;
        FlatBtn btnCollapse, btnClearLog, btnExportLog, btnFreeze;
        Label stRole, stReply, stErr, stCount;
        FlatBtn btnErrRef;
        int _logLines = 0, _tickDiv = 0;
        bool _frozen = false;
        readonly List<string[]> _freezeBuf = new List<string[]>();   // [tag,text]
        const int FreezeCap = 6000;

        // ===== 页面专用(跨方法) =====
        DarkCombo cmbRoleSwitch;
        CheckBox[] phyChecks = new CheckBox[4];
        Label phySumLbl;

        // ===== 规格书参数描述(悬浮提示词典;含本机实测补充) =====
        static readonly Dictionary<string, string> Tips = new Dictionary<string, string>
        {
            { "AT", "AT 测试指令(6.3.1)\n用于测试串口通讯是否正常;应答 +OK" },
            { "NAME", "广播名称(6.3.2)\n范围:≤16字节;立即生效,掉电保存\n默认:CDEBYTE_XXXX(XXXX=MAC末两字节)\n查询应答:+NAME=CDEBYTE_3316(实测)" },
            { "MAC", "MAC 地址(6.3.3)\n只读参数;返回16进制冒号格式\n查询应答:+MAC=4C:5B:B3:C7:33:16(实测)" },
            { "ROLE", "模块角色(6.3.4)\n0=单从(默认) 1=单主 2=主从一体 3=Beacon\n重启生效,掉电保存\n切换角色会清空自动重连列表;Beacon 上电串口关闭,需 CTS(接RTS)上升沿唤醒" },
            { "VERSION", "固件版本(6.3.32)\n查询应答:+VERSION=7413-1-16(实测,A3版本;手册示例 +OK= 前缀与实际不符)\n末两位为版本号" },
            { "PWR", "发射功率(6.3.5)\n取值:-28/-20/-10/-5/-3/0/1/2/4/6 dBm\n立即生效,掉电保存\n限制:扫描/观察者开启或已连接时设置返回 +ERR=3;A1 版本上限 0dBm(实测本模块当前为 0)" },
            { "PHY", "从机物理层速率(6.3.22,位域可多选)\n1=1M(默认)  2=2M  4=125K Coded  8=500K Coded\n组合:参数=勾选求和,如 3=1M+2M\n立即生效,掉电不保存;从角色下生效;连手机需打开通知时生效" },
            { "LE_CODED", "远距离广播(6.3.8)\n0=关(默认) 1=开\n立即生效,掉电保存\n支持角色:单从/主从一体;单主、Beacon、A1 返回 +ERR=4\n开启后为带名称设备,可被 LE CODED 物理层主机扫描并连接" },
            { "SCAN_PHY", "主角色扫描物理层(6.3.13)\n0=1M PHY(默认)  1=LE CODED PHY\n立即生效,掉电保存\n设置后只能扫描/连接同物理层设备;观察者模式2需先设此项;A1 不支持" },
            { "ADV", "广播参数(6.3.6)\n开关:0关/1开;类型:0不可连接/1可连接;间隔:20~10240ms 默认200\n立即生效,掉电保存;仅从角色(单从/主从/Beacon)有效\n单主角色查询仍返回参数,设置返回 +ERR=4(实测)" },
            { "ADVDATA", "自定义广播数据(6.3.7)\n格式:0=ASCII 1=HEX;置于厂商自定义字段,≤26字节\nAT+ADVDATA 掉电保存;AT+ADVDATA_CUR 本帧生效不保存(频繁修改建议用 _CUR 防Flash磨损)\nAT+ADVDATA=0 恢复默认广播数据\n角色:仅单从/主从一体(实测单主 +ERR=4)" },
            { "ADV_EXT", "扩展广播(6.3.9,两步下发)\n长度:0~251(0=恢复默认);输入超时:1~5000ms\n流程:启动 → 模块提示 INPUT EXT ADV:n → 发送 n 字节 → +OK(超时 RECEIVE TIMEOUT)\n启用后设备变为不可连接不可扫描无名称设备;重启后使用最后一次保存的数据" },
            { "BEACON", "Beacon 参数(6.3.10)\nCompanyID(默认4C00) Major(2775) Minor(848F)\n1米参考RSSI:范围-90~-10,默认-48;自定义UUID:32位hex\n立即生效,掉电保存(Beacon 角色下生效)" },
            { "SERVICE", "从角色服务(6.3.11)\n16位:服务/接收/发送/无线AT通道UUID(默认FFF0-FFF3)\n128位:增加基础UUID(32hex),其第3、4字节被服务UUID替换\n重启生效,掉电保存;0000xxxx-0000-1000-8000-00805F9B34FB 基础UUID不可用;仅从角色生效" },
            { "SCAN", "主角色扫描(6.3.12)\n开关:1启动/0停止;超时:1~65535秒;名称:0不显示/1显示\n达20台或超时自动停止,掉电不保存\n仅单主/主从一体有效(实测从角色 +ERR=4)\n表格双击行即按 MAC 发起连接,连接超时10秒" },
            { "SCANCMP", "扫描过滤名称(6.3.33)\n开关:0关(默认)/1开;过滤名:≤20字节\n立即生效,掉电保存;对主角色扫描生效" },
            { "CONNECT", "主角色连接(6.3.14)\n可按扫描列表序号或 MAC 连接;连接超时10秒\n成功:XX:..:XX CONNECTD C*handle(handle 即透传指向)\n注意:手册示例 MAC 仅5字节会被判 +ERR=2(实测);须6字节完整地址" },
            { "CONNECT_LIST", "已连接设备列表(6.3.16)\n格式:handle+标记,MAC 地址\n标记 P = 该连接为从角色下的主设备(手机/主机模块)\n配合 AT+TRM_HANDLE 指定数据转发目标;实测无连接返回 NULL" },
            { "TRM_HANDLE", "指定透传设备(6.3.20)\nhandle 范围1~8\n立即生效,掉电不保存;仅主角色(单主/主从一体)\n多连接时数据默认发往最新连接;实测无连接查询返回 255" },
            { "DISCON", "断开连接(6.3.17)\nAT+DISCON=角色,handle 断指定连接;AT+DISCON 断开全部\n被主动断开的重连设备本次不再自动重连" },
            { "SEND", "指令发送数据(6.3.15,两步下发)\nhandle:1~8;长度:1~300字节;输入超时:1~5000ms 默认500\n流程:启动 → INPUT BLE DATA:n → 发送数据\n无连接返回 +ERR=3(实测);AT模式下收数据打印 +RECEIVED:handle,长度" },
            { "AUTO_CNT", "自动重连(6.3.18)\n开关:0关/1开;参数2可选MAC加入列表(不影响列表已有设备)\n重启生效,掉电保存\n被指令主动断开→本次不重连;重新连接/重启/关开BLE后恢复" },
            { "DEV_DEL", "删除自动重连设备(6.3.19)\n按MAC删除;AT+DEV_DEL=ALL 清空全部\n重启生效,掉电保存\n实测:MAC不存在于列表时返回 +ERR=2(手册写错误码3)" },
            { "OBSERVER", "观察者功能(6.3.21)\n模式:0关 1普通观察者 2扩展广播观察者(需先 SCAN_PHY=1)\n持续监听并打印周边设备的广播(MAC/RSSI/ADV原始HEX)\n立即生效,掉电不保存;仅主角色;收不到每个广播属正常现象" },
            { "BAUD", "串口波特率(6.3.23)\n取值:1200/2400/4800/9600/14400/19200/38400/57600/115200/230400/460800/921600\n默认115200;重启生效,掉电保存\n修改后需复位模块并用新波特率重连工具" },
            { "CONN_INTERVAL", "连接间隔(6.3.24)\n参数范围6~3200,实际间隔 = 参数 × 1.25ms;默认16(即20ms)\n立即生效,掉电保存\n间隔越大转发越慢、动态功耗越低;与手机连接建议不小于20ms" },
            { "LOGMSG", "状态输出(6.3.27)\n1=开启(默认):连接/断开/POWERUP等事件打印(日志区橙色横幅)\n0=关闭,全部状态静默\n立即生效,掉电保存" },
            { "WDOG", "看门狗(6.3.29)\n0=关闭 1=开启(默认)\n重启生效,掉电保存;关闭后功耗约降 2~3uA\n实测:当前模块为 0(关闭)" },
            { "SLEEP", "串口/BLE 开关(6.3.28)\npara1=模块串口  para2=模块BLE(0关/1开)\n0,0=最低休眠(约2uA);1,0=蓝牙静默;0,1=仅关串口\n关闭串口后需 CTS(接RTS)上升沿唤醒——顶栏[RTS唤醒]按钮\n关BLE会断开全部连接,但相关AT指令仍可发送" },
            { "UP_AUTH", "APP配置认证密码(6.3.26)\n6位数字,默认123456\n立即生效,掉电保存;仅串口可修改\n手机APP连接模块后需先经 AT+AUTH 验证此密码才能配置" },
            { "AUTH", "APP配置认证(6.3.25)\n6位字符;单次连接有效,断开重连须再次认证\n仅手机APP配置通道使用;指令必须大写\n实测:串口发送返回 +ERR=4,属正常现象" },
            { "RESET", "模块软复位(6.3.30)\nAT+RESET → +OK,延时100ms后软件复位\n等待 MODULE POWERUP 事件确认重启完成(LOGMSG 开启时橙色横幅显示)" },
            { "RESTORE", "恢复出厂设置(6.3.31)\nAT+RESTORE → +OK,100ms后软复位\n清除全部自定义配置(名称/功率/Service/认证码等)\n过程严禁任何复位与断电;恢复后可 AT+RESET 重试连接" },
        };

        /// <summary>按文字自适应宽度(深色自绘复选/单选;字段声明为基类时走这里)</summary>
        public static void FitDark(Control c)
        {
            if (c == null) return;
            DarkCheck dk = c as DarkCheck; if (dk != null) { dk.FitWidth(); return; }
            DarkRadio dr = c as DarkRadio; if (dr != null) { dr.FitWidth(); }
        }

        /// <summary>网格里的空占位(保持列索引不错位)</summary>
        public static Control Blank()
        {
            Panel p = new Panel();
            p.Width = 1;
            p.Height = 1;
            p.BackColor = Color.Transparent;
            p.Margin = new Padding(0);
            return p;
        }

        public MainForm()
        {
            Text = "智家伴蓝牙串口助手 — E104-BT53 模块调试(规格书 v1.6 · 33 组 AT 指令)";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1024, 620);
            WindowState = FormWindowState.Maximized;   // 直接最大化,充分利用屏幕且不超界
            BackColor = Theme.Bg;
            ForeColor = Theme.TxT;
            Font = Theme.F;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            BuildShell();
            BuildPages();
            BuildAllPages();
            RefreshPorts();
            SwitchPage(0);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 25;
            _tickDiv = 0;
            _timer.Tick += delegate
            {
                multi.PumpAll();
                // 计数/冻结提示周期刷新(约每 0.5s)
                _tickDiv++;
                if (_tickDiv >= 20)
                {
                    _tickDiv = 0;
                    if (_se != null)
                        stCount.Text = string.Format("TX {0} B ({1} 帧) · RX {2} B", _se.TxBytes, _se.TxCount, _se.RxBytes);
                    if (_frozen && btnFreeze != null && _freezeBuf.Count > 0)
                        btnFreeze.Text = "▶ 解冻(缓存 " + _freezeBuf.Count + ("" ) + ")";
                }
            };
            _timer.Start();

            FormClosing += delegate { try { Disconnect(); } catch { } };
        }

        // ================================================================ 壳层
        private void BuildShell()
        {
            // ── 顶栏 ──
            topBar = new Panel();
            topBar.Dock = DockStyle.Top; topBar.Height = 54; topBar.BackColor = Theme.Panel;
            topBar.Paint += delegate(object s, PaintEventArgs e)
            { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1); };

            Label logo = new Label();
            logo.Text = "智家伴"; logo.Font = Theme.FTitle; logo.ForeColor = Theme.TxT;
            logo.AutoSize = true; topBar.Controls.Add(logo);
            Label logo2 = new Label();
            logo2.Text = "蓝牙串口助手 · E104-BT53 模块调试上位机"; logo2.Font = Theme.FSub; logo2.ForeColor = Theme.Dim;
            logo2.AutoSize = true; topBar.Controls.Add(logo2);

            Label lp = new Label(); lp.Text = "串口"; lp.ForeColor = Theme.Dim; lp.AutoSize = true;
            topBar.Controls.Add(lp);
            cbPort = new DarkCombo(); cbPort.Width = 92;
            topBar.Controls.Add(cbPort);
            Label lb = new Label(); lb.Text = "波特率"; lb.ForeColor = Theme.Dim; lb.AutoSize = true;
            topBar.Controls.Add(lb);
            cbBaud = new DarkCombo(); cbBaud.Width = 84;
            cbBaud.Items.AddRange(new object[] { "1200","2400","4800","9600","14400","19200","38400","57600","115200","230400","460800","921600" });
            cbBaud.SelectedIndex = 8;
            topBar.Controls.Add(cbBaud);

            btnConn = new FlatBtn("🔌 连接", BtnKind.Accent); btnConn.Width = 104;
            topBar.Controls.Add(btnConn); btnConn.Click += delegate { Connect(); };
            btnDisc = new FlatBtn("断开", BtnKind.Normal); btnDisc.Width = 76; btnDisc.Enabled = false;
            topBar.Controls.Add(btnDisc); btnDisc.Click += delegate { DisconnectActive(); };
            btnRts = new FlatBtn("⚡ RTS唤醒", BtnKind.Ghost); btnRts.Width = 104;
            topBar.Controls.Add(btnRts); btnRts.Click += delegate { RtsPulse(); };

            topBar.Resize += delegate
            {
                int W = topBar.ClientSize.Width;
                logo.Location = new Point(16, 12);
                logo2.Location = new Point(logo.Right + 8, 19);
                int x = W - 14;
                btnRts.Location = new Point(x - btnRts.Width, 12); x = btnRts.Location.X - 8;
                btnDisc.Location = new Point(x - btnDisc.Width, 12); x = btnDisc.Location.X - 8;
                btnConn.Location = new Point(x - btnConn.Width, 12); x = btnConn.Location.X - 18;
                cbBaud.Location = new Point(x - cbBaud.Width, 13); x = cbBaud.Location.X - 6;
                lb.Location = new Point(x - lb.Width, 17); x = lb.Location.X - 6;
                cbPort.Location = new Point(x - cbPort.Width, 13); x = cbPort.Location.X - 6;
                lp.Location = new Point(x - lp.Width, 17);
            };

            // ── 底部状态栏 ──
            statusPanel = new Panel();
            statusPanel.Dock = DockStyle.Bottom; statusPanel.Height = 30; statusPanel.BackColor = Theme.Panel;
            statusPanel.Paint += delegate(object s, PaintEventArgs e)
            { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, statusPanel.Width, 0); };
            chipConn = new Chip(); chipConn.Width = 100; chipConn.Set("○ 未连接", Theme.Dim, Theme.Dim);
            statusPanel.Controls.Add(chipConn);
            stRole = new Label(); stRole.AutoSize = true; stRole.ForeColor = Theme.AccentHi; stRole.Text = "角色 -";
            statusPanel.Controls.Add(stRole);
            stReply = new Label(); stReply.AutoSize = false; stReply.Height = 18;
            stReply.ForeColor = Theme.RxCol; stReply.Text = "最近应答 -"; stReply.AutoEllipsis = true;
            statusPanel.Controls.Add(stReply);
            stErr = new Label(); stErr.AutoSize = false; stErr.Height = 18;
            stErr.ForeColor = Theme.Bad; stErr.AutoEllipsis = true;
            statusPanel.Controls.Add(stErr);
            stCount = new Label(); stCount.AutoSize = true; stCount.ForeColor = Theme.Dim; stCount.Text = "TX 0 · RX 0";
            statusPanel.Controls.Add(stCount);
            btnErrRef = new FlatBtn("错误码参考", BtnKind.Ghost); btnErrRef.Width = 92; btnErrRef.Height = 24;
            statusPanel.Controls.Add(btnErrRef); btnErrRef.Click += delegate { ShowErrRef(); };
            statusPanel.Resize += delegate { LayoutStatusBar(); };

            // ── 日志坞 ──
            logDock = new Panel();
            logDock.Dock = DockStyle.Fill; logDock.BackColor = Theme.Panel;
            logDock.Paint += delegate(object s, PaintEventArgs e)
            { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, logDock.Width, 0); };
            Panel logBar = new Panel(); logBar.Dock = DockStyle.Top; logBar.Height = 34; logBar.BackColor = Theme.Panel;
            int bx0 = 12;
            Label logTitle = new Label(); logTitle.Text = "串口日志"; logTitle.ForeColor = Theme.TxT;
            logTitle.Font = Theme.FHead; logTitle.AutoSize = true; logTitle.Location = new Point(bx0, 8);
            logBar.Controls.Add(logTitle); bx0 += 80;
            chkAutoScroll = new DarkCheck(); chkAutoScroll.Text = "自动滚动"; chkAutoScroll.Checked = true; FitDark(chkAutoScroll);
            chkAutoScroll.Location = new Point(bx0, 6); logBar.Controls.Add(chkAutoScroll); bx0 += chkAutoScroll.Width + 12;
            chkHex = new DarkCheck(); chkHex.Text = "HEX"; chkHex.Checked = false; FitDark(chkHex);
            chkHex.Location = new Point(bx0, 6); logBar.Controls.Add(chkHex); bx0 += chkHex.Width + 12;
            btnClearLog = new FlatBtn("清空", BtnKind.Ghost); btnClearLog.Width = 60;
            btnClearLog.Location = new Point(bx0, 4); logBar.Controls.Add(btnClearLog); bx0 += 68;
            btnClearLog.Click += delegate { rtbLog.Clear(); _logLines = 0; };
            btnExportLog = new FlatBtn("导出", BtnKind.Ghost); btnExportLog.Width = 60;
            btnExportLog.Location = new Point(bx0, 4); logBar.Controls.Add(btnExportLog); bx0 += 68;
            btnExportLog.Click += delegate { ExportLog(); };
            btnFreeze = new FlatBtn("⏸ 冻结", BtnKind.Normal); btnFreeze.Width = 74;
            btnFreeze.Location = new Point(bx0, 4); logBar.Controls.Add(btnFreeze); bx0 += 82;
            btnFreeze.Click += delegate { ToggleFreeze(); };
            btnCollapse = new FlatBtn("▼ 收起", BtnKind.Ghost); btnCollapse.Width = 74;
            btnCollapse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            logBar.Controls.Add(btnCollapse);
            btnCollapse.Click += delegate
            {
                if (mainSplit == null) return;
                int barOnly = mainSplit.Height - 34 - mainSplit.SplitterWidth;
                if (mainSplit.Panel2.Height > 60)
                {
                    _logRestoreDist = mainSplit.SplitterDistance;
                    if (barOnly >= mainSplit.Panel1MinSize) mainSplit.SplitterDistance = barOnly;
                    btnCollapse.Text = "▲ 展开";
                    if (logSendBar != null) logSendBar.Visible = false;
                }
                else
                {
                    int d = _logRestoreDist > 0 ? _logRestoreDist
                            : mainSplit.Height - Math.Max(140, (int)(mainSplit.Height * 0.32)) - mainSplit.SplitterWidth;
                    int maxAllowed = mainSplit.Height - mainSplit.Panel1MinSize - mainSplit.SplitterWidth;
                    if (d > maxAllowed) d = maxAllowed;
                    if (d >= mainSplit.Panel1MinSize) mainSplit.SplitterDistance = d;
                    btnCollapse.Text = "▼ 收起";
                    if (logSendBar != null) logSendBar.Visible = true;
                }
            };
            logDock.Resize += delegate { btnCollapse.Location = new Point(logDock.Width - btnCollapse.Width - 10, 4); };

            // ── 手动发送栏(常规串口助手式:日志下方输入行) ──
            logSendBar = new Panel();
            logSendBar.Dock = DockStyle.Bottom; logSendBar.Height = 38;
            logSendBar.BackColor = Theme.Panel;
            logSendBar.Paint += delegate(object s, PaintEventArgs e)
            { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, logSendBar.Width, 0); };

            Label lsT = new Label();
            lsT.Text = "发送"; lsT.ForeColor = Theme.Dim; lsT.AutoSize = true; lsT.Location = new Point(12, 11);
            logSendBar.Controls.Add(lsT);

            txtLogSend = new TextBox();
            Theme.StyleText(txtLogSend);
            txtLogSend.Font = Theme.FMono;
            txtLogSend.Location = new Point(48, 7);
            txtLogSend.Height = 24;
            txtLogSend.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            logSendBar.Controls.Add(txtLogSend);
            DarkTips.Set(txtLogSend, "手动发送(串口助手)\nEnter=发送,↑↓=历史记录\nASCII 勾选\"+CRLF\"为标准AT指令;不勾为透传原始数据;HEX=发十六进制字节");

            cmbLogFmt = new DarkCombo();
            cmbLogFmt.Width = 68; cmbLogFmt.Height = 24;
            cmbLogFmt.Items.Add("ascii"); cmbLogFmt.Items.Add("hex");
            cmbLogFmt.SelectedIndex = 0;
            cmbLogFmt.Location = new Point(0, 7);
            cmbLogFmt.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            logSendBar.Controls.Add(cmbLogFmt);

            chkLogCrlf = new DarkCheck(); chkLogCrlf.Text = "+CRLF"; FitDark(chkLogCrlf); chkLogCrlf.Checked = true;
            chkLogCrlf.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            logSendBar.Controls.Add(chkLogCrlf);

            btnLogSend = new FlatBtn("发送", BtnKind.Accent);
            btnLogSend.Width = 62; btnLogSend.Height = 26;
            btnLogSend.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            logSendBar.Controls.Add(btnLogSend);
            btnLogSend.Click += delegate { LogSendNow(); };

            btnLogSendRepeat = new FlatBtn("定时重发", BtnKind.Normal);
            btnLogSendRepeat.Width = 78; btnLogSendRepeat.Height = 26;
            btnLogSendRepeat.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            logSendBar.Controls.Add(btnLogSendRepeat);
            btnLogSendRepeat.Click += delegate { ToggleLogRepeat(); };

            txtLogRepeatMs = new TextBox();
            Theme.StyleText(txtLogRepeatMs);
            txtLogRepeatMs.Text = "1000";
            txtLogRepeatMs.Width = 56; txtLogRepeatMs.Height = 24;
            txtLogRepeatMs.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            logSendBar.Controls.Add(txtLogRepeatMs);

            logSendBar.Resize += delegate
            {
                int right = logSendBar.ClientSize.Width - 8;
                btnLogSendRepeat.Location = new Point(right - btnLogSendRepeat.Width, 6); right -= btnLogSendRepeat.Width + 6;
                txtLogRepeatMs.Location = new Point(right - txtLogRepeatMs.Width, 7); right -= txtLogRepeatMs.Width + 6;
                btnLogSend.Location = new Point(right - btnLogSend.Width, 6); right -= btnLogSend.Width + 10;
                chkLogCrlf.Location = new Point(right - 62, 8); right -= 66;
                cmbLogFmt.Location = new Point(right - cmbLogFmt.Width, 7); right -= cmbLogFmt.Width + 8;
                txtLogSend.Width = Math.Max(120, right - 48);
            };

            txtLogSend.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; LogSendNow(); }
                else if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; LogHistUp(); }
                else if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; LogHistDown(); }
            };

            rtbLog = new RichTextBox();
            rtbLog.BackColor = Color.FromArgb(24, 24, 27); rtbLog.ForeColor = Theme.TxT;
            rtbLog.Font = Theme.FMono; rtbLog.BorderStyle = BorderStyle.None; rtbLog.ReadOnly = true;
            rtbLog.DetectUrls = false; rtbLog.Dock = DockStyle.Fill;
            logDock.Controls.Add(rtbLog);
            logDock.Controls.Add(logSendBar);
            logDock.Controls.Add(logBar);

            // ── 左侧导航 ──
            navPanel = new Panel();
            navPanel.Dock = DockStyle.Left; navPanel.Width = 200; navPanel.BackColor = Theme.Nav;
            navPanel.Paint += delegate(object s, PaintEventArgs e)
            { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, navPanel.Width - 1, 0, navPanel.Width - 1, navPanel.Height); };
            Label navTitle = new Label(); navTitle.Text = "功能导航"; navTitle.ForeColor = Theme.Dim;
            navTitle.Font = Theme.FSub; navTitle.AutoSize = true; navTitle.Location = new Point(14, 12);
            navPanel.Controls.Add(navTitle);

            // ── 右侧工作区:上下可拖动分隔(上=页面内容,下=串口日志) ──
            centerHost = new Panel();
            centerHost.Dock = DockStyle.Fill; centerHost.BackColor = Theme.Bg;

            mainSplit = new SplitContainer();
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.Orientation = Orientation.Horizontal;
            mainSplit.BackColor = Theme.Bg;
            mainSplit.BorderStyle = BorderStyle.None;
            mainSplit.Panel1.BackColor = Theme.Bg;
            mainSplit.Panel2.BackColor = Theme.Panel;
            mainSplit.Panel1.Controls.Add(centerHost);
            mainSplit.Panel2.Controls.Add(logDock);
            mainSplit.SplitterWidth = 5;
            mainSplit.Panel2MinSize = 34;
            mainSplit.Panel1MinSize = 220;

            Controls.Add(mainSplit);
            Controls.Add(navPanel);
            Controls.Add(statusPanel);
            Controls.Add(topBar);

            // 日志区高度在窗口显示后设置(构造期 Size 未定型)
            this.Shown += delegate
            {
                int h = mainSplit.Height;
                if (h < 200) return;
                // 小窗口给日志更大比例,保证日志可读
                double ratio = h < 800 ? 0.42 : 0.30;
                int want = Math.Max(150, (int)(h * ratio));
                int maxAllowed = h - mainSplit.Panel1MinSize - mainSplit.SplitterWidth;
                if (want > maxAllowed) want = Math.Max(mainSplit.Panel2MinSize, maxAllowed);
                int dist = h - want - mainSplit.SplitterWidth;
                if (dist >= mainSplit.Panel1MinSize) mainSplit.SplitterDistance = dist;
                _logRestoreDist = mainSplit.SplitterDistance;
            };
        }

        public SplitContainer mainSplit;
        private int _logRestoreDist = 0;

        /// <summary>
        /// 三段式状态栏布局:
        ///   [胶囊][角色] | [最近应答(可伸缩,空间不足则隐藏)] | [计数][错误码参考]
        /// 从右往左先固定右侧,再分配中部剩余宽度 —— 窄窗口下 TX/RX 不会被按钮遮挡。
        /// </summary>
        public void LayoutStatusBar()
        {
            if (statusPanel == null || btnErrRef == null) return;
            int W = statusPanel.ClientSize.Width;
            const int pad = 8;

            // 右区:错误码参考 + TX/RX 计数(优先保障)
            int rx = W - pad;
            btnErrRef.Location = new Point(Math.Max(0, rx - btnErrRef.Width), 3);
            rx = btnErrRef.Location.X;

            int countW = TextRenderer.MeasureText(stCount.Text, stCount.Font).Width + 2;
            stCount.Location = new Point(Math.Max(0, rx - countW - 10), 6);
            rx = stCount.Location.X - 10;

            // 左区:连接胶囊 + 角色
            chipConn.Location = new Point(pad, 3);
            int lx = chipConn.Right + 10;
            stRole.Location = new Point(lx, 6);
            lx = stRole.Right + 14;

            // 中区:剩余宽度够则显示最近应答,否则隐藏(而不是被覆盖)
            int midW = rx - lx;
            bool showReply = midW >= 140;
            stReply.Visible = showReply;
            if (showReply)
            {
                stReply.Width = midW;
                stReply.Location = new Point(lx, 6);
            }
            stErr.Visible = false;   // 错误信息复用"最近应答"位置,避免两段争抢
        }

        // ================================================================ 导航 & 页面骨架
        private void BuildPages()
        {
            string[] names = new string[] { "总览", "基础信息", "射频物理层", "广播配置",
                                            "从机服务", "主机连接", "串口与功耗", "认证与安全", "多机协同", "AT 终端" };
            string[] glyphs = new string[] { "◎", "◉", "⇄", "✦", "⊞", "⇉", "⚙", "⚿", "⋈", "❯" };

            for (int i = 0; i < names.Length; i++)
            {
                Panel pg = new Panel();
                pg.Dock = DockStyle.Fill; pg.Visible = false;
                Panel stack = new Panel();
                stack.Dock = DockStyle.Fill; stack.AutoScroll = true;
                stack.Padding = new Padding(14, 10, 14, 6);
                stack.BackColor = Theme.Bg;
                pg.Controls.Add(stack);
                centerHost.Controls.Add(pg);
                _pages.Add(pg);

                NavBtn nb = new NavBtn(names[i], glyphs[i]);
                nb.SetBounds(0, 40 + i * 40, 200, 40);
                nb.Tag = i;
                int idx = i;
                nb.Click += delegate { SwitchPage(idx); };
                navPanel.Controls.Add(nb);
                _navBtns.Add(nb);
            }
        }

        private Panel PageStack(int idx) { return (Panel)_pages[idx].Controls[0]; }

        public void SwitchPage(int idx)
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = (i == idx);
            for (int i = 0; i < _navBtns.Count; i++) _navBtns[i].Active = (i == idx);
            _pageIdx = idx;
        }

        /// <summary>每页顶部的统一操作条(一键读取当前值 / 读全部 / 握手)</summary>
        private void AddPageToolbar(Panel stack, string title, string[] keys)
        {
            Panel bar = new Panel();
            bar.Height = 40; bar.BackColor = Theme.Bg; bar.Dock = DockStyle.Top;
            bar.Paint += delegate(object s, PaintEventArgs e)
            { using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width - 36, bar.Height - 1); };

            Label t = new Label();
            t.Text = title; t.ForeColor = Theme.TxT; t.Font = Theme.FHead; t.AutoSize = true;
            t.Location = new Point(4, 10);
            bar.Controls.Add(t);

            FlatBtn bRead = new FlatBtn("读取本页当前值", BtnKind.Accent);
            bRead.Width = 124; bRead.Height = 28;
            bRead.Location = new Point(t.Right + 18, 6);
            bar.Controls.Add(bRead);
            DarkTips.Set(bRead, "一键读取本页所有参数(6.3 章)\n按参数顺序逐个查询并回填当前值;读取过程自动避让应答,不会粘连帧");
            bRead.Click += delegate { ReadKeys(keys); };

            FlatBtn bAll = new FlatBtn("读取全部", BtnKind.Normal);
            bAll.Width = 92; bAll.Height = 28;
            bAll.Location = new Point(bRead.Right + 8, 6);
            bar.Controls.Add(bAll);
            DarkTips.Set(bAll, "读取全部参数(按角色)\n遍历手册 33 组指令中当前角色支持的查询指令");
            bAll.Click += delegate { ReadAll(); };

            FlatBtn bAt = new FlatBtn("AT 握手", BtnKind.Normal);
            bAt.Width = 88; bAt.Height = 28;
            bAt.Location = new Point(bAll.Right + 8, 6);
            bar.Controls.Add(bAt);
            bAt.Click += delegate { SendAndWait("AT", null); };

            stack.Controls.Add(bar);
            // 操作条需置于最前:由顶部堆叠顺序保证(Dock=Top 先加入者在上)
        }

        /// <summary>按指定参数键列表链式读取</summary>
        public void ReadKeys(string[] keys)
        {
            if (_se == null) { MessageBox.Show("串口未连接,请先[连接]", "提示"); return; }
            if (keys == null || keys.Length == 0) { ReadAll(); return; }
            ChainRead(keys, null);
        }

        // ================================================================ 行构建基础设施
        public class RowStack
        {
            Panel _host;
            int _y;

            public Panel Host { get { return _host; } }
            public int Y { get { return _y; } set { _y = value; } }
            public RowStack(Panel host) { _host = host; _y = 38; }

            public void Add(Control c, int x, int w)
            {
                c.SetBounds(x, _y, w, c.Height);
                c.Anchor = AnchorStyles.Left | AnchorStyles.Top;
                _host.Controls.Add(c);
            }
            public void Put(Control c, int x, int dy)
            {
                _host.Controls.Add(c);
                c.Location = new Point(x, _y + dy);
                c.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            }
            public void NewRow(int dy) { _y += dy; }

            /// <summary>
            /// 用网格列添加一行(6 列:名称/输入/当前/值/查询/下发)。
            /// 同类元素每行都落在同一列上 → 上下对齐;列宽为百分比 → 缩放不重叠。
            /// 行高由 AutoSize 自动计算。
            /// </summary>
            public void AddGridRow(Control label, Control input, Control curKey, Control curVal,
                                   Control btnQuery, Control btnSet)
            {
                GridRows g = _curGrid;
                if (g == null || g.RowCount > 60)
                {
                    // 7 列:名称 输入 当前 值 [查询92abs] [下发92abs] 余量
                    // 输入列收窄、"当前"紧跟其后,避免大片空白
                    g = new GridRows(7, new float[] { 100, 100, 0, 100, 0, 0, 0 });
                    g.ColumnStyles.Clear();
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17f));   // 0 名称
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));   // 1 输入
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46f));  // 2 "当前"
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14f));   // 3 值
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));  // 4 查询
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));  // 5 下发
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));   // 6 余量(按钮靠右)
                    g.Left = 8; g.Top = _y;
                    g.Width = Math.Max(560, _host.ClientSize.Width - 16);
                    g.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                    _host.Controls.Add(g);
                    Panel hostRef = _host;
                    hostRef.Resize += delegate
                    {
                        if (!g.IsDisposed) g.Width = Math.Max(560, hostRef.ClientSize.Width - 16);
                    };
                    _curGrid = g;
                    _grids.Add(g);
                }
                NormalizeBtn(btnQuery, true);
                NormalizeBtn(btnSet, false);
                try { g.PutRow(label, input, curKey, curVal, btnQuery, btnSet, null); }
                catch { }
                _y = g.Top + g.Height;
            }

            /// <summary>统一按钮尺寸与对齐:不管有没有"下发",查询与下发都不变形</summary>
            private static void NormalizeBtn(Control b, bool isQuery)
            {
                if (b == null) return;
                b.Width = 80;
                b.Height = 26;
                b.Anchor = AnchorStyles.Right;
                b.Margin = new Padding(4, 3, 6, 3);
            }

            /// <summary>
            /// 特殊行:左侧块占前两列并跨两行(如 2×2 选项),右侧"值/查询/下发"仍用标准列,
            /// 保证与其它页面的按钮列严格同列。
            /// </summary>
            public void AddGridRowSpan(Control leftBlock, Control curVal, Control btnQuery, Control btnSet)
            {
                GridRows g = _curGrid;
                if (g == null)
                {
                    g = new GridRows(7, null);
                    g.ColumnStyles.Clear();
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17f));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46f));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14f));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92f));
                    g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35f));
                    g.Left = 8; g.Top = _y;
                    g.Width = Math.Max(560, _host.ClientSize.Width - 16);
                    g.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                    _host.Controls.Add(g);
                    Panel hostRef = _host;
                    hostRef.Resize += delegate
                    {
                        if (!g.IsDisposed) g.Width = Math.Max(560, hostRef.ClientSize.Width - 16);
                    };
                    _curGrid = g;
                    _grids.Add(g);
                }
                NormalizeBtn(btnQuery, true);
                NormalizeBtn(btnSet, false);
                g.EnsureRows(g.RowCount + 2);
                int r = g.RowCount - 2;
                g.PutCellSpan(leftBlock, 0, r, 2, 2);   // 选项块跨 2 列 2 行
                g.PutCell(curVal, 3, r);
                g.PutCell(btnQuery, 4, r);
                g.PutCell(btnSet, 5, r);
                _y = g.Top + g.Height;
            }
            private GridRows _curGrid = null;
            readonly List<GridRows> _grids = new List<GridRows>();
            public List<GridRows> Grids { get { return _grids; } }

            /// <summary>结束当前网格行组(下一个卡片/分组重新开始)</summary>
            public void EndGrid()
            {
                if (_curGrid != null) { _y = _curGrid.Top + _curGrid.Height + 4; }
                _curGrid = null;
            }

            /// <summary>该行栈内所有子控件的真实最低边界(用于精确计算卡片高度,避免内容被裁)</summary>
            public int MaxBottom()
            {
                int max = 0;
                for (int i = 0; i < _host.Controls.Count; i++)
                {
                    Control ctl = _host.Controls[i];
                    if (ctl == null || ctl.IsDisposed || !ctl.Visible) continue;
                    int b = ctl.Bottom;
                    if (b > max) max = b;
                }
                return max;
            }
            public Label Hint(string text)
            {
                Label l = Theme.Hint(text);
                _host.Controls.Add(l);
                l.Location = new Point(14, _y);
                l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
                _y += 20;
                return l;
            }

            /// <summary>
            /// 新增一个自适应行(FlowLayoutPanel):子控件从左到右自动排列,不会重叠错位。
            /// 返回的 FlowRow 用 AddLabel/Add 往里面加控件;行高按内容自动计算后推进 Y。
            /// </summary>
            public FlowRow AddRow(int leftIndent)
            {
                FlowRow fr = new FlowRow();
                // 构造期 _host.ClientSize 可能仍是 0:先给一个足够大的初始宽度,避免子控件被压成极窄;
                // 之后由 _host.Resize 与 RefreshLayout() 校正到真实宽度。
                fr.Left = 14 + leftIndent;
                fr.Top = _y;
                fr.Width = 900;
                fr.MaximumSize = Size.Empty;
                fr.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
                _host.Controls.Add(fr);
                Panel hostRef = _host;
                int indent = leftIndent;
                hostRef.Resize += delegate
                {
                    if (!fr.IsDisposed)
                        fr.Width = Math.Max(320, hostRef.ClientSize.Width - 28 - indent);
                };
                _rows.Add(fr);
                _rowPending = fr;
                return fr;
            }

            readonly List<FlowRow> _rows = new List<FlowRow>();

            /// <summary>宿主尺寸确定后校正所有行宽(卡片显示后调用)</summary>
            public void RefreshLayout()
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    FlowRow fr = _rows[i];
                    if (fr.IsDisposed) continue;
                    int w = _host.ClientSize.Width - 28 - Math.Max(0, fr.Left - 14);
                    fr.Width = Math.Max(240, w);

                    // 计算换行后的真实内容高度(FlowLayoutPanel PreferredSize 会随换行增长)
                    int needH = fr.GetPreferredSize(new Size(fr.Width, 0)).Height;
                    if (needH < 30) needH = 30;
                    if (fr.Height != needH) fr.Height = needH;

                    // 高度变化后,后续行整体下移
                    int newTop = fr.Top;
                    for (int k = i + 1; k < _rows.Count; k++)
                    {
                        FlowRow nx = _rows[k];
                        if (nx.IsDisposed) continue;
                        newTop = newTop + fr.Height + 4;
                        if (nx.Top != newTop) nx.Top = newTop;
                        newTop = nx.Top;
                    }
                }
                _host.PerformLayout();
            }

            /// <summary>结束当前自适应行:按行内容实际高度推进 Y(多行时自动换行)</summary>
            public void EndRow(int extra)
            {
                FlowRow fr = _rowPending;
                _rowPending = null;
                if (fr == null) return;
                int h = fr.PreferredSize.Height;
                if (h < 30) h = 30;
                fr.Height = h;
                _y += h + extra;
            }
            private FlowRow _rowPending = null;

            /// <summary>便捷:一行内放"名称 + 控件",返回控件</summary>
            public T Row1<T>(string label, T c, string tip, int leftIndent) where T : Control
            {
                FlowRow fr = AddRow(leftIndent);
                fr.AddFixedLabel(label, 118, false);
                if (!string.IsNullOrEmpty(tip)) DarkTips.Set(fr.Controls[fr.Controls.Count - 1], tip);
                fr.Add(c, 10);
                if (!string.IsNullOrEmpty(tip)) DarkTips.Set(c, tip);
                EndRow(2);
                return c;
            }

            // ── 三段式流式行 API ──
            // RowBegin() → 依次 Label/Input/Button/Gap → RowEnd()
            FlowRow _cur = null;

            public FlowRow Cur { get { return _cur; } }

            public FlowRow RowBegin(int leftIndent)
            {
                _cur = AddRow(leftIndent);
                return _cur;
            }
            public Label RowLabel(string text, string tip)
            {
                if (_cur == null) return null;
                Label l = _cur.AddLabel(text, true, tip, null);
                return l;
            }
            public Label RowTitle(string text, string tip)
            {
                if (_cur == null) return null;
                Label l = _cur.AddLabel(text, false, tip, null);
                l.Font = Theme.F;
                return l;
            }
            public T RowControl<T>(T c, int gapAfter) where T : Control
            {
                if (_cur != null) _cur.Add(c, gapAfter);
                return c;
            }
            public T RowControl<T>(T c, int gapAfter, string tip) where T : Control
            {
                if (_cur != null) { _cur.Add(c, gapAfter); if (!string.IsNullOrEmpty(tip)) DarkTips.Set(c, tip); }
                return c;
            }
            public FlatBtn RowButton(string text, int width, BtnKind kind, string tip)
            {
                FlatBtn b = new FlatBtn(text, kind);
                // 按钮宽度至少容纳文字(实测宽度 + 内边距),避免文字被截断
                int need = TextRenderer.MeasureText(text ?? "", Theme.F,
                              Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 28;
                b.Width = Math.Max(width, need);
                b.Height = 26;
                if (_cur != null) _cur.Add(b, 6);
                if (!string.IsNullOrEmpty(tip)) DarkTips.Set(b, tip);
                return b;
            }
            public void RowGap(int px) { if (_cur != null) _cur.Gap(px); }
            public void RowEnd(int extra)
            {
                EndRow(extra);
                _cur = null;
            }
        }

        public Card MkCard(int pageIdx, string title, string sub)
        {
            Card c = new Card(title, sub);
            PageStack(pageIdx).Controls.Add(c);
            c.BringToFront();
            return c;
        }

        public void CardEnd(Card c, RowStack rs)
        {
            rs.EndGrid();
            c.Height = Math.Max(rs.Y + 10, rs.MaxBottom() + 10);
            rs.RefreshLayout();
            c.Resize += delegate
            {
                try
                {
                    rs.RefreshLayout();
                    int hh = Math.Max(rs.Y + 10, rs.MaxBottom() + 10);
                    if (Math.Abs(c.Height - hh) > 2) c.Height = hh;
                }
                catch { }
            };
        }

        // 通用一行: 名称 [输入] 当前: - [查询][下发](描述信息鼠标悬浮查看)
        // 采用 GridRows 网格列:同类元素天然上下对齐,窗口缩放时列宽等比伸缩、不会重叠
        public void ParamRow(RowStack rs, string key, string label, Control input, int inputW,
                             bool withSet, string hint)
        {
            string tip;
            if (!Tips.TryGetValue(key, out tip)) tip = hint;

            Control cLbl = new Label();
            ((Label)cLbl).Text = label;
            ((Label)cLbl).ForeColor = Theme.TxT;
            ((Label)cLbl).Font = Theme.F;
            ((Label)cLbl).AutoSize = true;
            ((Label)cLbl).Cursor = Cursors.Hand;
            input.Width = Math.Max(60, inputW);

            Label cq = new Label(); cq.Text = "当前"; cq.ForeColor = Theme.Dim; cq.AutoSize = true;
            Label cv = Theme.Cur(); cv.AutoSize = true;
            _cur["CUR_" + key] = cv;

            FlatBtn bq = new FlatBtn("查询", BtnKind.Normal); bq.Width = 62;
            bq.Click += delegate { QueryOne(key); };
            FlatBtn bs = null;
            if (withSet)
            {
                bs = new FlatBtn("下发", BtnKind.Accent); bs.Width = 62;
                bs.Click += delegate { DoParamSet(key, input); };
            }

            if (!string.IsNullOrEmpty(tip))
            {
                DarkTips.Set(cLbl, tip);
                DarkTips.Set(input, tip);
                DarkTips.Set(cq, tip);
            }
            if (bs != null) rs.AddGridRow(cLbl, input, cq, cv, bq, bs);
            else rs.AddGridRow(cLbl, input, cq, cv, bq, null);
        }

        public void DoParamSet(string key, Control input)
        {
            string v = InputText(input);
            if (v.Length == 0) { MessageBox.Show("参数为空", "提示"); return; }
            if (key == "BAUD" &&
                MessageBox.Show("修改波特率需重启后生效,继续?", "确认", MessageBoxButtons.OKCancel) != DialogResult.OK)
                return;
            SendAndWait("AT+" + key + "=" + v, key);
        }

        public string InputText(Control c)
        {
            TextBox tb = c as TextBox; if (tb != null) return tb.Text.Trim();
            DarkCombo dc = c as DarkCombo; if (dc != null) return dc.Text.Trim();
            return "";
        }
        public void SetInputText(Control c, string v)
        {
            TextBox tb = c as TextBox; if (tb != null) { tb.Text = v; return; }
            DarkCombo dc = c as DarkCombo; if (dc != null) { dc.Text = v; return; }
        }

        // ================================================================ 串口连接
        public void RefreshPorts()
        {
            cbPort.Items.Clear();
            string[] ports = SerialIo.ListPorts();
            cbPort.Items.AddRange(ports);
            if (cbPort.Items.Count > 0) cbPort.SelectedIndex = 0;
            Log("sys", "检测到串口: " + (ports.Length > 0 ? string.Join(", ", ports) : "无"));
        }

        public void Connect()
        {
            if (cbPort.SelectedItem == null) { MessageBox.Show("未选择串口", "提示"); return; }
            string port = cbPort.SelectedItem.ToString();
            if (multi.Count >= MultiDevice.MaxDevices)
            { MessageBox.Show("最多同时接入 " + MultiDevice.MaxDevices + " 个模组", "提示"); return; }
            for (int i = 0; i < multi.Count; i++)
                if (multi.Devices[i].PortName == port)
                { MessageBox.Show(port + " 已接入,请选择其它串口", "提示"); return; }
            try
            {
                int baud = int.Parse(cbBaud.Text);
                DeviceSession d = multi.Add(port, baud, delegate(string msg)
                {
                    try { BeginInvoke((MethodInvoker)delegate { OnPortError(msg); }); } catch { }
                });
                WireSession(d.Se, d);
                Log("sys", "已接入 M" + (d.Index + 1) + " " + port + " @" + baud + " 8-N-1");
                multi.ActiveIndex = d.Index;
                SyncActiveRefs();
                RefreshDeviceList();

                d.Se.Query("AT", delegate(List<string> lines, bool okk)
                {
                    if (LinesHave(lines, "+OK"))
                    {
                        d.Ready = true;
                        Log("sys", "M" + (d.Index + 1) + " " + port + " AT 握手完成 ✔");
                        // 只在激活通道上走链式读取,避免多路争抢 UI 回填
                        if (d == multi.Active) ChainRead(new string[] { "ROLE", "NAME", "MAC", "VERSION" }, null);
                        else d.Se.Query("AT+ROLE?", delegate(List<string> l2, bool o2)
                        { ParseRoleInto(d, l2); RefreshDeviceList(); }, 1200, 120);
                    }
                    else
                    {
                        d.LastError = "无应答";
                        Log("err", "M" + (d.Index + 1) + " " + port + " 无应答(可能休眠/Beacon,试 RTS 唤醒或核对波特率)");
                        RefreshDeviceList();
                    }
                }, 1500, 250);
            }
            catch (Exception ex)
            {
                MessageBox.Show("连接失败: " + ex.Message, "错误");
                Log("sys", "连接失败: " + ex.Message);
            }
            UpdateConnButtons();
        }

        /// <summary>把 AT+ROLE? 应答解析进设备信息</summary>
        public void ParseRoleInto(DeviceSession d, List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                Match m = Regex.Match(lines[i] ?? "", @"^\+ROLE\s*=\s*([0-3])");
                if (m.Success) { int r; int.TryParse(m.Groups[1].Value, out r); d.Role = r; }
            }
        }

        /// <summary>激活通道切换后,同步 _io/_se 引用(所有页面代码继续走这两个引用)</summary>
        public void SyncActiveRefs()
        {
            DeviceSession d = multi.Active;
            _io = d != null ? d.Io : null;
            _se = d != null ? d.Se : null;
            if (d != null) chipConn.Set("● " + d.PortName, Theme.Good, Theme.TxT);
            else chipConn.Set("○ 未连接", Theme.Dim, Theme.Dim);
            UpdateConnButtons();
            if (d != null && d.Role >= 0) ApplyRole(d.Role.ToString());
        }

        public void UpdateConnButtons()
        {
            bool has = multi.Count > 0;
            btnDisc.Enabled = has;
            btnConn.Enabled = multi.CanAdd;
            if (btnConn.Enabled) btnConn.Text = "接入";
            else btnConn.Text = "已达上限";
            btnConn.Invalidate();
            if (chipConn != null && multi.Count > 0)
                chipConn.Set("● " + multi.Count + "/" + MultiDevice.MaxDevices + " 模组", Theme.Good, Theme.TxT);
        }

        public void DisconnectActive()
        {
            DeviceSession d = multi.Active;
            if (d == null) return;
            StopLogRepeat();
            if (_loopTimer != null) { _loopTimer.Stop(); if (termLoopChk != null) termLoopChk.Checked = false; }
            multi.Remove(d);
            Log("sys", "已断开 " + d.PortName);
            SyncActiveRefs();
            RefreshDeviceList();
        }

        public void Disconnect()
        {
            if (_loopTimer != null) { _loopTimer.Stop(); if (termLoopChk != null) termLoopChk.Checked = false; }
            StopLogRepeat();
            multi.DisposeAll();
            _se = null; _io = null;
            chipConn.Set("○ 未连接", Theme.Dim, Theme.Dim);
            UpdateConnButtons();
            RefreshDeviceList();
            Log("sys", "已断开全部模组");
        }

        private void OnPortError(string msg)
        {
            Log("err", "串口异常: " + msg);
            DeviceSession dead = null;
            for (int i = 0; i < multi.Count; i++)
                if (!multi.Devices[i].Io.IsOpen) { dead = multi.Devices[i]; break; }
            if (dead != null) { multi.Remove(dead); Log("sys", "已移除掉线模组 " + dead.PortName); }
            SyncActiveRefs();
            RefreshDeviceList();
        }

        private void RtsPulse()
        {
            if (_io == null || !_io.IsOpen) { MessageBox.Show("请先连接串口", "提示"); return; }
            Log("sys", "RTS 唤醒脉冲发送中(CTS 上升沿 ×3)…");
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            { try { _io.RtsPulse(3); } catch { } });
            th.IsBackground = true; th.Start();
        }

        // ================================================================ 会话接线
        private void WireSession(Session se) { WireSession(se, null); }

        private void WireSession(Session se, DeviceSession owner)
        {
            se.OnLog = delegate(string tag, string text)
            {
                // 多模组时给日志加端口前缀,便于区分来源
                if (owner != null && multi.Count > 1) Log(tag, "[" + owner.PortName + "] " + text);
                else Log(tag, text);
            };
            se.OnLastReply = delegate(string l)
            {
                if (owner != null && owner != multi.Active) return;
                stReply.Text = "最近应答 " + (l.Length > 40 ? l.Substring(0, 40) : l);
            };
            se.OnPrompt = delegate(int n) { OnInputPrompt(n); };
            se.OnReceived = delegate(string l) { OnReceivedLine(l); };
            se.OnScanRow = delegate(int i, string mac, string rssi, string name)
            {
                if (owner != null && owner != multi.Active) return;   // 仅激活通道驱动UI
                ScanAddRow(i, mac, rssi, name);
            };
            se.OnObserverLine = delegate(string l)
            {
                if (owner != null && owner != multi.Active) return;
                ObserverAddLine(l);
            };
            se.OnLinkEvent = delegate(string l)
            {
                if (owner != null && owner != multi.Active) return;
                stReply.Text = "事件 " + l;
            };
            se.OnStateValue = delegate(string k, string v)
            {
                // 只让激活通道回填页面状态;非激活通道仅更新设备信息
                if (owner != null && owner != multi.Active) { TrackDeviceInfo(owner, k, v); return; }
                StateArrived(k, v);
                if (owner != null) TrackDeviceInfo(owner, k, v);
            };
            se.OnErrCode = delegate(string l)
            {
                if (owner != null && owner != multi.Active) return;
                OnErrLine(l);
            };
            se.OnRawRx = delegate(byte[] chunk)
            {
                if (owner == null || owner == multi.Active) RawRx(chunk);
            };
            se.OnRawTx = delegate(byte[] frame)
            {
                if (owner == null || owner == multi.Active) RawTx(frame);
            };
        }

        /// <summary>跟踪各模组的 ROLE/NAME/MAC/VERSION,用于多机列表</summary>
        public void TrackDeviceInfo(DeviceSession d, string k, string v)
        {
            if (d == null) return;
            if (k == "ROLE")
            {
                Match m = Regex.Match(v ?? "", "[0-3]");
                if (m.Success) { int r; int.TryParse(m.Value, out r); d.Role = r; }
            }
            else if (k == "NAME") d.Name = v;
            else if (k == "MAC") d.Mac = v;
            else if (k == "VERSION") d.Version = v;
            RefreshDeviceList();
        }

        private void StateArrived(string key, string value)
        {
            _state[key] = value;
            Label ov; if (_ov.TryGetValue(key, out ov)) ov.Text = value;
            Label cv; if (_cur.TryGetValue("CUR_" + key, out cv)) cv.Text = value;
            if (key == "ROLE") ApplyRole(value);
        }

        private void ApplyRole(string raw)
        {
            Match m = Regex.Match(raw ?? "", "[0-3]");
            if (!m.Success) return;
            _role = m.Value;
            string desc; RoleNames.TryGetValue(_role, out desc);
            stRole.Text = "角色 " + _role + " · " + (desc ?? "");
            _navBtns[5].Enabled = (_role == "1" || _role == "2") || _roleOverride;
            if (_pageIdx == 5 && !_navBtns[5].Enabled) SwitchPage(0);
        }

        private void OnErrLine(string line)
        {
            Match m = Regex.Match(line, @"\+ERR=(\d+)");
            if (!m.Success) return;
            string desc; ErrNames.TryGetValue(m.Groups[1].Value, out desc);
            stErr.Text = "✘ " + line + (desc == null ? "" : " — " + desc);
            if (m.Groups[1].Value == "4" && !_roleOverride)
                Log("sys", "提示: 当前角色不支持该指令;可在[主机连接]页勾选[强制允许]实测错误码");
        }

        public bool LinesHave(List<string> lines, string needle)
        {
            for (int i = 0; i < lines.Count; i++) if (lines[i].Contains(needle)) return true;
            return false;
        }

        // ================================================================ 查询/发送
        public void SendAndWait(string cmd, string requeryKey)
        {
            if (_se == null) { MessageBox.Show("串口未连接,请先[连接]", "提示"); return; }
            _se.Query(cmd, delegate(List<string> lines, bool okk)
            {
                if (LinesHave(lines, "+OK"))
                {
                    stReply.Text = "✔ " + cmd + " → +OK";
                    stErr.Text = "";
                    if (requeryKey != null) QueryOne(requeryKey);
                }
                else
                {
                    string err = "无应答";
                    for (int i = 0; i < lines.Count; i++) if (lines[i].StartsWith("+ERR")) { err = lines[i]; break; }
                    stReply.Text = "✘ " + cmd + " → " + err;
                }
            }, 3000, 280);
        }

        public void QueryOne(string key) { QueryOne(key, null); }

        public void QueryOne(string key, Action done)
        {
            if (_se == null) { MessageBox.Show("串口未连接,请先[连接]", "提示"); return; }
            _se.Query("AT+" + key + "?", delegate(List<string> lines, bool okk)
            {
                SpecialFill(key);
                if (done != null) done();
            }, 1200, 110);
        }

        // 链式一键读取(单链,等应答再下一条)
        private List<string> _chain = null;
        private int _chainIdx = 0;
        private Action _chainDone = null;
        public void ChainRead(string[] keys, Action done)
        {
            _chain = new List<string>(keys);
            _chainIdx = 0; _chainDone = done;
            stReply.Text = "一键读取中…";
            ChainStep();
        }
        private void ChainStep()
        {
            if (_se == null) return;
            if (_chain == null || _chainIdx >= _chain.Count)
            {
                Action d = _chainDone; _chainDone = null;
                if (d != null) d();
                stReply.Text = "一键读取完成 ✔";
                return;
            }
            string key = _chain[_chainIdx]; _chainIdx++;
            QueryOne(key, delegate { ChainStep(); });
        }

        /// <summary>读取间隔:静默 90ms、超时 900ms —— 模块单条应答通常 &lt;20ms,可显著提速</summary>
        public const int ChainQuiet = 90;
        public const int ChainTimeout = 900;

        // ================================================================ 日志(串口助手式实时显示)
        private readonly object _logLock = new object();
        public void Log(string tag, string text)
        {
            // HEX 模式:rx/tx 行级文本由原始字节管道渲染,行级日志跳过
            if ((tag == "tx" || tag == "rx") && chkHex != null && chkHex.Checked) return;
            EnqueueRender(tag, text);
        }

        private void EnqueueRender(string tag, string text)
        {
            if (_frozen)
            {
                // 冻结显示:数据继续接收与计数,渲染缓存,解冻后补显(串口助手标准行为)
                if (_freezeBuf.Count < FreezeCap) _freezeBuf.Add(new string[] { tag, text });
                else if (_freezeBuf.Count == FreezeCap)
                { _freezeBuf.Add(new string[] { "sys", "…冻结缓存已满(" + FreezeCap + "条),之后的数据未缓存…" }); _freezeBuf.Add(null); }
                return;
            }
            RenderLog(tag, text);
        }

        private static Color TagColor(string tag)
        {
            if (tag == "tx" || tag == "txhex") return Theme.TxCol;
            if (tag == "rx" || tag == "rxhex") return Theme.RxCol;
            if (tag == "err") return Theme.Bad;
            if (tag == "event") return Theme.EvCol;
            return Theme.SysCol;
        }
        private static string TagPrefix(string tag)
        {
            if (tag == "tx") return "TX ";
            if (tag == "rx") return "RX ";
            if (tag == "txhex") return "TX[h] ";
            if (tag == "rxhex") return "RX[h] ";
            if (tag == "event") return "EVT ";
            return "";
        }

        private void RenderLog(string tag, string text)
        {
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string prefix = TagPrefix(tag);
            Color col = TagColor(tag);
            lock (_logLock)
            {
                AppendRtb(rtbLog, time, prefix, text, col, true);
                _logLines++;
                if (_logLines > 3500) { TrimRtb(rtbLog); _logLines /= 2; }
            }
        }

        private void AppendRtb(RichTextBox rtb, string time, string prefix, string text, Color col, bool doScroll)
        {
            if (rtb == null) return;
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionColor = Theme.Dim;
            rtb.AppendText(time + " ");
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionColor = col;
            rtb.AppendText(prefix + text + "\n");
            if (doScroll && chkAutoScroll != null && chkAutoScroll.Checked) rtb.ScrollToCaret();
        }

        private void TrimRtb(RichTextBox rtb)
        {
            int half = rtb.TextLength / 2;
            int cut = rtb.GetLineFromCharIndex(half);
            if (cut > 1)
            {
                rtb.SelectionStart = 0;
                rtb.SelectionLength = rtb.GetFirstCharIndexFromLine(cut);
                rtb.SelectedText = "";
            }
        }

        private void ToggleFreeze()
        {
            if (!_frozen)
            {
                _frozen = true;
                btnFreeze.Text = "▶ 解冻(缓存中…)";
                Log("sys", "显示已冻结(收发与计数继续)");
            }
            else
            {
                int cnt = 0;
                for (int i = 0; i < _freezeBuf.Count; i++)
                {
                    string[] it = _freezeBuf[i];
                    if (it == null) break;
                    RenderLog(it[0], it[1]);
                    cnt++;
                }
                _freezeBuf.Clear();
                btnFreeze.Text = "⏸ 冻结";
                Log("sys", "显示已解冻,补显 " + cnt + " 条");
            }
        }

        // 原始字节管道(HEX 视图)
        public void RawRx(byte[] chunk) { if (chkHex != null && chkHex.Checked) EnqueueRender("rxhex", HexDump(chunk)); }
        public void RawTx(byte[] frame) { if (chkHex != null && chkHex.Checked) EnqueueRender("txhex", HexDump(frame)); }

        private static string HexDump(byte[] data)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('(').Append(data.Length).Append("B) ");
            int n = data.Length;
            int per = 20;
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && (i % per) == 0) sb.Append("\r\n      ");
                if ((i % per) == 0 && i > 0) { }
                sb.Append(data[i].ToString("X2"));
                if ((i % per) != per - 1 && i != n - 1) sb.Append(' ');
            }
            return sb.ToString();
        }

        private void ExportLog()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.FileName = "E104_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                d.Filter = "文本|*.txt";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(d.FileName, rtbLog.Text, Encoding.UTF8);
                    Log("sys", "日志已导出: " + d.FileName);
                }
            }
        }

        // ================================================================ 日志区手动发送(串口助手)
        private void LogSendNow() { LogSend(false); }

        private void LogSend(bool fromRepeat)
        {
            if (_se == null) { MessageBox.Show("串口未连接,请先[连接]", "提示"); return; }
            string s = fromRepeat ? (_logRepeatText ?? "") : txtLogSend.Text.Trim();
            if (s.Length == 0) return;
            if (!fromRepeat)
            {
                _logHist.Insert(0, s);
                if (_logHist.Count > 50) _logHist.RemoveAt(50);
                _logHistIdx = -1;
            }

            bool hex = (cmbLogFmt.SelectedIndex == 1);
            if (hex)
            {
                byte[] d = ParseHexLenient(s);
                if (d == null) { MessageBox.Show("HEX 非法:需要偶数个十六进制字符", "提示"); StopLogRepeat(); return; }
                if (chkLogCrlf.Checked) d = AppendCrlf(d);
                _se.SendRaw(d, "手动HEX");
                if (!fromRepeat) txtLogSend.SelectAll();
                return;
            }
            // ASCII:是否加 CRLF 决定"AT 指令语义"还是"透传原始数据"
            if (chkLogCrlf.Checked)
            {
                if (s.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                    _se.Query(s, null, 4000, 120);     // 走指令通道,可等应答
                else
                    _se.SendRaw(Encoding.ASCII.GetBytes(s + "\r\n"), "手动发送");
            }
            else
            {
                _se.SendRaw(Encoding.ASCII.GetBytes(s), "透传发送");
            }
            if (!fromRepeat) txtLogSend.SelectAll();
        }

        private static byte[] AppendCrlf(byte[] d)
        {
            byte[] r = new byte[d.Length + 2];
            Array.Copy(d, r, d.Length);
            r[d.Length] = 13; r[d.Length + 1] = 10;
            return r;
        }

        private static byte[] ParseHexLenient(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", "");
            if (s.Length == 0 || s.Length % 2 != 0 || !Regex.IsMatch(s, "^[0-9A-Fa-f]+$")) return null;
            byte[] d = new byte[s.Length / 2];
            for (int i = 0; i < d.Length; i++) d[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return d;
        }

        private void ToggleLogRepeat()
        {
            if (_logRepeatTimer != null && _logRepeatTimer.Enabled) { StopLogRepeat(); return; }
            if (_se == null) { MessageBox.Show("串口未连接,请先[连接]", "提示"); return; }
            string s = txtLogSend.Text.Trim();
            if (s.Length == 0) { MessageBox.Show("请先输入要重复发送的内容", "提示"); return; }
            int ms;
            if (!int.TryParse(txtLogRepeatMs.Text.Trim(), out ms) || ms < 100) ms = 1000;
            txtLogRepeatMs.Text = ms.ToString();
            _logRepeatText = s;
            if (_logRepeatTimer == null) { _logRepeatTimer = new System.Windows.Forms.Timer(); _logRepeatTimer.Tick += delegate { LogSend(true); }; }
            _logRepeatTimer.Interval = ms;
            _logRepeatTimer.Start();
            btnLogSendRepeat.Text = "停止重发";
            btnLogSendRepeat.Kind = BtnKind.Danger;
            btnLogSendRepeat.Invalidate();
            Log("sys", "定时重发已启动: 每 " + ms + " ms 发送 \"" + s + "\"");
        }

        private void StopLogRepeat()
        {
            if (_logRepeatTimer != null) _logRepeatTimer.Stop();
            if (btnLogSendRepeat != null)
            {
                btnLogSendRepeat.Text = "定时重发";
                btnLogSendRepeat.Kind = BtnKind.Normal;
                btnLogSendRepeat.Invalidate();
            }
        }

        private void LogHistUp()
        {
            if (_logHist.Count == 0) return;
            _logHistIdx++;
            if (_logHistIdx >= _logHist.Count) _logHistIdx = _logHist.Count - 1;
            txtLogSend.Text = _logHist[_logHistIdx];
            txtLogSend.SelectionStart = txtLogSend.TextLength;
        }
        private void LogHistDown()
        {
            if (_logHist.Count == 0 || _logHistIdx < 0) { txtLogSend.Text = ""; return; }
            _logHistIdx--;
            if (_logHistIdx < 0) txtLogSend.Text = "";
            else { txtLogSend.Text = _logHist[_logHistIdx]; txtLogSend.SelectionStart = txtLogSend.TextLength; }
        }

        // ================================================================ 页面:总览(0)
        private void BuildOverview()
        {
            // ---- 模块状态总览卡片 ----
            Card c1 = MkCard(0, "模块状态总览", "读取全部当前值 · 可导出 JSON 快照 · 串口日志统一显示在下方");
            RowStack rs = new RowStack(c1.Body);
            FlatBtn bExp = new FlatBtn("导出配置快照(JSON)", BtnKind.Normal); bExp.Width = 156;
            rs.Add(bExp, 0, 156); bExp.Click += delegate { ExportConfig(); };
            FlatBtn bRts2 = new FlatBtn("RTS 唤醒脉冲", BtnKind.Normal); bRts2.Width = 116;
            rs.Put(bRts2, 164, 0); bRts2.Click += delegate { RtsPulse(); };
            rs.NewRow(30);
            rs.Hint("数值为模块实时返回;用上方操作条的 [读取本页当前值] / [读取全部] 可随时刷新,时间戳见下方日志。");

            string[] cols = new string[] { "角色", "模块名", "MAC", "固件版本", "发射功率dBm", "广播参数",
                "物理层PHY", "波特率", "连接间隔", "休眠/串口/BLE", "看门狗", "状态输出",
                "广播数据", "扩展广播", "Beacon参数", "服务UUID", "空中密码", "扫描过滤",
                "自动重连", "透传handle", "已连接列表", "观察者", "扫描状态" };
            string[] keys = new string[] { "ROLE", "NAME", "MAC", "VERSION", "PWR", "ADV",
                "PHY", "BAUD", "CONN_INTERVAL", "SLEEP", "WDOG", "LOGMSG",
                "ADVDATA", "ADV_EXT", "BEACON", "SERVICE", "UP_AUTH", "SCANCMP",
                "AUTO_CNT", "TRM_HANDLE", "CONNECT_LIST", "OBSERVER", "SCAN" };
            int perRow = 3, wLab = 100, wVal = 180, gx = 12, gy = rs.Y + 8;
            // 避免总览网格撑出横向滚动条:总宽受限于宿主可用宽度
            int availW = Math.Max(720, PageStack(0).Controls[0].ClientSize.Width - 36);
            int colW = availW / perRow;
            for (int i = 0; i < keys.Length; i++)
            {
                int r = i / perRow, cc = i % perRow;
                Label lk = new Label();
                lk.Text = cols[i]; lk.ForeColor = Theme.Dim; lk.Font = Theme.FSub;
                lk.AutoSize = false; lk.Width = wLab; lk.Height = 17; lk.AutoEllipsis = true;
                lk.Location = new Point(gx + cc * colW, gy + r * 24);
                c1.Body.Controls.Add(lk);
                Label lv = new Label();
                lv.ForeColor = Theme.AccentHi; lv.Font = Theme.F;
                lv.AutoSize = false; lv.Width = Math.Max(60, colW - wLab - 10); lv.Height = 17; lv.AutoEllipsis = true;
                lv.Text = "-";
                lv.Location = new Point(gx + cc * colW + wLab + 6, gy + r * 24);
                c1.Body.Controls.Add(lv);
                _ov[keys[i]] = lv;
            }
            int rows = (keys.Length + perRow - 1) / perRow;
            c1.Height = gy + rows * 24 + 16;
        }

        public void ReadAll()
        {
            if (_se == null) { MessageBox.Show("请先连接串口", "提示"); return; }
            List<string> keys = new List<string>(new string[] {
                "ROLE","NAME","MAC","VERSION","PWR","ADV","PHY","BAUD","CONN_INTERVAL",
                "SLEEP","WDOG","LOGMSG","UP_AUTH","SCANCMP","ADVDATA","ADV_EXT","LE_CODED","BEACON","SERVICE" });
            if (_role == "1" || _role == "2")
                keys.AddRange(new string[] { "SCAN","SCAN_PHY","SCANCMP","CONNECT_LIST","TRM_HANDLE","AUTO_CNT","OBSERVER" });
            ChainRead(keys.ToArray(), null);
        }

        private void ExportConfig()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.FileName = "E104-BT53_config_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
                d.Filter = "JSON|*.json";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                int i = 0;
                foreach (KeyValuePair<string, string> kv in _state)
                {
                    i++;
                    string v = (kv.Value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                    sb.AppendLine("  \"" + kv.Key + "\": \"" + v + "\"" + (i < _state.Count ? "," : ""));
                }
                sb.AppendLine("}");
                File.WriteAllText(d.FileName, sb.ToString(), Encoding.UTF8);
                Log("sys", "配置快照已导出: " + d.FileName);
            }
        }

        // ================================================================ 页面:基础信息(1)
        private void BuildBasic()
        {
            Card c = MkCard(1, "调试与身份", "AT(6.3.1) · NAME(6.3.2) · MAC(6.3.3) · ROLE(6.3.4) · VERSION(6.3.32) · RESET(6.3.30) · RESTORE(6.3.31)");
            RowStack rs = new RowStack(c.Body);

            TextBox tName = new TextBox(); Theme.StyleText(tName);
            ParamRow(rs, "NAME", "广播名称(≤16B)", tName, 150, true,
                "立即生效,掉电保存;出厂默认 CDEBYTE_XXXX(实测当前模块 CDEBYTE_3316)");

            TextBox tMac = new TextBox(); Theme.StyleText(tMac); tMac.ReadOnly = true;
            ParamRow(rs, "MAC", "MAC 地址(只读)", tMac, 150, false, "仅可查询");

            TextBox tVer = new TextBox(); Theme.StyleText(tVer); tVer.ReadOnly = true;
            ParamRow(rs, "VERSION", "固件版本(只读)", tVer, 150, false,
                "实测应答 +VERSION=7413-1-16(手册示例 +OK= 前缀不符)");

            cmbRoleSwitch = new DarkCombo(); cmbRoleSwitch.Width = 150;
            foreach (KeyValuePair<string, string> kv in RoleNames) cmbRoleSwitch.Items.Add(kv.Key + " - " + kv.Value);
            cmbRoleSwitch.SelectedIndex = 0;
            ParamRow(rs, "ROLE", "模块角色", cmbRoleSwitch, 150, false,
                "0从/1主/2主从/3Beacon;切换后须[模块复位]生效,且自动重连列表清空;Beacon 上电串口关闭需 CTS 唤醒");
            FlatBtn bs = new FlatBtn("切换角色", BtnKind.Accent); bs.Width = 78;
            int bxRole = 154 + 150 + 12 + 34 + 120 + 68 + 68;
            rs.Put(bs, bxRole, 0);
            bs.Click += delegate
            {
                string v = cmbRoleSwitch.Text;
                if (v.Length < 1) return;
                SendAndWait("AT+ROLE=" + v.Substring(0, 1), null);
                Log("sys", "角色已保存 → 请[模块复位]后生效");
            };
            rs.NewRow(30);

            Label dz = new Label(); dz.Text = "危险区(需二次确认)"; dz.ForeColor = Theme.Bad; dz.Font = Theme.FHead;
            rs.Add(dz, 14, 180); rs.NewRow(30);
            FlatBtn bReset = new FlatBtn("⟳ 模块复位 AT+RESET", BtnKind.Danger); bReset.Width = 178;
            rs.Add(bReset, 14, 178);
            bReset.Click += delegate
            {
                if (MessageBox.Show("发送 AT+RESET 软复位模块?(约1秒重启)", "确认",
                    MessageBoxButtons.OKCancel) == DialogResult.OK) SendAndWait("AT+RESET", null);
            };
            FlatBtn bRestore = new FlatBtn("☠ 恢复出厂 AT+RESTORE", BtnKind.Danger); bRestore.Width = 208;
            rs.Put(bRestore, 210, 0);
            bRestore.Click += delegate
            {
                if (MessageBox.Show("将清除全部自定义配置(名称/功率/Service/认证码等)并复位!\n继续?",
                    "⚠ 恢复出厂", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                if (MessageBox.Show("确定清空全部配置?过程中严禁断电/复位。", "再次确认",
                    MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                SendAndWait("AT+RESTORE", null);
            };
            rs.NewRow(40);
            CardEnd(c, rs);
        }

        // ================================================================ 页面:射频(2)
        private void BuildRf()
        {
            Card c = MkCard(2, "发射功率 PWR(6.3.5)", "取值 -28/-20/-10/-5/-3/0/1/2/4/6 dBm");
            RowStack rs = new RowStack(c.Body);
            DarkCombo cPwr = new DarkCombo(); cPwr.Width = 110;
            foreach (string s in new string[] { "-28", "-20", "-10", "-5", "-3", "0", "1", "2", "4", "6" }) cPwr.Items.Add(s);
            cPwr.SelectedIndex = 5;
            ParamRow(rs, "PWR", "发射功率", cPwr, 110, true,
                "立即生效,掉电保存;扫描/观察者开启或连接时设置会返回错误码3;A1 版本上限 0dBm,模块实测 0");
            CardEnd(c, rs);

            Card c2 = MkCard(2, "从机物理层速率 PHY(6.3.22,位域)", "可多选,参数=勾选值求和");
            RowStack rs2 = new RowStack(c2.Body);
            phyChecks = new CheckBox[4];
            string[] names = new string[] { "1M PHY(默认)", "2M PHY", "125K Coded", "500K Coded" };
            int[] bits = new int[] { 1, 2, 4, 8 };

            // 选项 2x2 放进块容器,块本身占网格前两列并跨两行
            // 关键:块与子项宽度都按"单元格实际宽度"自适应,窄窗口时等比收缩,绝不被裁切
            Panel optBlock = new Panel();
            optBlock.BackColor = Color.Transparent;
            optBlock.Width = 360;
            optBlock.Height = 56;
            DarkCheck[] blockCks = new DarkCheck[4];
            for (int i = 0; i < 4; i++)
            {
                DarkCheck ck = new DarkCheck();
                ck.Text = names[i] + " (" + bits[i] + ")";
                FitDark(ck);
                DarkTips.Set(ck, Tips["PHY"]);
                ck.Height = 24;
                optBlock.Controls.Add(ck);
                blockCks[i] = ck;
                phyChecks[i] = ck;
            }
            // 重新排布块内 2x2:每格宽 = 块宽/2,文字过长由自绘 EndEllipsis 收尾
            EventHandler layBlock = delegate
            {
                int half = Math.Max(90, optBlock.ClientSize.Width / 2);
                for (int i = 0; i < 4; i++)
                {
                    blockCks[i].Left = (i % 2) * half;
                    blockCks[i].Top = (i / 2) * 28;
                    blockCks[i].Width = Math.Max(80, half - 4);
                }
            };
            optBlock.Resize += delegate { layBlock(null, null); };
            layBlock(null, null);

            phySumLbl = new Label();
            phySumLbl.ForeColor = Theme.AccentHi; phySumLbl.AutoSize = true; phySumLbl.Text = "参数 = 1";

            FlatBtn bq = new FlatBtn("查询", BtnKind.Normal); bq.Width = 80; bq.Height = 26;
            bq.Anchor = AnchorStyles.Right;
            bq.Click += delegate { QueryOne("PHY"); };
            FlatBtn bs2 = new FlatBtn("下发", BtnKind.Accent); bs2.Width = 80; bs2.Height = 26;
            bs2.Anchor = AnchorStyles.Right;
            bs2.Click += delegate
            {
                int s2 = 0;
                for (int j = 0; j < 4; j++) if (phyChecks[j].Checked) s2 += bits[j];
                if (s2 == 0) s2 = 1;
                SendAndWait("AT+PHY=" + s2, "PHY");
            };

            // 复用站内同一套网格列:选项块跨 2 列 2 行,按钮落在标准第 4/5 列
            rs2.AddGridRowSpan(optBlock, phySumLbl, bq, bs2);

            for (int i = 0; i < 4; i++)
            {
                CheckBox ck = phyChecks[i];
                ck.CheckedChanged += delegate
                {
                    int s = 0;
                    for (int j = 0; j < 4; j++) if (phyChecks[j].Checked) s += bits[j];
                    phySumLbl.Text = "参数 = " + s;
                };
            }
            phyChecks[0].Checked = true;
            CardEnd(c2, rs2);

            Card c3 = MkCard(2, "远距离广播与主角色扫描物理层", "LE_CODED(6.3.8) · SCAN_PHY(6.3.13)");
            RowStack rs3 = new RowStack(c3.Body);
            DarkCombo cLc = new DarkCombo(); cLc.Width = 110;
            cLc.Items.Add("0"); cLc.Items.Add("1"); cLc.SelectedIndex = 0;
            ParamRow(rs3, "LE_CODED", "远距离广播", cLc, 110, true,
                "0关/1开;仅从角色(含主从)支持,单主/Beacon/A1 返回 +ERR=4;开启后带广播名,可被 LE CODED 主机扫连");
            DarkCombo cSp = new DarkCombo(); cSp.Width = 110;
            cSp.Items.Add("0"); cSp.Items.Add("1"); cSp.SelectedIndex = 0;
            ParamRow(rs3, "SCAN_PHY", "主角色扫描物理层", cSp, 110, true,
                "0=1M(默认) 1=LE CODED;设1后只能扫到/连接 LE CODED 设备;观察者模式2需先设此项;A1 不支持");
            CardEnd(c3, rs3);
        }

        // ================================================================ 特殊回填(读取 _state 后分发)
        private void SpecialFill(string key)
        {
            if (key == "PHY") FillPhy();
            else if (key == "ADV") FillAdv();
            else if (key == "BEACON") FillBeacon();
            else if (key == "SERVICE") FillService();
            else if (key == "AUTO_CNT") FillAutoCnt();
            else if (key == "SLEEP") FillSleep();
            else if (key == "CONN_INTERVAL") FillConnIv();
        }

        private static string Str(Dictionary<string, string> d, string k)
        { string v; d.TryGetValue(k, out v); return v ?? ""; }

        // ================================================================ 错误码参考
        private void ShowErrRef()
        {
            Form f = new Form();
            f.Text = "错误码与实测参考";
            f.Size = new Size(680, 540);
            f.StartPosition = FormStartPosition.CenterParent;
            f.BackColor = Theme.Bg;
            f.Font = Theme.F;
            TextBox tb = new TextBox();
            tb.Multiline = true; tb.ReadOnly = true; tb.ScrollBars = ScrollBars.Vertical;
            tb.Dock = DockStyle.Fill; tb.Font = Theme.F;
            tb.BackColor = Theme.Bg; tb.ForeColor = Theme.TxT; tb.BorderStyle = BorderStyle.None;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("【+ERR 错误码】(手册 6.2)");
            foreach (KeyValuePair<string, string> kv in ErrNames) sb.AppendLine("  +ERR=" + kv.Key + "\t" + kv.Value);
            sb.AppendLine();
            sb.AppendLine("【模块状态事件】(手册 5.5)");
            sb.AppendLine("  MODULE POWERUP            模块启动");
            sb.AppendLine("  XX:..:XX CONNECTD P*X     从角色连接成功(handle=P编号)");
            sb.AppendLine("  XX:..:XX DISCONNECTED P  从角色断开");
            sb.AppendLine("  XX:..:XX CONNECTD C*Y    主角色连接成功(handle=Y)");
            sb.AppendLine("  XX:..:XX DISCONNECTD C    主角色断开");
            sb.AppendLine("  ALREADY CONNECTED         已连接此设备");
            sb.AppendLine("  XX:..:XX CONNECT TIMEOUT  连接从设备超时(10s)");
            sb.AppendLine("  RECEIVE TIMEOUT           交互式数据输入超时");
            sb.AppendLine();
            sb.AppendLine("【本机实测提示(7413-1-16 固件)】");
            sb.AppendLine("  · AT+VERSION? 实际回 +VERSION=7413-1-16(手册示例 +OK= 前缀不符)");
            sb.AppendLine("  · AT+TRM_HANDLE? 无连接返回 255(即无透传句柄)");
            sb.AppendLine("  · AT+DEV_DEL 删除不存在的 MAC 实测 +ERR=2(手册写错误码 3)");
            sb.AppendLine("  · 手册 6.3.14 示例 MAC 仅 5 字节会被判 +ERR=2;须 6 字节");
            sb.AppendLine("  · AT+AUTH 仅手机APP通道有效,串口发送回 +ERR=4 属正常");
            sb.AppendLine("  · 毫秒级连发两帧会粘连(实测 AT+ROLE?×2 → +ERR=1);本工具已做等待应答+20ms 帧间隔");
            sb.AppendLine("  · A1 版本不支持 LE_CODED / SCAN_PHY / OTA,最大发射功率 0dBm");
            tb.Text = sb.ToString();
            f.Controls.Add(tb);
            f.ShowDialog(this);
        }
    }
}
