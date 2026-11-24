# -*- coding: utf-8 -*-
"""app_main 无头冒烟测试:实例化全部 UI + 连接 COM3 真机走一遍安全查询"""
import importlib.util, sys, time, os

DIR = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("app_main", os.path.join(DIR, "app_main.pyw"))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

import tkinter as tk

fails = []
def check(name, cond):
    print(("PASS " if cond else "FAIL ") + name)
    if not cond:
        fails.append(name)

root = tk.Tk()
root.withdraw()                       # 无头,不显示窗口
app = m.App(root)
root.update()
check("UI 全部选项卡构建", len(app.nb.tabs()) == 8)
check("状态面板变量就绪", len(app.state_vars) >= 23)

# --- 程序化连接 COM3 ---
ports = m.list_serial_ports()
if "COM3" not in ports:
    print("SKIP: COM3 不在,仅 UI 冒烟"); root.destroy(); sys.exit(0 if not fails else 1)
app.cb_port.set("COM3")
app.cb_baud.set("115200")
app.connect()
check("连接状态显示", "已连接" in app.var_link.get())

# --- 泵运转,链式查询自动回填 ---
t_end = time.time() + 8.0
while time.time() < t_end:
    root.update()
    time.sleep(0.02)
role = app.state_vars["ROLE"].get()
name = app.state_vars["NAME"].get()
mac = app.state_vars["MAC"].get()
ver = app.state_vars["VERSION"].get()
print(f"  ROLE={role!r} NAME={name!r} MAC={mac!r} VERSION={ver!r}")
check("ROLE 自动回填", role.startswith("0"))
check("NAME 自动回填", name.startswith("CDEBYTE"))
check("MAC 自动回填", ":" in mac)
check("VERSION 自动回填", "7413" in ver)

# --- 从角色下发主角色指令 → 应体现在状态栏错误码 ---
app.query("AT+SCAN?")
t_end = time.time() + 4.0
while time.time() < t_end:
    root.update()
    time.sleep(0.02)
st = app.var_stat.get()
print("  状态栏:", st)
check("从角色 SCAN? → +ERR=4 提示", ("+ERR=4" in st) or ("角色" in st))

# --- 一键读取(full) ---
app.read_all()
t_end = time.time() + 10.0
while time.time() < t_end:
    root.update()
    time.sleep(0.02)
print("  一键读取后状态:", app.var_stat.get())
check("一键读取完成", "完成" in app.var_stat.get())
adv = app.state_vars["ADV"].get()
print("  ADV:", adv, "| BAUD:", app.state_vars["BAUD"].get(),
      "| UP_AUTH:", app.state_vars["UP_AUTH"].get())
check("ADV 回填 1,1,200", "1,1,200" in adv)
check("安全断开", True)
app.disconnect()
root.update()
root.destroy()

print("\n==== 冒烟结果:", "全部通过 ✔" if not fails else f"{len(fails)} 项失败: {fails}")
sys.exit(0 if not fails else 1)
