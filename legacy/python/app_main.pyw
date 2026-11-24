# -*- coding: utf-8 -*-
"""
E104-BT53 蓝牙模块调试助手 (上位机)
├ 三栏布局: 设备状态 | 功能选项卡 | 串口日志
├ 8 选项卡覆盖《E104-BT53 产品规格书 v1.6》第六章全部 33 组 AT 指令
├ 角色感知置灰 / 一键读取全部当前值 / 交互式指令分步引导 / 危险操作二次确认
依赖: 仅 Python 标准库 + serial_engine.py (ctypes 串口)
"""
import sys, os, re, json, time, queue, threading
from datetime import datetime
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from serial_engine import SerialPort, list_serial_ports

# ============================== 常量 ==============================
BAUDS = ["1200", "2400", "4800", "9600", "14400", "19200", "38400",
         "57600", "115200", "230400", "460800", "921600"]
ROLE_NAMES = {"0": "从角色(单从)", "1": "主角色(单主)", "2": "主从一体", "3": "Beacon"}
ERR_CODES = {
    "1": "指令不存在(拼写错误)",
    "2": "参数错误",
    "3": "操作不允许 / 操作失败",
    "4": "角色不支持该指令",
}
PUMP_MS = 25            # 串口泵间隔
AVG_QUIET = 300         # 应答静默判定(ms)
AVG_TIMEOUT = 3000       # 应答超时(ms)

# 实测补充提示(基于 7413-1-16 固件真机全指令验证)
HW_TIPS = {
    "VERSION": "实测应答为 +VERSION=7413-1-16(手册示例 +OK= 前缀与实际不符)",
    "TRM_HANDLE": "实测无连接时返回 255(即无透传句柄)",
    "DEV_DEL": "实测删除列表中不存在的 MAC 返回 +ERR=2(手册写错误码 3)",
    "CONNECT": "注意:手册示例 MAC 只 5 字节会被判 +ERR=2,需 6 字节;连接超时 10s",
    "AUTH": "该指令仅手机 APP 配置通道有效;串口发送预期返回 +ERR=4",
    "ROLE": "切换角色后需 AT+RESET 重启生效,并清空自动重连列表",
    "BAUD": "修改后需 AT+RESET 重启生效",
    "SERVICE": "修改后需重启生效;0000xxxx-0000-1000-8000-00805F9B34FB 基础UUID不可用",
    "SLEEP": "关闭串口后需 CTS 上升沿唤醒(顶部 [RTS 唤醒脉冲] 按钮)",
    "BEACON_ROLE": "Beacon 角色每次上电串口关闭,需先按 [RTS 唤醒脉冲]",
    "A1_NOTES": "A1 版本不支持 LE_CODED / SCAN_PHY / OTA,最大发射功率 0dBm",
}

REPLY_KV = re.compile(r"^\+([A-Za-z_0-9]+)=(.*)$")
RE_SCAN_ROW = re.compile(r"^(\d+)\s+([0-9A-F:]{17})\s+(-?\d+)[ \t]*(.*)$")
RE_EVENT = ("MODULE POWERUP", "CONNECT TIMEOUT", "ALREADY CONNECTED")
RE_EVENT_CONTAINS = ("CONNECTD", "CONNECTED", "DISCONNECTED", "DISCONNECT",
                     "RECEIVE TIMEOUT", "MODULE POWERUP", "CONNECT TIMEOUT")


# ============================== 会话层 ==============================
class Awaiter:
    __slots__ = ("cmd", "cb", "deadline", "quiet", "lines", "last_rx", "tag")
    def __init__(self, cmd, cb, timeout, quiet, tag=""):
        self.cmd, self.cb, self.quiet, self.tag = cmd, cb, quiet, tag
        self.deadline = time.time() + timeout / 1000.0
        self.lines, self.last_rx = [], 0.0


class Session:
    """协议会话:读泵 + 行组装 + 应答等待 + 流式路由 + 交互数据监视"""

    def __init__(self, app, port):
        self.app = app
        self.port = port
        self.linebuf = bytearray()
        self.awaiters = []          # list[Awaiter]
        self.scan_active = False
        self.observer_active = False
        self.pending_input = None  # {'kind','needed','target'}交互式待输入数据
        self.rx_count = self.tx_count = 0
        self.last_cmd_t = 0.0
        self._pump_handle = app.root.after(PUMP_MS, self.pump)

    # ---------- 基础 ----------
    def send(self, cmd: str, log=True):
        # 防粘连:手册 5.7 要求等待应答再发下一条;另强制最小 20ms 帧间隔
        gap = time.time() - self.last_cmd_t
        if gap < 0.02:
            time.sleep(0.02 - gap)
        self.last_cmd_t = time.time()
        data = cmd.encode("ascii") + b"\r\n"
        self.port.write(data)
        self.tx_count += 1
        if log:
            self.app.log_tx(cmd.strip() + "\\r\\n")

    def send_bytes(self, data: bytes, note="raw"):
        self.port.write(data)
        self.tx_count += 1
        self.app.log_tx(f"[{note}] {len(data)}字节: " +
                        (data.decode('ascii', 'replace') if all(0x20 <= c < 0x7f for c in data) else data.hex(' ')))

    def query(self, cmd, cb=None, timeout=AVG_TIMEOUT, quiet=AVG_QUIET):
        aw = Awaiter(cmd, cb, timeout, quiet)
        self.awaiters.append(aw)
        self.send(cmd)
        return aw

    # ---------- 泵 ----------
    def pump(self):
        try:
            # 串口级错误(拔出等)
            # (engine 回调在从主 app 侧注册, 拔出时 is_open=False)
            drained = bytearray()
            while True:
                try:
                    drained += self.port.rx_queue.get_nowait()
                except queue.Empty:
                    break
            now = time.time()
            if drained:
                self.rx_count += len(drained)
                self.app.log_rx_bytes(drained)
                self.linebuf += drained
                for aw in self.awaiters:
                    aw.last_rx = now
            # 行拆分
            while b"\n" in self.linebuf:
                raw, _, rest = self.linebuf.partition(b"\n")
                self.linebuf = bytearray(rest)
                line = raw.decode("utf-8", "replace").strip("\r").strip()
                if line:
                    self.dispatch_line(line)
            # 静默/超时结算
            for aw in list(self.awaiters):
                got = bool(aw.lines)
                idle = (now - aw.last_rx) * 1000 >= aw.quiet
                if got and idle and aw.last_rx > 0:
                    self.resolve(aw, True)
                elif now > aw.deadline:
                    self.resolve(aw, got)
        except Exception as e:
            self.app.log("sys", f"泵异常: {e}")
        self._pump_handle = self.app.root.after(PUMP_MS, self.pump)

    def resolve(self, aw, ok):
        if aw in self.awaiters:
            self.awaiters.remove(aw)
        try:
            if aw.cb:
                self.app.root.after(0, lambda: aw.cb(aw.lines, ok))
        except Exception:
            pass

    def dispatch_line(self, line):
        self.app.maybe_show_reply(line)
        # 1) 交互式输入提示
        m = re.match(r"^INPUT (?:EXT ADV|BLE DATA):(\d+)$", line)
        if m:
            self.pending_input = {"needed": int(m.group(1))}
        # 2) BLE 数据到达(AT 指令模式下)
        if line.startswith("+RECEIVED:"):
            self.app.got_received(line)
        # 3) 事件横幅
        if line.startswith("+ERR="):
            self.app.log("err", line)
            self.app.show_errcode(line)
        elif any(k in line for k in RE_EVENT_CONTAINS) and not line.startswith("+"):
            self.app.log("event", line)
            self.app.on_link_event(line)
        else:
            if not self.app.var_hex.get():
                self.app.log("rx", line)
        # 4) 流式路由
        if self.scan_active:
            mm = RE_SCAN_ROW.match(line)
            if mm:
                self.app.scan_add_row(mm.groups())
        if self.observer_active and line.startswith("+MAC:"):
            self.app.observer_add(line)
        # 5) 写给等待器
        for aw in self.awaiters:
            aw.lines.append(line)
            if not aw.last_rx:
                aw.last_rx = time.time()
        # 键值回填左侧状态面板
        if not line.startswith("+ERR="):
            kv = REPLY_KV.match(line)
            if kv:
                self.app.state_set(kv.group(1).upper(), kv.group(2).strip())

        # 6) 回显到终端/接收区
    def stop(self):
        try:
            self.app.root.after_cancel(self._pump_handle)
        except Exception:
            pass


# ============================== 可滚动容器 ==============================
class ScrollFrame(ttk.Frame):
    def __init__(self, parent, **kw):
        super().__init__(parent, **kw)
        self.canvas = tk.Canvas(self, bd=0, highlightthickness=0)
        vsb = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.inner = ttk.Frame(self.canvas)
        win = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.inner.bind("<Configure>",
                        lambda e: (self.canvas.configure(scrollregion=self.canvas.bbox("all")),
                                   self.canvas.itemconfigure(win, width=self.canvas.winfo_width())))
        self.canvas.configure(yscrollcommand=vsb.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        # 鼠标滚轮
        for w in (self, self.canvas):
            w.bind("<Enter>", lambda e: self._bind_wheel(True))
            w.bind("<Leave>", lambda e: self._bind_wheel(False))

    def _bind_wheel(self, on):
        self.canvas.bind_all("<MouseWheel>",
                             lambda e: self.canvas.yview_scroll(int(-1 * (e.delta / 120)), "units")
                             if on and self.canvas.winfo_containing(e.x_root, e.y_root) else None)


# ============================== 主应用 ==============================
class App:
    def __init__(self, root):
        self.root = root
        root.title("E104-BT53 蓝牙模块调试助手(规格书 v1.6 · 33 组 AT 指令)")
        root.geometry("1380x840")
        root.minsize(1150, 700)
        self.session = None
        self.gated = []            # (widget, predicate) 角色门控注册
        self.chain_queue = []      # 一键读取链
        self.role_override = tk.BooleanVar(value=False)
        self.state_vars = {}

        self._build_style()
        self._build_topbar()
        self._build_body()
        self._build_statusbar()
        self.refresh_ports()

    # ------------------------------------------------ 风格
    def _build_style(self):
        st = ttk.Style(self.root)
        try:
            st.theme_use("vista")
        except Exception:
            pass
        base = ("Microsoft YaHei UI", 9)
        self.root.option_add("*Font", base)
        st.configure("TButton", padding=(6, 2))
        st.configure("Danger.TButton", foreground="#b02020")
        st.configure("Hdr.TLabel", font=("Microsoft YaHei UI", 10, "bold"))
        st.configure("Cap.TLabel", foreground="#3a6ea5", font=("Microsoft YaHei UI", 9, "bold"))

    # ------------------------------------------------ 顶栏
    def _build_topbar(self):
        bar = ttk.Frame(self.root, padding=(8, 5))
        bar.pack(side="top", fill="x")
        ttk.Label(bar, text="E104-BT53 调试助手", style="Hdr.TLabel").pack(side="left", padx=(0, 15))
        ttk.Label(bar, text="串口").pack(side="left")
        self.cb_port = ttk.Combobox(bar, width=8, state="readonly")
        self.cb_port.pack(side="left", padx=(3, 2))
        ttk.Button(bar, text="刷新", width=5, command=self.refresh_ports).pack(side="left", padx=2)
        ttk.Label(bar, text="波特率").pack(side="left", padx=(8, 0))
        self.cb_baud = ttk.Combobox(bar, width=8, state="readonly", values=BAUDS)
        self.cb_baud.set("115200")
        self.cb_baud.pack(side="left", padx=(3, 8))
        self.bt_conn = ttk.Button(bar, text="🔌 连接", command=self.connect)
        self.bt_conn.pack(side="left", padx=2)
        self.bt_disc = ttk.Button(bar, text="⏏ 断开", command=self.disconnect, state="disabled")
        self.bt_disc.pack(side="left", padx=2)
        ttk.Button(bar, text="⚡ RTS唤醒脉冲",
                   command=self.rts_pulse).pack(side="left", padx=8)
        self.var_link = tk.StringVar(value="○ 未连接")
        ttk.Label(bar, textvariable=self.var_link, foreground="#999").pack(side="left", padx=10)

    def refresh_ports(self):
        ports = list_serial_ports()
        self.cb_port["values"] = ports
        if ports and not self.cb_port.get():
            self.cb_port.set(ports[0])
        self.log("sys", f"检测到串口: {ports if ports else '无'}")

    def connect(self):
        name = self.cb_port.get()
        if not name:
            messagebox.showwarning("提示", "未选择串口")
            return
        try:
            baud = int(self.cb_baud.get())
            port = SerialPort(name, baud, on_error=self._port_err)
            self.session = Session(self, port)
            self.bt_conn.state(["disabled"])
            self.bt_disc.state(["!disabled"])
            self.var_link.set(f"● {name} 已连接 @{baud}")
            self.var_link_master = None
            self.log("sys", f"已连接 {name} @ {baud},8-N-1")
            self.after_connect()
        except Exception as e:
            messagebox.showerror("连接失败", str(e))
            self.log("sys", f"连接失败: {e}")

    def _port_err(self, msg):
        try:
            self.root.after(0, lambda: (self.log("err", f"串口异常: {msg}"), self.disconnect()))
        except Exception:
            pass

    def disconnect(self):
        if self.session:
            self.session.stop()
            try:
                self.session.port.close()
            except Exception:
                pass
        self.session = None
        self.bt_conn.state(["!disabled"])
        self.bt_disc.state(["!disabled"])
        self.var_link.set("○ 未连接")
        self.log("sys", "已断开")

    def rts_pulse(self):
        if not self.session:
            messagebox.showinfo("提示", "请先连接串口")
            return
        th = threading.Thread(target=self._rts_do, daemon=True)
        th.start()

    def _rts_do(self):
        try:
            self.session.port.rts_pulse()
            self.root.after(0, lambda: self.log("sys", "RTS 唤醒脉冲已发出(CTS 上升沿)"))
        except Exception as e:
            self.root.after(0, lambda: self.log("err", f"RTS 脉冲失败: {e}"))

    def after_connect(self):
        """连接后:握手 + 链式刷新(ROLE 首位 → 自动触发角色门控)"""
        def _boot(lines, ok):
            if any("+OK" in l for l in lines):
                self.log("sys", "AT 握手完成 ✔")
            else:
                messagebox.showwarning("提示", "无应答:模块可能休眠/Beacon 角色,试试 [RTS唤醒脉冲] 并检查波特率")
            self.start_chain_read(full=False)

        self.session.query("AT", _boot, timeout=1500)

    # ------------------------------------------------ 主体三栏
    def _build_body(self):
        pw = ttk.PanedWindow(self.root, orient="horizontal")
        pw.pack(side="top", fill="both", expand=True)
        # 左栏
        left = ttk.Frame(pw, padding=(6, 6))
        pw.add(left, weight=0)
        self._build_left(left)
        # 中栏
        center = ttk.Frame(pw)
        pw.add(center, weight=1)
        self.nb = ttk.Notebook(center)
        self.nb.pack(fill="both", expand=True)
        self._build_tabs()
        # 右栏
        right = ttk.Frame(pw, padding=(6, 6))
        pw.add(right, weight=0)
        right.configure(width=400)
        self._build_right(right)
        pw.sashpos(0, 250)
        pw.sashpos(1, 950)

    # ------------------------------------------------ 左栏:设备状态
    def _build_left(self, parent):
        ttk.Label(parent, text="设备状态", style="Hdr.TLabel").pack(anchor="w", pady=(0, 4))
        frm = ttk.LabelFrame(parent, padding=(6, 6))
        frm.pack(fill="both", expand=True)
        keys = [
            ("角色 ROLE", "ROLE"), ("模块名 NAME", "NAME"), ("MAC 地址", "MAC"),
            ("固件版本", "VERSION"), ("发射功率 dBm", "PWR"),
            ("广播参数", "ADV"), ("物理层 PHY", "PHY"), ("波特率", "BAUD"),
            ("连接间隔(参数)", "CONN_INTERVAL"), ("休眠/串口/BLE", "SLEEP"),
            ("看门狗", "WDOG"), ("状态输出", "LOGMSG"),
            ("广播数据", "ADVDATA"), ("扩展广播", "ADV_EXT"),
            ("Beacon参数", "BEACON"), ("服务UUID", "SERVICE"),
            ("空中认证密码", "UP_AUTH"), ("扫描过滤", "SCANCMP"),
            ("自动重连", "AUTO_CNT"), ("透传handle", "TRM_HANDLE"),
            ("已连接列表", "CONNECT_LIST"), ("观察者", "OBSERVER"),
            ("扫描状态", "SCAN"),
        ]
        for text, key in keys:
            v = tk.StringVar(value="-")
            self.state_vars[key] = v
            row = ttk.Frame(frm)
            row.pack(fill="x", pady=1)
            ttk.Label(row, text=text, width=12).pack(side="left")
            ttk.Label(row, textvariable=v, foreground="#2050a0").pack(side="left", fill="x", expand=True)
        btns = ttk.Frame(frm)
        btns.pack(fill="x", pady=(8, 0))
        ttk.Button(btns, text="🔄 一键读取全部", command=self.read_all).pack(side="left")
        ttk.Button(btns, text="💾 导出配置", command=self.export_cfg).pack(side="left", padx=4)
        ttk.Label(frm, text="Role 角色说明:0从/1主/2主从/3Beacon", foreground="#888").pack(anchor="w", pady=(6, 0))

    def state_set(self, key, value):
        v = self.state_vars.get(key)
        if v is None:
            return
        if key == "ROLE":
            rnum = re.match(r"[0-3]", value)
            value = f"{value} {ROLE_NAMES.get(rnum.group(0), '')}".strip()
        v.set(value)
        if key == "ROLE":
            m = re.match(r"[0-3]", value)
            if m:
                self.set_role(m.group(0))

    def query(self, cmd, cb=None, timeout=AVG_TIMEOUT, quiet=AVG_QUIET):
        """App 级查询代理:串口未连接统一提示"""
        if not self.session:
            messagebox.showinfo("提示", "串口未连接,请先在顶部[连接]")
            return None
        return self.session.query(cmd, cb, timeout, quiet)

    # ------------------------------------------------ 右栏:日志
    def _build_right(self, parent):
        ttk.Label(parent, text="串口日志", style="Hdr.TLabel").pack(anchor="w")
        bar = ttk.Frame(parent)
        bar.pack(fill="x", pady=2)
        self.var_autoscroll = tk.BooleanVar(value=True)
        self.var_hex = tk.BooleanVar(value=False)
        ttk.Checkbutton(bar, text="自动滚动", variable=self.var_autoscroll).pack(side="left")
        ttk.Checkbutton(bar, text="HEX显示", variable=self.var_hex).pack(side="left", padx=4)
        ttk.Button(bar, text="清空", width=5, command=lambda: self.txt_log.delete("1.0", "end")).pack(side="left", padx=2)
        ttk.Button(bar, text="导出", width=5, command=self.export_log).pack(side="left")
        self.txt_log = tk.Text(parent, height=10, wrap="none", state="disabled",
                               font=("Consolas", 9))
        for tag, cfg in (("tx", {"foreground": "#1040a0"}), ("rx", {"foreground": "#0a7020"}),
                         ("err", {"foreground": "#c01010"}), ("event", {"foreground": "#b06000"}),
                         ("sys", {"foreground": "#888888"})):
            self.txt_log.tag_configure(tag, **cfg)
        ys = ttk.Scrollbar(parent, orient="vertical", command=self.txt_log.yview)
        xs = ttk.Scrollbar(parent, orient="horizontal", command=self.txt_log.xview)
        self.txt_log.configure(yscrollcommand=ys.set, xscrollcommand=xs.set)
        self.txt_log.pack(fill="both", expand=True)
        ys.pack(side="right", fill="y")
        xs.pack(side="bottom", fill="x")
        # 底部小工具:最近应答
        self.var_lastreply = tk.StringVar(value="-")
        lab = ttk.Label(parent, text="最近应答:", foreground="#888")
        lab.pack(anchor="w", pady=(4, 0))
        ttk.Label(parent, textvariable=self.var_lastreply, foreground="#0a7020", wraplength=360,
                  font=("Consolas", 9)).pack(anchor="w")

    # ------------------------------------------------ 状态栏
    def _build_statusbar(self):
        bar = ttk.Frame(self.root, padding=(8, 2))
        bar.pack(side="bottom", fill="x", pady=(0, 2))
        self.var_stat = tk.StringVar(value="就绪。先选择串口并[连接]")
        ttk.Label(bar, textvariable=self.var_stat, foreground="#555").pack(side="left")
        self.var_txrx = tk.StringVar(value="TX:0 RX:0")
        ttk.Label(bar, textvariable=self.var_txrx).pack(side="right")
        ttk.Button(bar, text="📖 错误码参考", command=self.show_err_ref).pack(side="right", padx=6)
        # 顶部进入 app 内:周期刷新计数
        self._tick()

    def _tick(self):
        if self.session:
            self.var_txrx.set(f"TX:{self.session.tx_count} RX:{self.session.rx_count}")
        self.root.after(500, self._tick)

    # ================================================== 选项卡
    def _build_tabs(self):
        self.tab_builders = {
            "基础信息": self.tab_basic, "射频物理层": self.tab_rf,
            "广播配置": self.tab_adv, "从机服务": self.tab_service,
            "主角色连接": self.tab_master, "串口功耗": self.tab_uart,
            "认证": self.tab_auth, "AT 终端": self.tab_terminal,
        }
        for name, builder in self.tab_builders.items():
            sf = ScrollFrame(self.nb)
            self.nb.add(sf, text=name)
            builder(sf.inner)

    # ---------- 通用行件 ----------
    def row_head(self, parent, text, tip=None):
        ttk.Label(parent, text=text, style="Cap.TLabel").grid(row=parent.grid_size()[1],
                                                              column=0, columnspan=6, sticky="w", pady=(10, 2))
        if tip:
            ttk.Label(parent, text="ℹ " + tip, foreground="#997700").grid(row=parent.grid_size()[1],
                                                                         column=0, columnspan=6, sticky="w")

    def param_row(self, parent, key, label, kind="entry", values=None,
                  setcmd=None, hint="", width=14, confirm=None):
        """一行参数控件: 名称 [输入] 当前: - [查询][下发]"""
        r = parent.grid_size()[1]
        ttk.Label(parent, text=label, width=16).grid(row=r, column=0, sticky="w")
        var = tk.StringVar()
        cur = tk.StringVar(value="-")
        if kind == "combo":
            w = ttk.Combobox(parent, textvariable=var, values=values, width=width, state="readonly")
            if values:
                var.set(values[0])
        elif kind in ("ro", "roq"):
            w = ttk.Label(parent, textvariable=var)
        else:
            w = ttk.Entry(parent, textvariable=var, width=width)
        w.grid(row=r, column=1, sticky="w")
        ttk.Label(parent, text="当前:", foreground="#888").grid(row=r, column=2, sticky="e")
        lab = ttk.Label(parent, textvariable=cur, foreground="#2050a0")
        lab.grid(row=r, column=3, sticky="w")
        self.state_vars.setdefault("CUR_" + key, cur)
        if kind != "ro":
            b1 = ttk.Button(parent, text="查询", width=6,
                            command=lambda: self.query_one(key, self._fill_curcb(key)))
            b1.grid(row=r, column=4, padx=2)
            if kind != "roq":
                b2 = ttk.Button(parent, text="下发", width=6,
                                command=lambda: self.do_set(key, var, setcmd, confirm))
                b2.grid(row=r, column=5, padx=2)
        if hint:
            ttk.Label(parent, text="· " + hint, foreground="#997700").grid(row=r, column=0, columnspan=6, sticky="w")
        return {"r": r, "var": var, "cur": cur, "key": key}

    def _fill_curcb(self, key):
        def cb(lines, ok):
            cur = self.state_vars.get("CUR_" + key)
            for l in lines:
                kv = REPLY_KV.match(l)
                if kv and kv.group(1).upper() == key:
                    cur.set(kv.group(2))
                    return
            cur.set("无应答/错误")
        return cb

    def do_set(self, key, var, setcmd=None, confirm=None):
        def run():
            v = var.get().strip()
            if setcmd:
                cmd = setcmd(v)
            else:
                if v == "":
                    messagebox.showwarning("提示", "参数为空")
                    return
                cmd = f"AT+{key}={v}"
            def done(lines, ok):
                if any(l.startswith("+OK") for l in lines):
                    self.stat_ok(cmd)
                    self.query_one(key, self._fill_curcb(key))
                else:
                    self.stat_fail(cmd, lines)
            self.query(cmd, done)
        if confirm and not messagebox.askokcancel("确认", confirm):
            return
        run()

    def query_one(self, key, cb=None):
        def _cb(lines, ok):
            if cb:
                cb(lines, ok)
        self.query(f"AT+{key}?", _cb)

    def stat_ok(self, cmd):
        self.var_stat.set(f"✔ {cmd} → +OK")
        self.var_stat_resp = ("#0a7020",)

    def stat_fail(self, cmd, lines):
        err = next((l for l in lines if l.startswith("+ERR")), "无应答")
        self.var_stat.set(f"✘ {cmd} → {err}")
        self.maybe_show_reply(err, force=True)

    # ================================================== Tab 1 基础信息
    def tab_basic(self, p):
        self.row_head(p, "调试与身份(手册 6.3.1~6.3.4, 6.3.30~6.3.32)")
        row = ttk.Frame(p); row.grid(row=1, column=0, sticky="w", pady=4)
        ttk.Button(row, text="AT 握手测试", command=lambda: self.query("AT", None)).pack(side="left")
        self.param_row(p, "NAME", "广播名称", kind="entry", hint="≤16字节,立即生效,掉电保存;默认 CDEBYTE_XXXX")
        self.param_row(p, "MAC", "MAC 地址", kind="roq", hint="MAC 为只读参数(仅查询)")
        row2 = ttk.Frame(p); row2.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(row2, text="角色 ROLE:").pack(side="left")
        self.var_role = tk.StringVar(value="0")
        ttk.Label(row2, textvariable=tk.StringVar(value="")).pack(side="left")
        cb = ttk.Combobox(row2, textvariable=self.var_role, state="readonly", width=18,
                          values=[f"{k} - {v}" for k, v in ROLE_NAMES.items()])
        cb.current(0); cb.pack(side="left", padx=4)
        ttk.Button(row2, text="查询", command=lambda: self.query_one("ROLE")).pack(side="left", padx=2)
        def _set_role():
            v = self.var_role.get().split(" ")[0]
            def done(lines, ok):
                self.stat_ok(f"AT+ROLE={v}") if "+OK" in "".join(lines) else self.stat_fail(f"AT+ROLE={v}", lines)
                if any("+OK" in l for l in lines):
                    self.log("sys", "角色已保存 → 需要 [模块复位] 后生效(RESET)")
            self.query(f"AT+ROLE={v}", done)
        ttk.Button(row2, text="切换角色", command=_set_role).pack(side="left", padx=2)
        ttk.Label(row2, text="· 切换后需重启生效;会清空自动重连列表;Beacon 角色上电串口关闭需 CTS 唤醒",
                  foreground="#997700").pack(side="left", padx=6)
        self.row_head(p, "复位与出厂设置(危险区)", HW_TIPS.get("BEACON_ROLE", ""))
        rowd = ttk.Frame(p); rowd.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        def _reset():
            if messagebox.askokcancel("确认", "发送 AT+RESET 软复位模块?(约 1 秒重启)"):
                def done(lines, ok): self.log("sys", "复位指令已发出,等待 MODULE POWERUP 事件")
                self.query("AT+RESET", done, timeout=4500, quiet=600)
        def _restore():
            if not messagebox.askokcancel("⚠ 恢复出厂",
                    "发送 AT+RESTORE 将清除全部自定义配置(名称/功率/Service/认证码等)并复位!\n继续?"):
                return
            if not messagebox.askokcancel("再次确认",
                    "确定清空全部配置?此操作不可撤销(过程中严禁断电/复位)。"):
                return
            def done(lines, ok): self.log("sys", "恢复出厂指令已发出")
            self.query("AT+RESTORE", done, timeout=4500, quiet=600)
        ttk.Button(rowd, text="模块复位 AT+RESET", style="Danger.TButton", command=_reset).pack(side="left")
        ttk.Button(rowd, text="☠ 恢复出厂 AT+RESTORE", style="Danger.TButton", command=_restore).pack(side="left", padx=8)
        self.row_head(p, "固件信息")
        self.param_row(p, "VERSION", "固件版本", kind="roq", hint=HW_TIPS["VERSION"])

    # ================================================== Tab 2 射频
    def tab_rf(self, p):
        self.row_head(p, "发射功率 PWR(6.3.5)")
        self.param_row(p, "PWR", "发射功率 dBm", kind="combo",
                       values=[str(x) for x in (-28, -20, -10, -5, -3, 0, 1, 2, 4, 6)],
                       hint="取值 -28/-20/-10/-5/-3/0/1/2/4/6;" + HW_TIPS["A1_NOTES"])
        self.row_head(p, "从机物理层速率 PHY(6.3.22,位域可多选)")
        rp = ttk.Frame(p); rp.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        self.phy_vars = {}
        for bit, name in ((1, "1M"), (2, "2M"), (4, "125K Coded"), (8, "500K Coded")):
            v = tk.BooleanVar(value=(bit == 1))
            self.phy_vars[bit] = v
            ttk.Checkbutton(rp, text=name, variable=v).pack(side="left", padx=3)
        self.var_phy_sum = tk.StringVar(value="1")
        ttk.Label(rp, text="参数和:").pack(side="left", padx=(10, 0))
        lab = ttk.Label(rp, textvariable=self.var_phy_sum, foreground="#2050a0")
        lab.pack(side="left")
        for v in self.phy_vars.values():
            v.trace_add("write", lambda *a: self.var_phy_sum.set(str(sum(
                b for b, vv in self.phy_vars.items() if vv.get())) or "0"))
        rowp = ttk.Frame(p); rowp.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Button(rowp, text="查询 PHY", command=lambda: self.query_one("PHY", self._phy_fill)).pack(side="left")
        def _setphy():
            n = sum(b for b, v in self.phy_vars.items() if v.get())
            self.do_set("PHY", tk.StringVar(value=str(n)) if n else None or tk.StringVar(value="1"))
            self.query_one("PHY", self._phy_fill)
        ttk.Button(rowp, text="下发 PHY", command=_setphy).pack(side="left", padx=4)
        ttk.Label(rowp, text="· 1=1M 2=2M 3=1M+2M 4=125K 8=500K;该参数从角色生效,不保存",
                  foreground="#997700").pack(side="left")
        self.row_head(p, "远距离广播 LE_CODED(6.3.8)")
        self.param_row(p, "LE_CODED", "Long Range", kind="combo", values=["0", "1"],
                       hint="0关/1开;从角色支持,单主/Beacon/A1 不支持;配合 SCAN_PHY=1 使用")
        self.row_head(p, "主角色扫描物理层 SCAN_PHY(6.3.13)")
        self.param_row(p, "SCAN_PHY", "扫描物理层", kind="combo", values=["0", "1"],
                       hint="0=1M(默认) 1=LE CODED;仅主角色;设 1 后只能扫到/连接 LE CODED 设备;" + HW_TIPS["A1_NOTES"])

    def _phy_fill(self, lines, ok):
        for l in lines:
            kv = REPLY_KV.match(l)
            if kv and kv.group(1).upper() == "PHY":
                try:
                    n = int(kv.group(2))
                except ValueError:
                    continue
                for b, v in self.phy_vars.items():
                    v.set(bool(n & b))

    # ================================================== Tab 3 广播
    def tab_adv(self, p):
        self.row_head(p, "广播参数 ADV(6.3.6)—— para1 开关 / para2 类型 / para3 间隔ms")
        ra = ttk.Frame(p); ra.grid(row=1, column=0, sticky="w", pady=4)
        ttk.Label(ra, text="开关:").pack(side="left")
        self.var_adv_on = tk.StringVar(value="1")
        ttk.Combobox(ra, textvariable=self.var_adv_on, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        ttk.Label(ra, text="类型(0不可连/1可连):").pack(side="left", padx=(8, 0))
        self.var_adv_type = tk.StringVar(value="1")
        ttk.Combobox(ra, textvariable=self.var_adv_type, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        ttk.Label(ra, text="间隔(20~10240):").pack(side="left", padx=(8, 0))
        self.var_adv_iv = tk.StringVar(value="200")
        ttk.Entry(ra, textvariable=self.var_adv_iv, width=8).pack(side="left")
        ttk.Button(ra, text="查询", command=lambda: self.query_one("ADV", self._adv_fill)).pack(side="left", padx=6)
        ttk.Button(ra, text="下发", command=self._adv_set).pack(side="left")
        ttk.Label(ra, text="· 立即生效,掉电保存;仅从角色(含主从/Beacon)有效",
                  foreground="#997700").pack(side="left", padx=6)

        self.row_head(p, "自定义广播数据 ADVDATA(6.3.7,厂商字段≤26字节)")
        rd = ttk.Frame(p); rd.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        self.var_advd_ascii = tk.BooleanVar(value=True)
        ttk.Label(rd, text="格式: 0=ASCII").pack(side="left")
        ttk.Label(rd, text="0=ASCII 1=HEX:").pack(side="left")
        self.var_advd_fmt = tk.StringVar(value="0")
        ttk.Combobox(rd, textvariable=self.var_advd_fmt, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        ttk.Label(rd, text="数据:").pack(side="left", padx=(8, 0))
        self.var_advd_data = tk.StringVar(value="ebyte")
        ttk.Entry(rd, textvariable=self.var_advd_data, width=16).pack(side="left")
        ttk.Button(rd, text="查询", command=lambda: self.query_one("ADVDATA")).pack(side="left", padx=4)
        def _advd(cur=False):
            fmt = self.var_advd_fmt.get()
            data = self.var_advd_data.get().strip()
            if fmt == "1":
                if not re.fullmatch(r"[0-9A-Fa-f]+", data):
                    messagebox.showwarning("HEX 数据非法", "请输入纯十六进制字符")
                    return
            cmd = f"AT+ADVDATA{'_CUR' if cur else ''}={fmt},{data}"
            self.query(cmd, lambda l, o: self.stat_ok(cmd) if any("+OK" in x for x in l)
                       else self.stat_fail(cmd, l))
        ttk.Button(rd, text="下发(保存)", command=lambda: _advd(False)).pack(side="left", padx=2)
        ttk.Button(rd, text="下发(本帧 _CUR 不保存)", command=lambda: _advd(True)).pack(side="left", padx=2)
        ttk.Button(rd, text="恢复默认数据", command=lambda: self.query(
            "AT+ADVDATA=0", lambda l, o: None)).pack(side="left", padx=2)
        ttk.Label(p, text="· 频繁改建议用 _CUR 防止 Flash 磨损;主角色不支持(+ERR=4)",
                  foreground="#997700").grid(row=p.grid_size()[1], column=0, sticky="w")

        self.row_head(p, "扩展广播 ADV_EXT(6.3.9,≤251字节,交互式两步下发)")
        re_ = ttk.Frame(p); re_.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(re_, text="长度(0~251):").pack(side="left")
        self.var_adve_len = tk.StringVar(value="10")
        ttk.Entry(re_, textvariable=self.var_adve_len, width=6).pack(side="left")
        ttk.Label(re_, text="输入超时(1~5000ms):").pack(side="left", padx=(8, 0))
        self.var_adve_to = tk.StringVar(value="5000")
        ttk.Entry(re_, textvariable=self.var_adve_to, width=7).pack(side="left")
        ttk.Button(re_, text="查询", command=lambda: self.query_one("ADV_EXT")).pack(side="left", padx=6)
        def _adve(cur):
            try:
                n, to = int(self.var_adve_len.get()), int(self.var_adve_to.get())
                assert 0 <= n <= 251 and 1 <= to <= 5000
            except Exception:
                messagebox.showwarning("参数", "长度 0~251,超时 1~5000ms")
                return
            cmd = f"AT+ADV_EXT{'_CUR' if cur else ''}={n},{to}"
            def done(lines, ok):
                if any("INPUT EXT ADV" in l for l in lines):
                    n2 = int(re.search(r":(\d+)", "".join(lines)).group(1))
                    self.log("sys", f"模块等待 {n2} 字节数据 → 在下方数据框输入后按 [发送数据]")
                    self.var_stat.set(f"⏳ 请输入 {n2} 字节广播数据(超时 {to}ms)")
                else:
                    self.stat_fail(cmd, lines)
            self.query(cmd, done, timeout=to + 2500, quiet=350)
        ttk.Button(re_, text="启动下发(保存)", command=lambda: _adve(False)).pack(side="left", padx=2)
        ttk.Button(re_, text="启动下发(_CUR)", command=lambda: _adve(True)).pack(side="left", padx=2)
        def _adve_clear():
            self.query("AT+ADV_EXT=0", lambda l, o: None)
        ttk.Button(re_, text="清除(=0)", command=_adve_clear).pack(side="left", padx=2)
        row2 = ttk.Frame(p); row2.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(row2, text="扩展广播数据(按启动长度准备):").pack(side="left")
        tk_var_fmt = tk.StringVar(value="ascii")
        ttk.Label(row2, text=" 格式:").pack(side="left")
        self.var_adve_fmt = tk.StringVar(value="ascii")
        ttk.Combobox(row2, textvariable=self.var_adve_fmt, values=["ascii", "hex"],
                     width=6, state="readonly").pack(side="left")
        self.var_adve_data = tk.StringVar(value="")
        ttk.Entry(row2, textvariable=self.var_adve_data, width=30).pack(side="left", padx=4)
        def _adve_send():
            try:
                n = int(self.session.pending_input["needed"]) if self.session and self.session.pending_input \
                    else int(self.var_adve_len.get())
            except Exception:
                n = int(self.var_adve_len.get())
            s = self.var_adve_data.get().strip()
            try:
                data = bytes.fromhex(s) if self.var_adve_fmt.get() == "hex" else s.encode("ascii")
            except Exception:
                messagebox.showerror("数据错误", "HEX 非法")
                return
            if len(data) != n:
                if not messagebox.askyesno("长度不符",
                        f"需要 {n} 字节,当前 {len(data)} 字节。自动截断/告知模块?\n选[是]按当前长度截断发送,选[否]返回修改。"):
                    return
                data = data[:n]
            if self.session:
                self.session.send_bytes(data, "扩展广播数据")
            if self.session:
                self.session.pending_input = None
        ttk.Button(row2, text="发送数据", command=_adve_send).pack(side="left", padx=4)
        ttk.Label(p, text="· 启用后设备变为不可连接不可扫描无名称设备;重启后使用最后一次保存的数据",
                  foreground="#997700").grid(row=p.grid_size()[1], column=0, sticky="w")

        self.row_head(p, "Beacon 参数 BEACON(6.3.10,5 字段)")
        rb = ttk.Frame(p); rb.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        self.var_beacon = {}
        defaults = [("CompanyID", "4C00"), ("Major", "2775"), ("Minor", "848F"),
                    ("RSSI@1m", "-48"), ("UUID32", "FDA50693A4E24FB1AFCFC6EB07647825")]
        for name, d in defaults:
            ttk.Label(rb, text=name + ":").pack(side="left", padx=(6 if name != "CompanyID" else 0, 1))
            v = tk.StringVar(value=d)
            self.var_beacon[name] = v
            ttk.Entry(rb, textvariable=v, width=(34 if name == "UUID32" else 6)).pack(side="left")
        rowb2 = ttk.Frame(p); rowb2.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        def _beacon_q():
            def cb(lines, ok):
                for l in lines:
                    kv = REPLY_KV.match(l)
                    if kv and kv.group(1).upper() == "BEACON":
                        parts = kv.group(2).split(",")
                        keys = ["CompanyID", "Major", "Minor", "RSSI@1m", "UUID32"]
                        for i, k in enumerate(keys[:len(parts)]):
                            self.var_beacon[k].set(parts[i].strip())
            self.query("AT+BEACON?", cb)
        def _beacon_set():
            vals = [self.var_beacon[k].get().strip() for k, _ in defaults]
            cmd = "AT+BEACON=" + ",".join(vals)
            self.query(cmd, lambda l, o: self.stat_ok(cmd) if any("+OK" in x for x in l)
                       else self.stat_fail(cmd, l))
        ttk.Button(rowb2, text="查询 Beacon", command=_beacon_q).pack(side="left")
        ttk.Button(rowb2, text="下发 Beacon", command=_beacon_set).pack(side="left", padx=4)
        ttk.Label(rowb2, text="· 立即生效,掉电保存", foreground="#997700").pack(side="left", padx=6)

    def _adv_fill(self, lines, ok):
        for l in lines:
            kv = REPLY_KV.match(l)
            if kv and kv.group(1).upper() == "ADV":
                parts = kv.group(2).split(",")
                if len(parts) == 3:
                    self.var_adv_on.set(parts[0].strip())
                    self.var_adv_type.set(parts[1].strip())
                    self.var_adv_iv.set(parts[2].strip())

    def _adv_set(self):
        cmd = f"AT+ADV={self.var_adv_on.get()},{self.var_adv_type.get()},{self.var_adv_iv.get()}"
        self.query(cmd, lambda l, o: (self.stat_ok(cmd), self.query_one("ADV", self._adv_fill))
                   if any("+OK" in x for x in l) else self.stat_fail(cmd, l))

    # ================================================== Tab 4 从机服务
    def tab_service(self, p):
        self.row_head(p, "从角色服务 UUID SERVICE(6.3.11)", HW_TIPS["SERVICE"])
        top = ttk.Frame(p); top.grid(row=1, column=0, sticky="w", pady=4)
        self.var_svc_128 = tk.BooleanVar(value=False)
        ttk.Label(top, text="UUID 位数:").pack(side="left")
        ttk.Radiobutton(top, text="16位(默认)", value=False, variable=self.var_svc_128,
                        command=self._svc_switch).pack(side="left")
        ttk.Radiobutton(top, text="128位", value=True, variable=self.var_svc_128,
                        command=self._svc_switch).pack(side="left", padx=4)
        srow = ttk.Frame(p); srow.grid(row=2, column=0, sticky="w", pady=4)
        self.var_svc = {}
        for name, d, w in (("服务UUID(server)", "FFF0", 6), ("接收通道", "FFF1", 6),
                           ("发送通道", "FFF2", 6), ("无线AT通道", "FFF3", 6)):
            ttk.Label(srow, text=name + ":").pack(side="left", padx=(8 if name != "服务UUID(server)" else 0, 2))
            v = tk.StringVar(value=d)
            self.var_svc[name] = v
            ttk.Entry(srow, textvariable=v, width=w).pack(side="left")
        self.srow_base128 = ttk.Frame(p); self.srow_base128.grid(row=3, column=0, sticky="w", pady=4)
        ttk.Label(self.srow_base128, text="128位基础UUID(32hex,第3、4字节将被上面服务UUID替换):").pack(side="left")
        self.var_svc_base = tk.StringVar(value="9ECADC240EE5A9E093F3A3B50000406E")
        ttk.Entry(self.srow_base128, textvariable=self.var_svc_base, width=36).pack(side="left")
        self._svc_switch()
        bend = ttk.Frame(p); bend.grid(row=4, column=0, sticky="w", pady=6)
        def _svc_q():
            def cb(lines, ok):
                for l in lines:
                    kv = REPLY_KV.match(l)
                    if kv and kv.group(1).upper() == "SERVICE":
                        parts = [x.strip() for x in kv.group(2).split(",")]
                        self.var_svc_128.set(parts[0] == "1")
                        keys = list(self.var_svc.keys())
                        for i, k in enumerate(keys):
                            if i + 1 < len(parts):
                                self.var_svc[k].set(parts[i + 1])
                        if self.var_svc_128.get() and len(parts) > 5:
                            self.var_svc_base.set(parts[5])
                        self._svc_switch()
            self.query("AT+SERVICE?", cb)
        def _svc_set():
            if self.var_svc_128.get():
                if not re.fullmatch(r"[0-9A-Fa-f]{32}", self.var_svc_base.get()):
                    messagebox.showerror("基础UUID", "需 32 位十六进制字符")
                    return
                cmd = ("AT+SERVICE=1," + ",".join(self.var_svc[k].get() for k in
                        ("服务UUID(server)", "接收通道", "发送通道", "无线AT通道")) +
                       "," + self.var_svc_base.get())
            else:
                cmd = ("AT+SERVICE=0," + ",".join(self.var_svc[k].get() for k in
                        ("服务UUID(server)", "接收通道", "发送通道", "无线AT通道")))
            if messagebox.askokcancel("确认下发", cmd + "\n重启后生效,继续?"):
                self.query(cmd, lambda l, o: self.stat_ok(cmd) if any("+OK" in x for x in l)
                           else self.stat_fail(cmd, l))
        ttk.Button(bend, text="查询 SERVICE", command=_svc_q).pack(side="left")
        ttk.Button(bend, text="下发 SERVICE", command=_svc_set).pack(side="left", padx=4)

    def _svc_switch(self):
        if self.var_svc_128.get():
            self.srow_base128.grid()
        else:
            self.srow_base128.grid_remove()

    # ================================================== Tab 5 主角色(最大)
    def tab_master(self, p):
        self.row_head(p, "⚠ 本页指令仅主角色(单主 1 / 主从一体 2)有效;当前角色不符将被置灰", "")
        rowo = ttk.Frame(p); rowo.grid(row=1, column=0, sticky="w", pady=2)
        ttk.Checkbutton(rowo, text="强制允许(实测角色不符时的错误码)",
                        variable=self.role_override, command=self.apply_role).pack(side="left")

        # ---------- 扫描区 ----------
        self.row_head(p, "主角色扫描 SCAN(6.3.12)+ 扫描过滤 SCANCMP(6.3.33)")
        rs = ttk.Frame(p); rs.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(rs, text="超时(秒):").pack(side="left")
        self.var_scan_to = tk.StringVar(value="10")
        ttk.Entry(rs, textvariable=self.var_scan_to, width=5).pack(side="left")
        self.var_scan_name = tk.BooleanVar(value=True)
        ttk.Checkbutton(rs, text="显示蓝牙名称", variable=self.var_scan_name).pack(side="left", padx=6)
        def _scan_go():
            show = "1" if self.var_scan_name.get() else "0"
            cmd = f"AT+SCAN=1,{self.var_scan_to.get()},{show}"
            if self.session:
                self.scan_clear(None)
                self.session.scan_active = True
            def done(lines, ok):
                if self.session:
                    self.session.scan_active = False
                self.var_stat.set(f"扫描结束(共 {len(self.scan_tv.get_children())} 台)")
            self.query(cmd, done, timeout=int(self.var_scan_to.get()) * 1000 + 3000, quiet=700)
        ttk.Button(rs, text="▶ 开始扫描", command=_scan_go).pack(side="left", padx=4)
        ttk.Button(rs, text="■ 停止",
                   command=lambda: self.query("AT+SCAN=0", lambda l, o: setattr(self.session, "scan_active", False))).pack(side="left")
        ttk.Button(rs, text="清空列表", command=self.scan_clear).pack(side="left", padx=4)
        ttk.Button(rs, text="查询状态", command=lambda: self.query_one("SCAN")).pack(side="left")
        # 过滤
        rsc = ttk.Frame(p); rsc.grid(row=p.grid_size()[1], column=0, sticky="w", pady=2)
        ttk.Label(rsc, text="过滤开关:").pack(side="left")
        self.var_scancmp_on = tk.StringVar(value="0")
        ttk.Combobox(rsc, textvariable=self.var_scancmp_on, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        ttk.Label(rsc,	text="过滤名称(≤20字节):").pack(side="left", padx=(8, 0))
        self.var_scancmp_name = tk.StringVar(value="")
        ttk.Entry(rsc, textvariable=self.var_scancmp_name, width=12).pack(side="left")
        def _scancmp_q():
            def cb(lines, ok):
                for l in lines:
                    kv = REPLY_KV.match(l)
                    if kv and kv.group(1).upper() == "SCANCMP":
                        parts = kv.group(2).split(",")
                        self.var_scancmp_on.set(parts[0].strip())
                        if len(parts) > 1:
                            self.var_scancmp_name.set(parts[1].strip())
            self.query("AT+SCANCMP?", cb)
        def _scancmp_set():
            cmd = f"AT+SCANCMP={self.var_scancmp_on.get()},{self.var_scancmp_name.get()}"
            self.query(cmd, lambda l, o: self.stat_ok(cmd) if any("+OK" in x for x in l)
                       else self.stat_fail(cmd, l))
        ttk.Button(rsc, text="查询过滤", command=_scancmp_q).pack(side="left", padx=4)
        ttk.Button(rsc, text="下发过滤", command=_scancmp_set).pack(side="left")
        # 扫描结果表格
        tvf = ttk.Frame(p); tvf.grid(row=p.grid_size()[1], column=0, sticky="we", pady=4)
        cols = ("idx", "mac", "rssi", "name")
        self.scan_tv = ttk.Treeview(tvf, columns=cols, show="headings", height=8)
        for c, w, t in (("idx", 50, "序号"), ("mac", 140, "MAC"), ("rssi", 60, "RSSI"), ("name", 240, "名称")):
            self.scan_tv.heading(c, text=t)
            self.scan_tv.column(c, width=w)
        self.scan_tv.pack(side="left", fill="both", expand=True)
        ttk.Button(tvf, text="连接\n选中", width=8, command=self._connect_selected).pack(side="left", fill="y")
        self.scan_tv.bind("<Double-1>", lambda e: self._connect_selected())
        hint = ttk.Label(p, text="· 双击行或选行后[连接选中];连接超时 10 秒;达 20 台自动停止",
                         foreground="#997700")
        hint.grid(row=p.grid_size()[1], column=0, sticky="w")

        # ---------- 连接区 ----------
        self.row_head(p, "主角色连接 CONNECT / 连接列表 CONNECT_LIST(6.3.14/16)" + " — " + HW_TIPS["CONNECT"])
        rc = ttk.Frame(p); rc.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(rc, text="MAC(6字节,如 C1:02:03:04:05:06):").pack(side="left")
        self.var_conn_mac = tk.StringVar(value="")
        ttk.Entry(rc, textvariable=self.var_conn_mac, width=18).pack(side="left", padx=3)
        def _connect():
            mac = self.var_conn_mac.get().strip().upper()
            if not re.fullmatch(r"([0-9A-F]{2}:){5}[0-9A-F]{2}", mac):
                messagebox.showwarning("MAC 格式", "需要 6 字节 MAC,如 C1:02:03:04:05:06")
                return
            cmd = f"AT+CONNECT=,{mac}"
            self.var_stat.set("⏳ 连接中…(超时 10s)")
            self.query(cmd, lambda l, o: None, timeout=12000, quiet=400)
        ttk.Button(rc, text="发起连接", command=_connect).pack(side="left", padx=4)
        ttk.Button(rc, text="显示已连接", command=lambda: self.query_one("CONNECT_LIST")).pack(side="left")
        ttk.Button(rc, text="断开全部", style="Danger.TButton",
                   command=lambda: (messagebox.askokcancel("确认", "断开全部连接?")
                                    and self.query("AT+DISCON", lambda l, o: None))).pack(side="left", padx=6)

        # 透传 handle
        rh = ttk.Frame(p); rh.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(rh, text="透传目标 TRM_HANDLE(1~8,255=无):").pack(side="left")
        self.var_trm = tk.StringVar(value="1")
        ttk.Entry(rh, textvariable=self.var_trm, width=4).pack(side="left", padx=2)
        ttk.Button(rh, text="查询", command=lambda: self.query_one("TRM_HANDLE")).pack(side="left", padx=2)
        ttk.Button(rh, text="下发",
                   command=lambda: self.query(f"AT+TRM_HANDLE={self.var_trm.get()}",
                                              lambda l, o: None)).pack(side="left")
        ttk.Label(rh, text="· 多连接时指定数据发往哪个 handle;立即生效,不保存;查询用 CONNECT_LIST 获取",
                  foreground="#997700").pack(side="left", padx=6)

        # ---------- 数据收发(SEND) ----------
        self.row_head(p, "指令发送数据 SEND(6.3.15,交互式)+ 收到的 BLE 数据")
        rse = ttk.Frame(p); rse.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(rse, text="handle(1~8):").pack(side="left")
        self.var_send_h = tk.StringVar(value="1")
        ttk.Entry(rse, textvariable=self.var_send_h, width=4).pack(side="left", padx=2)
        ttk.Label(rse, text="数据:").pack(side="left", padx=(8, 0))
        self.var_send_data = tk.StringVar(value="")
        ttk.Entry(rse, textvariable=self.var_send_data, width=20).pack(side="left")
        self.var_send_fmt = tk.StringVar(value="ascii")
        ttk.Combobox(rse, textvariable=self.var_send_fmt, values=["ascii", "hex"],
                     width=6, state="readonly").pack(side="left", padx=3)
        ttk.Label(rse, text="超时(1~5000):").pack(side="left", padx=(8, 0))
        self.var_send_to = tk.StringVar(value="2000")
        ttk.Entry(rse, textvariable=self.var_send_to, width=6).pack(side="left")
        def _send():
            try:
                h = int(self.var_send_h.get())
                assert 1 <= h <= 8
            except Exception:
                messagebox.showwarning("handle", "1~8")
                return
            s = self.var_send_data.get().strip()
            try:
                data = bytes.fromhex(s) if self.var_send_fmt.get() == "hex" else s.encode("ascii")
            except Exception:
                messagebox.showerror("数据", "HEX 非法")
                return
            to = self.var_send_to.get() or "500"
            cmd = f"AT+SEND={h},{len(data)},{to}"
            if self.session:
                self._send_pending = data
            def done(lines, ok):
                if any("INPUT BLE DATA" in l for l in lines):
                    self.var_stat.set(f"⏳ 请按 [发送数据] 输入 {len(data)} 字节")
                else:
                    self.stat_fail(cmd, lines)
            self.query(cmd, done, timeout=int(to) + 2000, quiet=350)
        ttk.Button(rse, text="启动 SEND", command=_send).pack(side="left", padx=4)
        def _send_data():
            data = getattr(self, "_send_pending", None)
            if data and self.session:
                self.session.send_bytes(data, "SEND数据")
                self.session.pending_input = None
            else:
                messagebox.showinfo("提示", "先 [启动 SEND] 等模块提示输入")
        ttk.Button(rse, text="发送数据", command=_send_data).pack(side="left")
        ttk.Label(p, text="· 无连接会返回 +ERR=3;接收方(手机等)发来的数据打印 +RECEIVED:handle,长度",
                  foreground="#997700").grid(row=p.grid_size()[1], column=0, sticky="w")
        recvf = ttk.LabelFrame(p, text="收到的 BLE 数据(+RECEIVED / 透传数据)", padding=4)
        recvf.grid(row=p.grid_size()[1], column=0, sticky="we", pady=4)
        self.txt_recv = tk.Text(recvf, height=5, state="disabled", font=("Consolas", 9))
        self.txt_recv.pack(fill="both", expand=True)

        # ---------- 自动重连 ----------
        self.row_head(p, "自动重连 AUTO_CNT(6.3.18)/ 删除 DEV_DEL(6.3.19) — " + HW_TIPS["DEV_DEL"])
        ra2 = ttk.Frame(p); ra2.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(ra2, text="列表状态:").pack(side="left")
        self.var_autoc_state = tk.StringVar(value="-")
        ttk.Label(ra2, textvariable=self.var_autoc_state, foreground="#2050a0").pack(side="left", padx=3)
        ttk.Button(ra2, text="查询 AUTO_CNT", command=self._autoc_q).pack(side="left", padx=2)
        self.var_autoc_on = tk.StringVar(value="1")
        ttk.Combobox(ra2, textvariable=self.var_autoc_on, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        self.var_autoc_mac = tk.StringVar(value="")
        ttk.Entry(ra2, textvariable=self.var_autoc_mac, width=16).pack(side="left", padx=3)
        ttk.Button(ra2, text="开启+添加MAC",
                   command=lambda: self.query(f"AT+AUTO_CNT={self.var_autoc_on.get()}," + self.var_autoc_mac.get(),
                                              lambda l, o: self._autoc_q())).pack(side="left")
        ttk.Button(ra2, text="仅设开关",
                   command=lambda: self.query(f"AT+AUTO_CNT={self.var_autoc_on.get()}",
                                              lambda l, o: self._autoc_q())).pack(side="left", padx=2)
        ttk.Button(ra2, text="删除该MAC",
                   command=lambda: self.query(f"AT+DEV_DEL={self.var_autoc_mac.get()}",
                                              lambda l, o: self._autoc_q())).pack(side="left", padx=2)
        ttk.Button(ra2, text="清空列表(ALL)", style="Danger.TButton",
                   command=lambda: (messagebox.askokcancel("确认", "删除全部自动重连设备?")
                                    and self.query("AT+DEV_DEL=ALL", lambda l, o: self._autoc_q()))).pack(side="left")
        ttk.Label(ra2, text="· 重启生效,掉电保存",
                  foreground="#997700").pack(side="left", padx=6)

        # ---------- 观察者 ----------
        self.row_head(p, "观察者 OBSERVER(6.3.21,持续打印广播)")
        ro = ttk.Frame(p); ro.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(ro, text="模式:").pack(side="left")
        self.var_obs_mode = tk.StringVar(value="1")
        ttk.Combobox(ro, textvariable=self.var_obs_mode, values=["0", "1", "2"],
                     width=3, state="readonly").pack(side="left")
        ttk.Label(ro, text="(0关 1普通 2扩展广播;模式2需先 SCAN_PHY=1)").pack(side="left")
        def _obs_go():
            cmd = f"AT+OBSERVER={self.var_obs_mode.get()}"
            if self.session:
                self.session.observer_active = self.var_obs_mode.get() != "0"
            self.query(cmd, lambda l, o: None, timeout=1500)
            self.var_stat.set("⏳ 观察者运行中…")
        ttk.Button(ro, text="启动", command=_obs_go).pack(side="left", padx=4)
        ttk.Button(ro, text="停止", command=lambda: self.query(
            "AT+OBSERVER=0", lambda l, o: (setattr(self.session, "observer_active", False)))).pack(side="left")
        ttk.Button(ro, text="查询", command=lambda: self.query_one("OBSERVER")).pack(side="left", padx=4)
        ttk.Button(ro, text="暂停滚动", command=lambda: self.obs_pause()).pack(side="left", padx=4)
        ttk.Button(ro, text="清空", command=lambda: self.txt_obs.delete("1.0", "end")).pack(side="left")
        obsf = ttk.LabelFrame(p, text="观察者输出", padding=2)
        obsf.grid(row=p.grid_size()[1], column=0, sticky="we", pady=2)
        self.txt_obs = tk.Text(obsf, height=7, state="disabled", font=("Consolas", 8))
        self.txt_obs.pack(fill="both", expand=True)

    def scan_add_row(self, g):
        idx, mac, rssi, name = g
        for iid in self.scan_tv.get_children():
            if self.scan_tv.set(iid, "mac") == mac:
                self.scan_tv.set(iid, "rssi", rssi)
                if name:
                    self.scan_tv.set(iid, "name", name)
                return
        self.scan_tv.insert("", "end", values=(idx, mac, rssi, name))

    def scan_clear(self, _=None):
        for iid in self.scan_tv.get_children():
            self.scan_tv.delete(iid)

    def _connect_selected(self):
        sel = self.scan_tv.selection()
        if not sel:
            messagebox.showinfo("提示", "先在列表中选择设备行")
            return
        mac = self.scan_tv.set(sel[0], "mac")
        self.var_conn_mac.set(mac)
        cmd = f"AT+CONNECT=,{mac}"
        self.var_stat.set(f"⏳ 连接 {mac} …(超时 10s)")
        self.query(cmd, lambda l, o: None, timeout=12000, quiet=400)

    def _autoc_q(self):
        def cb(lines, ok):
            joined = " ".join(lines)
            m = re.search(r"\+AUTO_CNT=?\s*(.*)", joined)
            self.var_autoc_state.set(m.group(1).strip() if m else "?")
        self.query("AT+AUTO_CNT?", cb)

    def obs_pause(self):
        self.txt_obs.configure(state="normal")
        # 暂停滚动 = 不自动看            (实现:切换标记)
        self.var_obs_autoscroll = not getattr(self, "var_obs_autoscroll", False)

    def observer_add(self, line):
        self.txt_obs.configure(state="normal")
        self.txt_obs.insert("end", line + "\n")
        if getattr(self, "var_obs_autoscroll", True):
            self.txt_obs.see("end")
        if int(self.txt_obs.index("end-1c").split(".")[0]) > 2000:
            self.txt_obs.delete("1.0", "1000.0")
        self.txt_obs.configure(state="disabled")

    def got_received(self, line):
        for tw in (self.txt_recv,):
            tw.configure(state="normal")
            tw.insert("end", time.strftime("%H:%M:%S ") + line + "\n")
            tw.see("end")
            tw.configure(state="disabled")

    def on_link_event(self, line):
        self.var_stat.set("事件: " + line[:60])

    # ================================================== Tab 6 串口功耗
    def tab_uart(self, p):
        self.row_head(p, "串口波特率 BAUD(6.3.23)" + " — " + HW_TIPS["BAUD"])
        self.param_row(p, "BAUD", "波特率", kind="combo", values=BAUDS, width=9,
                       hint="重启生效,掉电保存;若改了波特率,需复位后用新波特率重连",
                       confirm="修改波特率需重启后生效,继续?")
        self.row_head(p, "连接间隔 CONN_INTERVAL(6.3.24,参数×1.25ms)")
        rci = ttk.Frame(p); rci.grid(row=1, column=0, sticky="w", pady=4)
        ttk.Label(rci, text="参数(6~3200):").pack(side="left")
        self.var_ci_param = tk.StringVar(value="16")
        ttk.Entry(rci, textvariable=self.var_ci_param, width=7).pack(side="left")
        ttk.Label(rci, text="→ 毫秒:").pack(side="left", padx=(10, 0))
        self.var_ci_ms = tk.StringVar(value="20.0")
        ttk.Label(rci, textvariable=self.var_ci_ms, foreground="#2050a0").pack(side="left")
        self.var_ci_param.trace_add("write", lambda *a: self.var_ci_ms.set(
            f"{float(self.var_ci_param.get() or 0) * 1.25:.2f}" if re.fullmatch(r"\d+(\.\d+)?", self.var_ci_param.get() or "x") else "?"))
        ttk.Button(rci, text="查询", command=lambda: self.query_one("CONN_INTERVAL")).pack(side="left", padx=6)
        ttk.Button(rci, text="下发", command=lambda: self.query(
            f"AT+CONN_INTERVAL={self.var_ci_param.get()}",
            lambda l, o: None)).pack(side="left")
        ttk.Label(rci, text="· 100ms=80;连接间隔越大越省电但越慢;手机建议≥20ms",
                  foreground="#997700").pack(side="left", padx=6)
        self.row_head(p, "状态输出 LOGMSG(6.3.27)")
        self.param_row(p, "LOGMSG", "模块状态打印", kind="combo", values=["0", "1"],
                       hint="1=连接/断开/POWERUP 等事件打印(日志区以橙色横幅显示)")
        self.row_head(p, "看门狗 WDOG(6.3.29)")
        self.param_row(p, "WDOG", "看门狗", kind="combo", values=["0", "1"],
                       hint="默认1=开启;关闭可降 2~3uA;重启生效")
        self.row_head(p, "串口/BLE 开关 SLEEP(6.3.28)" + " — " + HW_TIPS["SLEEP"])
        rsl = ttk.Frame(p); rsl.grid(row=p.grid_size()[1], column=0, sticky="w", pady=4)
        ttk.Label(rsl, text="模块串口:").pack(side="left")
        self.var_sl_uart = tk.StringVar(value="1")
        ttk.Combobox(rsl, textvariable=self.var_sl_uart, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        ttk.Label(rsl, text="模块BLE:").pack(side="left", padx=(10, 0))
        self.var_sl_ble = tk.StringVar(value="1")
        ttk.Combobox(rsl, textvariable=self.var_sl_ble, values=["0", "1"], width=3,
                     state="readonly").pack(side="left")
        ttk.Button(rsl, text="查询", command=lambda: self.query_one("SLEEP")).pack(side="left", padx=6)
        def _sleep_set():
            cmd = f"AT+SLEEP={self.var_sl_uart.get()},{self.var_sl_ble.get()}"
            warn = (self.var_sl_uart.get() == "0") and ("⚠ 关闭串口后将无法收发 AT 指令,"
                    "需要 CTS(接 RTS)上升沿唤醒 —— 可用顶部 [RTS唤醒脉冲]!\n\n")
            if not messagebox.askokcancel("确认下发", warn + cmd):
                return
            self.query(cmd, lambda l, o: None)
        ttk.Button(rsl, text="下发", command=_sleep_set).pack(side="left")
        ttk.Label(rsl, text="· 0,0=最低休眠;1,0=蓝牙静默;0,1=仅关串口",
                  foreground="#997700").pack(side="left", padx=6)

    # ================================================== Tab 7 认证
    def tab_auth(self, p):
        self.row_head(p, "APP 配置认证密码 UP_AUTH(6.3.26)")
        self.param_row(p, "UP_AUTH", "密码(6位数字)", kind="entry",
                       hint="仅串口可改;立即生效,掉电保存;手机 APP 连接后需输入此密码")
        self.row_head(p, "APP 配置认证 AUTH(6.3.25)" + " — " + HW_TIPS["AUTH"])
        raut = ttk.Frame(p); raut.grid(row=1, column=0, sticky="w", pady=4)
        ttk.Label(raut, text="AUTH(6位):").pack(side="left")
        self.var_auth = tk.StringVar(value="123456")
        ttk.Entry(raut, textvariable=self.var_auth, width=8).pack(side="left")
        def _auth():
            cmd = f"AT+AUTH={self.var_auth.get()}"
            self.query(cmd, lambda l, o: None)
        ttk.Button(raut, text="下发(串口)", command=_auth).pack(side="left", padx=4)
        ttk.Label(raut, text="· 本指令必须大写;实际由手机 APP 配置通道使用,串口发送预期 +ERR=4(正常)",
                  foreground="#997700").pack(side="left")

    # ================================================== Tab 8 AT 终端
    def tab_terminal(self, p):
        self.row_head(p, "自由 AT 终端:发送任意指令(自动加 \\\\r\\\\n),应答/事件见右侧日志")
        top = ttk.Frame(p); top.grid(row=1, column=0, sticky="we", pady=4)
        self.var_term = tk.StringVar(value="")
        self.term_hist, self.term_hist_idx = [], -1
        ent = ttk.Entry(top, textvariable=self.var_term, width=40)
        ent.pack(side="left")
        ent.bind("<Return>", lambda e: self.term_send())
        ent.bind("<Up>", self.term_up)
        ent.bind("<Down>", self.term_down)
        ttk.Button(top, text="发送", command=self.term_send).pack(side="left", padx=4)
        hist = ttk.Frame(p); hist.grid(row=2, column=0, sticky="we", pady=2)
        for label, cmd in (("AT", "AT"), ("查全部", None), ("查角色", "AT+ROLE?"),
                           ("查连接列表", "AT+CONNECT_LIST?"), ("复位", "AT+RESET")):
            def mk(c=cmd):
                def go():
                    if c is None:
                        self.read_all()
                    else:
                        self.query(c, None)
                return go
            ttk.Button(hist, text=label, width=8, command=mk()).pack(side="left", padx=2)
        self.txt_term = tk.Text(p, height=10, state="disabled", font=("Consolas", 9))
        self.txt_term.grid(row=3, column=0, sticky="we", pady=4)
        ttk.Label(p, text="↑↓ 键翻历史", foreground="#888").grid(row=4, column=0, sticky="w")

    def term_send(self):
        cmd = self.var_term.get().strip()
        if not cmd or not self.session:
            return
        self.term_hist.insert(0, cmd)
        self.term_hist_idx = -1
        self.query(cmd, None)
        self.var_term.set("")

    def term_up(self, e=None):
        if self.term_hist and self.term_hist_idx < len(self.term_hist) - 1:
            self.term_hist_idx += 1
            self.var_term.set(self.term_hist[self.term_hist_idx])
        return "break"

    def term_down(self, e=None):
        if self.term_hist_idx > 0:
            self.term_hist_idx -= 1
            self.var_term.set(self.term_hist[self.term_hist_idx])
        else:
            self.term_hist_idx = -1
            self.var_term.set("")
        return "break"

    # ------------------------------------------------ 角色门控
    def set_role(self, role):
        self.current_role = role
        self.var_link.set(self.var_link.get())   # keep
        self.log("sys", f"当前角色: {role} {ROLE_NAMES.get(role, '')}")
        self.apply_role()

    def apply_role(self):
        role = getattr(self, "current_role", "0")
        master_ok = role in ("1", "2")
        override = self.role_override.get()
        # 主角色 tab 内控件
        try:
            master_tab = list(self.tab_builders).index("主角色连接")
        except ValueError:
            return
        nb_tab = self.nb.tabs()[master_tab]
        for w in self._walk(self.nb.nametowidget(nb_tab)):
            if isinstance(w, (ttk.Button, ttk.Entry, ttk.Combobox, ttk.Checkbutton)) and \
               str(w) != str(self.role_override) and not (isinstance(w, ttk.Checkbutton)):
                try:
                    if master_ok or override:
                        w.state(["!disabled"])
                    else:
                        w.state(["disabled"])
                except Exception:
                    pass
        # 广播 Tab 提示
        if role == "1":
            self.log("sys", "提示:当前为单主角色,ADVDATA/LE_CODED 等从角色指令将 +ERR=4")

    def _walk(self, w):
        yield w
        for c in w.winfo_children():
            yield from self._walk(c)

    # ------------------------------------------------ 一键读取 / 导出
    def read_all(self):
        if not self.session:
            messagebox.showinfo("提示", "请先连接")
            return
        self.start_chain_read(full=True)

    def start_chain_read(self, full=False):
        role = getattr(self, "current_role", "0")
        cmds = ["ROLE", "NAME", "MAC", "VERSION", "PWR", "ADV", "PHY", "BAUD",
                "CONN_INTERVAL", "SLEEP", "WDOG", "LOGMSG", "UP_AUTH", "SCANCMP",
                "ADVDATA", "ADV_EXT", "LE_CODED", "BEACON", "SERVICE"]
        if role in ("1", "2"):
            cmds += ["SCAN", "SCAN_PHY", "SCANCMP", "CONNECT_LIST", "TRM_HANDLE", "AUTO_CNT", "OBSERVER"]
        if not full:
            cmds = ["ROLE", "NAME", "MAC", "VERSION"]
        self.chain_queue = cmds[:]
        self.var_stat.set("一键读取中…")
        self._chain_step()

    def _chain_step(self):
        if not self.chain_queue:
            self.var_stat.set("一键读取完成 ✔")
            return
        if not self.session:
            return
        key = self.chain_queue.pop(0)
        # 特殊回填
        cbmap = {"ADV": self._adv_fill, "PHY": self._phy_fill}
        cb = cbmap.get(key)
        def _done(lines, ok):
            if cb:
                cb(lines, ok)
            elif key == "BEACON":
                pass   # state_set 已在 dispatch 自动回填左栏
            self.root.after(60, self._chain_step)
        self.query(f"AT+{key}?", _done, timeout=2000, quiet=220)

    def export_cfg(self):
        data = {k: v.get() for k, v in self.state_vars.items() if not k.startswith("CUR_")}
        fn = filedialog.asksaveasfilename(defaultextension=".json",
                                         initialfile=f"E104-BT53_config_{time.strftime('%Y%m%d_%H%M%S')}.json",
                                         filetypes=[("JSON", "*.json")])
        if not fn:
            return
        with open(fn, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        self.log("sys", f"配置已导出: {fn}")

    def export_log(self):
        fn = filedialog.asksaveasfilename(defaultextension=".txt",
                                          initialfile=f"serial_log_{time.strftime('%Y%m%d_%H%M%S')}.txt",
                                          filetypes=[("文本", "*.txt")])
        if not fn:
            return
        with open(fn, "w", encoding="utf-8") as f:
            f.write(self.txt_log.get("1.0", "end"))
        self.log("sys", f"日志已导出: {fn}")

    def show_err_ref(self):
        top = tk.Toplevel(self.root)
        top.title("错误码与实测参考")
        top.geometry("640x520")
        txt = tk.Text(top, wrap="word", font=("Microsoft YaHei UI", 9))
        txt.pack(fill="both", expand=True)
        txt.insert("end", "【+ERR 错误码】(手册 6.2)\n")
        for k, v in ERR_CODES.items():
            txt.insert("end", f"  +ERR={k}  {v}\n")
        txt.insert("end", "\n【模块状态事件】(手册 5.5)\n")
        txt.insert("end", "  MODULE POWERUP            模块启动\n"
                           "  XX:..:XX CONNECTD P*X      从角色连接成功(handle=P编号)\n"
                           "  XX:..:XX DISCONNECTED P   从角色断开\n"
                           "  XX:..:XX CONNECTD C*Y      主角色连接成功(handle=Y)\n"
                           "  XX:..:XX DISCONNECTD C    主角色断开\n"
                           "  ALREADY CONNECTED          已连接此设备\n"
                           "  XX:..:XX CONNECT TIMEOUT   连接从设备超时(10s)\n"
                           "  RECEIVE TIMEOUT            交互式数据输入超时失败\n")
        txt.insert("end", "\n【本机实测提示(7413-1-16 固件)】\n")
        for k, v in HW_TIPS.items():
            txt.insert("end", f"  · {v}\n" if k != "A1_NOTES" else f"  · A1 版本:{v}\n")
        txt.configure(state="disabled")

    # ------------------------------------------------ 日志 / 状态
    def log(self, tag, text):
        if not hasattr(self, "txt_log"):
            return
        self.txt_log.configure(state="normal")
        # 行数限制
        if int(self.txt_log.index("end-1c").split(".")[0]) > 4000:
            self.txt_log.delete("1.0", "1500.0")
        self.txt_log.insert("end", datetime.now().strftime("%H:%M:%S.%f")[:-3] + " ")
        self.txt_log.insert("end", ("TX " if tag == "tx" else "RX " if tag == "rx" else ""),
                            tag)
        self.txt_log.insert("end", text + "\n", tag)
        if self.var_autoscroll.get():
            self.txt_log.see("end")
        self.txt_log.configure(state="disabled")

    def log_tx(self, text):
        self.log("tx", text)

    def log_rx_bytes(self, data: bytes):
        if self.var_hex.get():
            self.log("rx", data.hex(" "))
        # 文本交给行级 dispatch 打印

    def maybe_show_reply(self, line, force=False):
        self.var_lastreply.set(line[:80])
        if force or line.startswith("+") or "TIMEOUT" in line:
            pass   # 已由 dispatch 打印,这里只更新最近应答

    def show_errcode(self, line):
        m = re.match(r"\+ERR=(\d+)", line)
        if m:
            desc = ERR_CODES.get(m.group(1), "?")
            self.var_stat.set(f"✘ {line} — {desc}")
            if self.session:
                if m.group(1) == "4":
                    tip = "当前角色不支持该指令;可在[主角色连接]页勾选'强制允许'继续实验"
                    self.log("sys", "提示: " + tip)
        self.last_err = line


# ============================== 启动 ==============================
def main():
    root = tk.Tk()
    app = App(root)

    def _excepthook(t, v, tb):
        try:
            messagebox.showerror("程序异常", f"{t}: {v}")
        except Exception:
            pass
    sys.excepthook = _excepthook

    root.mainloop()


if __name__ == "__main__":
    main()
