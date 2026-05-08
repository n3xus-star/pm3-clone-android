"""
usb_bridge.py — Android USB Host → PTY bridge for PM3

How it works:
  1. Find PM3 device via Android USB Host API
  2. Open CDC ACM interface (bulk endpoints)
  3. Configure line coding (115200 8N1)
  4. Create PTY pair  →  slave_path given to PM3 binary
  5. Bridge thread: USB bulk transfer ↔ PTY master fd
"""

import os
import pty
import threading
import time

# pyjnius (available inside Kivy Android APK)
from jnius import autoclass, cast

# Android classes
PythonActivity       = autoclass('org.kivy.android.PythonActivity')
UsbManager_cls       = autoclass('android.hardware.usb.UsbManager')
UsbConstants         = autoclass('android.hardware.usb.UsbConstants')
PendingIntent        = autoclass('android.app.PendingIntent')
Intent               = autoclass('android.content.Intent')
Context              = autoclass('android.content.Context')

# Proxmark3 USB identifiers
# Official RDV4 + common clones
PM3_DEVICES = [
    (0x9AC4, 0x4B8F),  # Proxmark3 RDV4
    (0x2D2D, 0x504D),  # Some clones
    (0x1D50, 0x6002),  # OpenMoko / clone variant
    (0x16D0, 0x06D8),  # Another variant
]

# CDC ACM class codes
USB_CLASS_CDC_DATA   = 0x0A
USB_CLASS_CDC        = 0x02

# Control request types
BM_REQUEST_HOST_TO_DEV_CLASS_IFACE = 0x21

# CDC ACM requests
SET_LINE_CODING       = 0x20
SET_CONTROL_LINE_STATE = 0x22
GET_LINE_CODING       = 0x21

# DTR + RTS
CTRL_DTR = 0x01
CTRL_RTS = 0x02


class USBBridge:
    """
    Manages Android USB Host connection to PM3 and exposes a PTY slave path
    that the PM3 binary can use as a serial port.
    """

    def __init__(self):
        self.context     = PythonActivity.mActivity
        self.usb_manager = self.context.getSystemService(Context.USB_SERVICE)
        self.device      = None
        self.connection  = None
        self.in_ep       = None   # bulk IN  endpoint
        self.out_ep      = None   # bulk OUT endpoint
        self.ctrl_iface  = None   # CDC control interface index
        self.data_iface  = None   # CDC data interface
        self.master_fd   = None
        self.slave_path  = None
        self._running    = False
        self._threads    = []

    # ── Device discovery ───────────────────────────────────────────────────────

    def find_pm3(self):
        """Return UsbDevice for PM3, or None."""
        device_list = self.usb_manager.getDeviceList()
        key_set = device_list.keySet()
        for key in key_set.toArray():
            dev = device_list.get(key)
            vid = dev.getVendorId()
            pid = dev.getProductId()
            if (vid, pid) in PM3_DEVICES:
                self.device = dev
                return dev
            # Fallback: any CDC ACM device
            for i in range(dev.getInterfaceCount()):
                iface = dev.getInterface(i)
                if iface.getInterfaceClass() == USB_CLASS_CDC:
                    self.device = dev
                    return dev
        return None

    def has_permission(self, device=None):
        dev = device or self.device
        if dev is None:
            return False
        return self.usb_manager.hasPermission(dev)

    def request_permission(self, device=None):
        dev = device or self.device
        ACTION = "com.pm3clone.USB_PERMISSION"
        pi = PendingIntent.getBroadcast(
            self.context, 0,
            Intent(ACTION),
            PendingIntent.FLAG_IMMUTABLE if hasattr(PendingIntent, 'FLAG_IMMUTABLE') else 0,
        )
        self.usb_manager.requestPermission(dev, pi)

    # ── Connection ─────────────────────────────────────────────────────────────

    def open_connection(self, device=None):
        """
        Open CDC ACM connection.
        Returns (True, slave_pty_path) on success, (False, error_msg) on failure.
        """
        dev = device or self.device
        if dev is None:
            return False, "No device"

        self.connection = self.usb_manager.openDevice(dev)
        if not self.connection:
            return False, "Failed to open USB device"

        # Find CDC control + data interfaces
        ctrl_iface_idx = -1
        data_iface = None
        in_ep  = None
        out_ep = None

        for i in range(dev.getInterfaceCount()):
            iface = dev.getInterface(i)
            cls = iface.getInterfaceClass()

            if cls == USB_CLASS_CDC:
                ctrl_iface_idx = i

            elif cls == USB_CLASS_CDC_DATA:
                data_iface = iface
                for j in range(iface.getEndpointCount()):
                    ep = iface.getEndpoint(j)
                    if ep.getType() == UsbConstants.USB_ENDPOINT_XFER_BULK:
                        if ep.getDirection() == UsbConstants.USB_DIR_IN:
                            in_ep = ep
                        else:
                            out_ep = ep

        if data_iface is None or in_ep is None or out_ep is None:
            # Single-interface CDC (some clones)
            for i in range(dev.getInterfaceCount()):
                iface = dev.getInterface(i)
                for j in range(iface.getEndpointCount()):
                    ep = iface.getEndpoint(j)
                    if ep.getType() == UsbConstants.USB_ENDPOINT_XFER_BULK:
                        if ep.getDirection() == UsbConstants.USB_DIR_IN:
                            in_ep = ep
                            data_iface = iface
                        else:
                            out_ep = ep
                if in_ep and out_ep:
                    break

        if in_ep is None or out_ep is None:
            return False, "CDC bulk endpoints not found"

        # Claim data interface
        self.connection.claimInterface(data_iface, True)
        self.data_iface = data_iface
        self.in_ep  = in_ep
        self.out_ep = out_ep

        # Configure CDC line coding: 115200 8N1
        self._set_line_coding(115200, 0, 0, 8)
        self._set_control_line_state(CTRL_DTR | CTRL_RTS)

        # Create PTY pair
        master_fd, slave_fd = pty.openpty()
        self.master_fd  = master_fd
        self.slave_path = os.ttyname(slave_fd)
        os.close(slave_fd)  # PM3 binary will re-open via slave_path

        # Start bridge threads
        self._running = True
        t1 = threading.Thread(target=self._usb_to_pty, daemon=True)
        t2 = threading.Thread(target=self._pty_to_usb, daemon=True)
        t1.start()
        t2.start()
        self._threads = [t1, t2]

        return True, self.slave_path

    # ── CDC ACM control ────────────────────────────────────────────────────────

    def _set_line_coding(self, baud, stop_bits, parity, data_bits):
        """Send SET_LINE_CODING control request."""
        # Encode baud rate as 4-byte little-endian
        b = bytearray(7)
        b[0] = baud & 0xFF
        b[1] = (baud >> 8) & 0xFF
        b[2] = (baud >> 16) & 0xFF
        b[3] = (baud >> 24) & 0xFF
        b[4] = stop_bits   # 0=1 stop, 1=1.5 stop, 2=2 stop
        b[5] = parity      # 0=none
        b[6] = data_bits   # 8
        self.connection.controlTransfer(
            BM_REQUEST_HOST_TO_DEV_CLASS_IFACE,
            SET_LINE_CODING,
            0, 0, b, 7, 2000
        )

    def _set_control_line_state(self, state):
        """Send SET_CONTROL_LINE_STATE (DTR/RTS)."""
        self.connection.controlTransfer(
            BM_REQUEST_HOST_TO_DEV_CLASS_IFACE,
            SET_CONTROL_LINE_STATE,
            state, 0, None, 0, 2000
        )

    # ── Bridge threads ─────────────────────────────────────────────────────────

    def _usb_to_pty(self):
        """USB IN endpoint → PTY master."""
        buf = bytearray(512)
        while self._running:
            try:
                n = self.connection.bulkTransfer(self.in_ep, buf, len(buf), 100)
                if n > 0:
                    os.write(self.master_fd, bytes(buf[:n]))
            except Exception:
                time.sleep(0.01)

    def _pty_to_usb(self):
        """PTY master → USB OUT endpoint."""
        while self._running:
            try:
                data = os.read(self.master_fd, 512)
                if data:
                    # Split into 64-byte chunks (USB full-speed max packet)
                    for i in range(0, len(data), 64):
                        chunk = data[i:i+64]
                        self.connection.bulkTransfer(
                            self.out_ep, bytearray(chunk), len(chunk), 2000
                        )
            except Exception:
                time.sleep(0.01)

    # ── Cleanup ────────────────────────────────────────────────────────────────

    def close(self):
        self._running = False
        if self.connection:
            try:
                self.connection.releaseInterface(self.data_iface)
                self.connection.close()
            except Exception:
                pass
        if self.master_fd is not None:
            try:
                os.close(self.master_fd)
            except Exception:
                pass
        self.connection = None
        self.master_fd  = None
        self.slave_path = None
