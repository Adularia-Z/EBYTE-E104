# -*- coding: utf-8 -*-
"""
E104-BT53 调试助手 - 串口引擎
零第三方依赖:ctypes 直连 Win32 API (CreateFileW/ReadFile/WriteFile/DCB/CommTimeouts)
特性:
  - COM 口枚举 (QueryDosDevice)
  - 独立读线程 → rx queue (线程安全)
  - 带锁写入
  - RTS/DTR 控制(支持 CTS 上升沿唤醒模块)
  - 115200-8N1 默认参数,可选波特率
"""
import ctypes, time, re, threading, queue
from ctypes import wintypes

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GENERIC_READ, GENERIC_WRITE, OPEN_EXISTING = 0x80000000, 0x40000000, 3
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

PURGE_RXABORT, PURGE_RXCLEAR, PURGE_TXABORT, PURGE_TXCLEAR = 0x0002, 0x0008, 0x0001, 0x0004


class CommTimeouts(ctypes.Structure):
    _fields_ = [("ReadIntervalTimeout", wintypes.DWORD),
                ("ReadTotalTimeoutMultiplier", wintypes.DWORD),
                ("ReadTotalTimeoutConstant", wintypes.DWORD),
                ("WriteTotalTimeoutMultiplier", wintypes.DWORD),
                ("WriteTotalTimeoutConstant", wintypes.DWORD)]


class DCB(ctypes.Structure):
    _fields_ = [("DCBlength", wintypes.DWORD), ("BaudRate", wintypes.DWORD),
                ("fBitfield", wintypes.DWORD), ("wReserved", wintypes.WORD),
                ("XonLim", wintypes.WORD), ("XoffLim", wintypes.WORD),
                ("ByteSize", wintypes.BYTE), ("Parity", wintypes.BYTE),
                ("StopBits", wintypes.BYTE), ("XonChar", ctypes.c_char),
                ("XoffChar", ctypes.c_char), ("ErrorChar", wintypes.BYTE),
                ("EofChar", wintypes.BYTE), ("EvtChar", wintypes.BYTE),
                ("wReserved1", wintypes.WORD)]


def list_serial_ports():
    """枚举本机 COM 口, 返回排序后的列表 ['COM3', 'COM4', ...]"""
    bufsize = 65536
    buf = ctypes.create_unicode_buffer(bufsize)
    n = kernel32.QueryDosDeviceW(None, buf, bufsize)   # 返回拷贝的字符数(含结尾0)
    if n == 0:
        return []
    text = ctypes.string_at(ctypes.addressof(buf), n * 2).decode("utf-16-le", errors="ignore")
    ports = [p for p in text.split("\x00") if re.match(r"^COM\d+$", p)]
    return sorted(set(ports), key=lambda s: int(s[3:]))


class SerialPort:
    """Win32 串口封装(基于已对真机验证过的参数与超时模型)"""

    def __init__(self, name, baud=115200, on_error=None):
        self.name, self.baud = name, baud
        self.is_open = False
        self.on_error = on_error          # callback(str) 读线程异常
        self.rx_queue = queue.Queue()
        self._wlock = threading.Lock()
        self._rthr = None
        self._h = None
        self._open(name, baud)

    # ---------------- 内部 ----------------
    def _open(self, name, baud):
        h = kernel32.CreateFileW(ctypes.c_wchar_p(name),
                                GENERIC_READ | GENERIC_WRITE, 0, None,
                                OPEN_EXISTING, 0, None)
        if not h or h == INVALID_HANDLE_VALUE:
            err = ctypes.get_last_error()
            raise OSError(err, f"无法打开 {name} (WinError {err};可能被其它程序占用)")
        self._h = h
        # 非阻塞读: MAXDWORD 组合(读立即返回现有字节)
        tmo = CommTimeouts(0xFFFFFFFF, 0xFFFFFFFF, 1, 0, 3000)
        if not kernel32.SetCommTimeouts(h, ctypes.byref(tmo)):
            kernel32.CloseHandle(h)
            raise OSError(ctypes.get_last_error(), "SetCommTimeouts 失败")
        if not self._apply_dcb(baud, rts_on=True):
            kernel32.CloseHandle(h)
            raise OSError(ctypes.get_last_error(), "SetCommState(DCB) 失败")
        kernel32.PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR | PURGE_RXABORT | PURGE_TXABORT)
        self.is_open = True
        self._rthr = threading.Thread(target=self._reader, daemon=True, name="rx-thread")
        self._rthr.start()

    def _apply_dcb(self, baud, rts_on):
        dcb = DCB()
        dcb.DCBlength = ctypes.sizeof(DCB)
        dcb.BaudRate = baud
        # fBinary=1 | DTR_CONTROL_ENABLE(1<<4) | RTS_CONTROL_ENABLE(1<<12)
        dcb.fBitfield = 0x01 | (1 << 4) | ((1 << 12) if rts_on else 0)
        dcb.ByteSize, dcb.Parity, dcb.StopBits = 8, 0, 0   # 8-N-1
        return kernel32.SetCommState(self._h, ctypes.byref(dcb))

    def _reader(self):
        buf = ctypes.create_string_buffer(8192)
        n = wintypes.DWORD(0)
        while self.is_open and self._h:
            try:
                ok = kernel32.ReadFile(self._h, buf, 8192, ctypes.byref(n), None)
                if not ok:
                    self._fail(f"串口读错误 WinError {ctypes.get_last_error()}")
                    return
                if n.value:
                    self.rx_queue.put(buf.raw[: n.value])
                else:
                    time.sleep(0.008)
            except Exception as e:                      # 端口拔出等
                self._fail(str(e))
                return

    def _fail(self, msg):
        self.is_open = False
        if self.on_error:
            try:
                self.on_error(msg)
            except Exception:
                pass

    # ---------------- 公共 API ----------------
    def write(self, data: bytes):
        if not self.is_open:
            raise IOError("串口未打开")
        with self._wlock:
            n = wintypes.DWORD(0)
            ok = kernel32.WriteFile(self._h, data, len(data), ctypes.byref(n), None)
            if not ok:
                raise OSError(ctypes.get_last_error(), "串口写失败")
            return n.value

    def rts_pulse(self, times=3, low_ms=150, high_ms=250):
        """RTS 拉低再拉高 → 接到模块 CTS 上即产生上升沿,唤醒休眠串口"""
        for _ in range(times):
            self._apply_dcb(self.baud, rts_on=False)
            time.sleep(low_ms / 1000)
            self._apply_dcb(self.baud, rts_on=True)
            time.sleep(high_ms / 1000)

    def purge(self):
        if self.is_open:
            kernel32.PurgeComm(self._h, PURGE_RXCLEAR | PURGE_TXCLEAR |
                               PURGE_RXABORT | PURGE_TXABORT)

    def close(self):
        self.is_open = False
        if self._h:
            kernel32.CloseHandle(self._h)
            self._h = None


def selftest():
    """无 UI 自检:枚举端口 + (COM3 若在) 打开 AT 握手"""
    print("串口枚举:", list_serial_ports())
    if "COM3" in list_serial_ports():
        sp = SerialPort("COM3")
        sp.write(b"AT\r\n")
        time.sleep(0.3)
        got = bytearray()
        while not sp.rx_queue.empty():
            got += sp.rx_queue.get()
        print("AT 握手应答:", bytes(got))
        sp.close()
        print("selftest OK")


if __name__ == "__main__":
    selftest()
