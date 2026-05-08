#!/usr/bin/env python3
"""
PM3 Clone Assistant - Android Native App
KivyMD-based standalone Android APK for Proxmark3
"""
__version__ = "1.0.0"

import os
import sys
import time
import threading
import subprocess
import re
import json
from queue import Queue, Empty

# ── Platform detection ─────────────────────────────────────────────────────────
try:
    import android  # noqa
    from android.permissions import request_permissions, Permission, check_permission
    ANDROID = True
    APP_DIR = os.environ.get('ANDROID_PRIVATE', '/data/data/com.pm3clone/files')
except ImportError:
    ANDROID = False
    APP_DIR = os.path.dirname(os.path.abspath(__file__))

PM3_BINARY = os.path.join(APP_DIR, 'proxmark3')

# ── Kivy setup ─────────────────────────────────────────────────────────────────
os.environ.setdefault('KIVY_NO_ENV_CONFIG', '1')

from kivy.app import App  # noqa
from kivy.clock import Clock, mainthread  # noqa
from kivy.metrics import dp, sp  # noqa
from kivy.properties import StringProperty, BooleanProperty, ListProperty  # noqa
from kivy.core.window import Window  # noqa
from kivy.uix.screenmanager import ScreenManager, Screen, SlideTransition  # noqa

from kivymd.app import MDApp  # noqa
from kivymd.uix.screen import MDScreen  # noqa
from kivymd.uix.button import MDRaisedButton, MDFlatButton, MDIconButton  # noqa
from kivymd.uix.label import MDLabel  # noqa
from kivymd.uix.card import MDCard  # noqa
from kivymd.uix.boxlayout import MDBoxLayout  # noqa
from kivymd.uix.scrollview import MDScrollView  # noqa
from kivymd.uix.dialog import MDDialog  # noqa
from kivymd.uix.snackbar import Snackbar  # noqa
from kivymd.uix.progressbar import MDProgressBar  # noqa
from kivymd.uix.toolbar import MDTopAppBar  # noqa
from kivymd.uix.spinner import MDSpinner  # noqa
from kivy.uix.boxlayout import BoxLayout  # noqa
from kivy.uix.gridlayout import GridLayout  # noqa
from kivy.uix.scrollview import ScrollView  # noqa
from kivy.uix.label import Label  # noqa
from kivy.graphics import Color, RoundedRectangle  # noqa

# ── Constants ──────────────────────────────────────────────────────────────────
JAKCOM_PASSWORDS = ["5469616E", "51243648", "0000000B", "00000000"]

HID_BLOCKS_DEFAULT = {
    0: "00107060",
    1: "1D555956",
    2: "9595566A",
    3: "6669A6A6",
}

# ── Colors ─────────────────────────────────────────────────────────────────────
C_BG      = (0.039, 0.039, 0.039, 1)   # #0a0a0a
C_CARD    = (0.102, 0.102, 0.102, 1)   # #1a1a1a
C_ACCENT  = (0.0,   1.0,   0.533, 1)   # #00ff88
C_TEXT    = (0.878, 0.878, 0.878, 1)   # #e0e0e0
C_DIM     = (0.502, 0.502, 0.502, 1)   # #808080
C_WARN    = (1.0,   0.42,  0.0,   1)   # #ff6b00
C_ERR     = (1.0,   0.22,  0.22,  1)   # #ff3838

# ── PM3 Controller ─────────────────────────────────────────────────────────────
class PM3Controller:
    """Manages PM3 connection and command execution."""

    def __init__(self):
        self.port = None
        self.connected = False
        self.bridge = None  # USBBridge on Android
        self._lock = threading.Lock()

    # ── Connection ─────────────────────────────────────────────────────────────
    def find_and_connect(self):
        """Find PM3 and connect. Returns (success, message)."""
        if ANDROID:
            return self._connect_android()
        else:
            return self._connect_desktop()

    def _connect_desktop(self):
        import serial.tools.list_ports
        candidates = []
        for p in serial.tools.list_ports.comports():
            name = p.device.lower()
            if 'usbmodem' in name or 'ttyacm' in name or 'com' in name.upper():
                candidates.append(p.device)
        if not candidates:
            return False, "PM3 not found. Check USB connection."
        self.port = candidates[0]
        self.connected = True
        return True, f"Connected: {self.port}"

    def _connect_android(self):
        try:
            from usb_bridge import USBBridge
            self.bridge = USBBridge()
            device = self.bridge.find_pm3()
            if not device:
                return False, "PM3 not found. Check USB-OTG cable."
            if not self.bridge.has_permission(device):
                self.bridge.request_permission(device)
                time.sleep(2)  # wait for user to grant
                if not self.bridge.has_permission(device):
                    return False, "USB permission denied."
            ok, slave_path = self.bridge.open_connection(device)
            if not ok:
                return False, "Failed to open USB connection."
            self.port = slave_path
            self.connected = True
            return True, "Connected via USB-OTG"
        except Exception as e:
            return False, f"Connection error: {e}"

    def disconnect(self):
        self.connected = False
        self.port = None
        if self.bridge:
            self.bridge.close()
            self.bridge = None

    # ── Command execution ──────────────────────────────────────────────────────
    def run(self, cmd, timeout=30):
        """Run a PM3 command. Returns (success, output)."""
        if not self.connected or not self.port:
            return False, "Not connected"
        try:
            if not os.path.exists(PM3_BINARY):
                return False, f"PM3 binary missing: {PM3_BINARY}"
            full_cmd = [PM3_BINARY, '--port', self.port, '--no-color', '-c', cmd]
            result = subprocess.run(
                full_cmd,
                capture_output=True, text=True, timeout=timeout
            )
            output = result.stdout + result.stderr
            return True, output
        except subprocess.TimeoutExpired:
            return False, "Command timed out"
        except Exception as e:
            return False, f"Error: {e}"

    def run_stream(self, cmd, callback, timeout=60):
        """Run a PM3 command streaming output line by line."""
        def _run():
            if not self.connected or not self.port:
                callback("[ERROR] Not connected\n", done=True)
                return
            try:
                full_cmd = [PM3_BINARY, '--port', self.port, '--no-color', '-c', cmd]
                proc = subprocess.Popen(
                    full_cmd,
                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                    text=True
                )
                for line in iter(proc.stdout.readline, ''):
                    callback(line, done=False)
                proc.wait()
                callback("", done=True)
            except Exception as e:
                callback(f"[ERROR] {e}\n", done=True)
        threading.Thread(target=_run, daemon=True).start()

    # ── Operations ─────────────────────────────────────────────────────────────
    def hw_status(self, cb): self.run_stream('hw status', cb)
    def lf_search(self, cb): self.run_stream('lf search', cb, timeout=15)
    def hf_search(self, cb): self.run_stream('hf search', cb, timeout=15)

    def read_hid(self, cb): self.run_stream('lf hid reader', cb, timeout=15)
    def read_em(self, cb):  self.run_stream('lf em 410x reader', cb, timeout=15)

    def clone_hid(self, raw, cb):
        cmd = f'lf hid clone -r {raw}'
        self.run_stream(cmd, cb)

    def wipe_t5577(self, cb): self.run_stream('lf t55xx wipe', cb)

    def write_block(self, blk, data, pwd, cb):
        cmd = f'lf t55xx write -b {blk} -d {data} -p {pwd}'
        self.run_stream(cmd, cb)

    def ring_clone_hid(self, blocks, cb):
        """Write HID blocks to JAKCOM ring (tries all passwords)."""
        def _work():
            for pwd in JAKCOM_PASSWORDS:
                for blk, data in blocks.items():
                    cb(f"[RING] pwd={pwd} b{blk}={data}\n", done=False)
                    for mode in ['--r0', '--r1']:
                        cmd = f'lf t55xx write -b {blk} -d {data} -p {pwd} {mode}'
                        ok, out = self.run(cmd, timeout=10)
                        cb(out, done=False)
                        time.sleep(0.3)
            cb("", done=True)
        threading.Thread(target=_work, daemon=True).start()

    def detect_t5577(self, cb): self.run_stream('lf t55xx detect', cb)
    def read_mifare(self, cb):  self.run_stream('hf mf reader', cb, timeout=30)


# ── Shared PM3 instance ────────────────────────────────────────────────────────
pm3 = PM3Controller()


# ══════════════════════════════════════════════════════════════════════════════
#  UI HELPERS
# ══════════════════════════════════════════════════════════════════════════════

def dark_card(**kwargs):
    card = MDCard(
        md_bg_color=C_CARD,
        radius=[dp(12)],
        padding=dp(16),
        elevation=4,
        **kwargs
    )
    return card


def accent_btn(text, on_press=None, **kwargs):
    btn = MDRaisedButton(
        text=text,
        md_bg_color=C_ACCENT,
        theme_text_color="Custom",
        text_color=(0, 0, 0, 1),
        font_size=sp(15),
        size_hint_y=None,
        height=dp(50),
        **kwargs
    )
    if on_press:
        btn.bind(on_press=on_press)
    return btn


def warn_btn(text, on_press=None, **kwargs):
    btn = MDRaisedButton(
        text=text,
        md_bg_color=C_WARN,
        theme_text_color="Custom",
        text_color=(1, 1, 1, 1),
        font_size=sp(15),
        size_hint_y=None,
        height=dp(50),
        **kwargs
    )
    if on_press:
        btn.bind(on_press=on_press)
    return btn


def section_label(text, **kwargs):
    return MDLabel(
        text=text,
        theme_text_color="Custom",
        text_color=C_ACCENT,
        font_style="H6",
        size_hint_y=None,
        height=dp(36),
        **kwargs
    )


def body_label(text, **kwargs):
    return MDLabel(
        text=text,
        theme_text_color="Custom",
        text_color=C_TEXT,
        font_style="Body1",
        size_hint_y=None,
        height=dp(28),
        **kwargs
    )


# ══════════════════════════════════════════════════════════════════════════════
#  LOG WIDGET  (shared)
# ══════════════════════════════════════════════════════════════════════════════

class LogBox(BoxLayout):
    """Scrollable dark log output box."""

    def __init__(self, **kwargs):
        super().__init__(orientation='vertical', **kwargs)
        self.scroll = ScrollView(size_hint=(1, 1))
        self.label = Label(
            text='',
            size_hint_y=None,
            text_size=(None, None),
            font_size=sp(11),
            font_name='RobotoMono',
            color=C_TEXT,
            markup=True,
            halign='left',
            valign='top',
            padding=(dp(8), dp(8)),
        )
        self.label.bind(texture_size=lambda inst, v: setattr(inst, 'height', v[1]))
        self.label.bind(width=lambda inst, v: setattr(inst, 'text_size', (v, None)))
        self.scroll.add_widget(self.label)
        self.add_widget(self.scroll)

    @mainthread
    def append(self, text):
        self.label.text += text
        Clock.schedule_once(lambda dt: self.scroll.scroll_y.__setattr__('', 0), 0.1)

    def clear(self):
        self.label.text = ''


# ══════════════════════════════════════════════════════════════════════════════
#  SCREENS
# ══════════════════════════════════════════════════════════════════════════════

class ConnectScreen(MDScreen):
    status_text = StringProperty("Plug PM3 via USB-OTG cable, then tap Connect.")

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.name = 'connect'
        self._build_ui()

    def _build_ui(self):
        root = MDBoxLayout(
            orientation='vertical',
            padding=dp(24),
            spacing=dp(16),
            md_bg_color=C_BG,
        )

        # Title
        root.add_widget(MDLabel(
            text="PM3 Clone Assistant",
            theme_text_color="Custom",
            text_color=C_ACCENT,
            font_style="H4",
            halign='center',
            size_hint_y=None,
            height=dp(60),
        ))

        root.add_widget(MDLabel(
            text="Proxmark3 • HID Prox • JAKCOM Ring",
            theme_text_color="Custom",
            text_color=C_DIM,
            font_style="Body2",
            halign='center',
            size_hint_y=None,
            height=dp(28),
        ))

        # Status card
        self.status_card = dark_card(size_hint_y=None, height=dp(80))
        self.status_lbl = MDLabel(
            text=self.status_text,
            theme_text_color="Custom",
            text_color=C_DIM,
            halign='center',
            font_style="Body1",
        )
        self.status_card.add_widget(self.status_lbl)
        root.add_widget(self.status_card)

        # Spinner (hidden)
        self.spinner = MDSpinner(
            size_hint=(None, None),
            size=(dp(40), dp(40)),
            pos_hint={'center_x': 0.5},
            active=False,
            color=C_ACCENT,
        )
        root.add_widget(self.spinner)

        # Connect button
        self.connect_btn = accent_btn("  CONNECT TO PM3  ", on_press=self.do_connect)
        root.add_widget(self.connect_btn)

        # Divider
        root.add_widget(MDLabel(size_hint_y=None, height=dp(12)))

        # Instructions card
        inst = dark_card(size_hint_y=None, height=dp(180))
        inst_box = MDBoxLayout(orientation='vertical', spacing=dp(6))
        inst_box.add_widget(section_label("SETUP"))
        steps = [
            "1. Android phone → USB-OTG adapter → PM3",
            "2. Grant USB permission when prompted",
            "3. Tap CONNECT",
            "4. Place card on PM3 antenna",
            "5. Follow steps in the app",
        ]
        for s in steps:
            inst_box.add_widget(body_label(s))
        inst.add_widget(inst_box)
        root.add_widget(inst)

        self.add_widget(root)

    def do_connect(self, *_):
        self.spinner.active = True
        self.connect_btn.disabled = True
        self.status_lbl.text = "Connecting..."
        self.status_lbl.text_color = C_DIM

        def _connect():
            ok, msg = pm3.find_and_connect()
            Clock.schedule_once(lambda dt: self._on_connect_result(ok, msg))

        threading.Thread(target=_connect, daemon=True).start()

    @mainthread
    def _on_connect_result(self, ok, msg):
        self.spinner.active = False
        self.connect_btn.disabled = False
        if ok:
            self.status_lbl.text = f"✓ {msg}"
            self.status_lbl.text_color = C_ACCENT
            Clock.schedule_once(lambda dt: self.go_home(), 1.0)
        else:
            self.status_lbl.text = f"✗ {msg}"
            self.status_lbl.text_color = C_ERR

    def go_home(self):
        self.manager.transition = SlideTransition(direction='left')
        self.manager.current = 'home'


# ─────────────────────────────────────────────────────────────────────────────

class HomeScreen(MDScreen):

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.name = 'home'
        self._build_ui()

    def _build_ui(self):
        root = MDBoxLayout(
            orientation='vertical',
            spacing=0,
            md_bg_color=C_BG,
        )

        # Toolbar
        bar = MDTopAppBar(
            title="PM3 Clone Assistant",
            md_bg_color=C_CARD,
            specific_text_color=C_ACCENT,
            right_action_items=[["power", lambda x: self.disconnect()]],
        )
        root.add_widget(bar)

        # Content
        scroll = ScrollView(size_hint=(1, 1))
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(20),
            spacing=dp(14),
            size_hint_y=None,
        )
        content.bind(minimum_height=content.setter('height'))

        # Status indicator
        self.conn_lbl = MDLabel(
            text="● Connected",
            theme_text_color="Custom",
            text_color=C_ACCENT,
            font_style="Body1",
            size_hint_y=None,
            height=dp(32),
        )
        content.add_widget(self.conn_lbl)

        # ── Step guide ────────────────────────────────────────────────────────
        content.add_widget(section_label("WIZARD — CLONE ACCESS CARD"))

        steps = [
            ("1. Auto Detect",   "Scan what card type is on PM3",    'scan'),
            ("2. Clone → T5577", "Write card data to blank T5577",   'clone'),
            ("3. Ring Clone",    "Write to JAKCOM R5 ring (T5577)",  'ring'),
            ("4. Verify",        "Re-read the cloned card/ring",     'verify'),
        ]
        for title, desc, screen in steps:
            card = dark_card(size_hint_y=None, height=dp(80))
            row = MDBoxLayout(orientation='horizontal', spacing=dp(12))
            info = MDBoxLayout(orientation='vertical')
            info.add_widget(MDLabel(
                text=title,
                theme_text_color="Custom",
                text_color=C_TEXT,
                font_style="Subtitle1",
                size_hint_y=None, height=dp(28),
            ))
            info.add_widget(MDLabel(
                text=desc,
                theme_text_color="Custom",
                text_color=C_DIM,
                font_style="Body2",
                size_hint_y=None, height=dp(24),
            ))
            row.add_widget(info)
            btn = MDIconButton(
                icon="arrow-right-circle",
                theme_icon_color="Custom",
                icon_color=C_ACCENT,
                size_hint=(None, None),
                size=(dp(48), dp(48)),
            )
            target = screen
            btn.bind(on_press=lambda x, t=target: self.go(t))
            row.add_widget(btn)
            card.add_widget(row)
            content.add_widget(card)

        # ── Quick actions ──────────────────────────────────────────────────────
        content.add_widget(section_label("QUICK ACTIONS"))
        grid = GridLayout(
            cols=2, spacing=dp(12),
            size_hint_y=None, height=dp(120),
        )
        actions = [
            ("LF Search",  lambda x: self.go('scan')),
            ("HF Search",  lambda x: self.go('hf_scan')),
            ("Wipe T5577", lambda x: self.go('wipe')),
            ("Full Log",   lambda x: self.go('log')),
        ]
        for label, fn in actions:
            b = MDRaisedButton(
                text=label,
                md_bg_color=C_CARD,
                theme_text_color="Custom",
                text_color=C_ACCENT,
                size_hint_y=None, height=dp(50),
            )
            b.bind(on_press=fn)
            grid.add_widget(b)
        content.add_widget(grid)

        scroll.add_widget(content)
        root.add_widget(scroll)
        self.add_widget(root)

    def go(self, screen_name):
        self.manager.transition = SlideTransition(direction='left')
        self.manager.current = screen_name

    def disconnect(self):
        pm3.disconnect()
        self.manager.transition = SlideTransition(direction='right')
        self.manager.current = 'connect'


# ─────────────────────────────────────────────────────────────────────────────

class _LogScreen(MDScreen):
    """Base screen with a log box and toolbar."""

    title = "Log"
    back_screen = 'home'

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self._build_base()
        self._build_content()

    def _build_base(self):
        self.root_box = MDBoxLayout(
            orientation='vertical',
            md_bg_color=C_BG,
        )
        bar = MDTopAppBar(
            title=self.title,
            md_bg_color=C_CARD,
            specific_text_color=C_ACCENT,
            left_action_items=[["arrow-left", lambda x: self.go_back()]],
        )
        self.root_box.add_widget(bar)
        self.add_widget(self.root_box)

    def _build_content(self):
        pass  # override in subclass

    def go_back(self):
        self.manager.transition = SlideTransition(direction='right')
        self.manager.current = self.back_screen

    @mainthread
    def log(self, text, done=False):
        if hasattr(self, 'log_box'):
            self.log_box.append(text)


# ─────────────────────────────────────────────────────────────────────────────

class ScanScreen(_LogScreen):
    title = "Auto Detect Card"
    name = 'scan'

    def _build_content(self):
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(16),
            spacing=dp(12),
        )

        # Card info area
        self.card_card = dark_card(size_hint_y=None, height=dp(120))
        self.card_box = MDBoxLayout(orientation='vertical')
        self.card_type_lbl = MDLabel(
            text="No card detected",
            theme_text_color="Custom",
            text_color=C_DIM,
            font_style="H6",
            size_hint_y=None, height=dp(36),
        )
        self.card_raw_lbl = MDLabel(
            text="Place card on PM3 antenna",
            theme_text_color="Custom",
            text_color=C_DIM,
            font_style="Body2",
            size_hint_y=None, height=dp(28),
        )
        self.card_box.add_widget(self.card_type_lbl)
        self.card_box.add_widget(self.card_raw_lbl)
        self.card_card.add_widget(self.card_box)
        content.add_widget(self.card_card)

        # Buttons
        btn_row = MDBoxLayout(
            orientation='horizontal',
            spacing=dp(12),
            size_hint_y=None, height=dp(50),
        )
        self.lf_btn = accent_btn("LF Search", on_press=self.do_lf)
        self.hf_btn = MDRaisedButton(
            text="HF Search",
            md_bg_color=C_CARD,
            theme_text_color="Custom",
            text_color=C_ACCENT,
            size_hint_y=None, height=dp(50),
        )
        self.hf_btn.bind(on_press=self.do_hf)
        btn_row.add_widget(self.lf_btn)
        btn_row.add_widget(self.hf_btn)
        content.add_widget(btn_row)

        # Spinner
        self.spinner = MDSpinner(
            size_hint=(None, None), size=(dp(36), dp(36)),
            pos_hint={'center_x': 0.5},
            active=False, color=C_ACCENT,
        )
        content.add_widget(self.spinner)

        # Next step
        self.clone_btn = MDRaisedButton(
            text="→ Clone to T5577",
            md_bg_color=C_CARD,
            theme_text_color="Custom",
            text_color=C_ACCENT,
            size_hint_y=None, height=dp(48),
            disabled=True,
        )
        self.clone_btn.bind(on_press=lambda x: self.go_to_clone())
        content.add_widget(self.clone_btn)

        # Log
        content.add_widget(section_label("OUTPUT"))
        self.log_box = LogBox(size_hint=(1, 1))
        content.add_widget(self.log_box)

        self.root_box.add_widget(content)
        self.detected_raw = None
        self.detected_type = None

    def do_lf(self, *_):
        self.log_box.clear()
        self.spinner.active = True
        self.lf_btn.disabled = True
        self.detected_raw = None
        pm3.lf_search(self._parse_lf_output)

    def do_hf(self, *_):
        self.log_box.clear()
        self.spinner.active = True
        self.hf_btn.disabled = True
        pm3.hf_search(self._on_log_done)

    @mainthread
    def _parse_lf_output(self, text, done=False):
        self.log_box.append(text)
        # Parse HID raw
        m = re.search(r'HID Prox.*?Raw:\s*([0-9a-fA-F]+)', text, re.IGNORECASE)
        if m:
            self.detected_raw = m.group(1)
            self.detected_type = 'HID Prox'
        m2 = re.search(r'EM 410x.*?ID:\s*([0-9a-fA-F]+)', text, re.IGNORECASE)
        if m2 and not self.detected_raw:
            self.detected_raw = m2.group(1)
            self.detected_type = 'EM410x'
        if done:
            self.spinner.active = False
            self.lf_btn.disabled = False
            if self.detected_type:
                self.card_type_lbl.text = f"✓ {self.detected_type}"
                self.card_type_lbl.text_color = C_ACCENT
                self.card_raw_lbl.text = f"Raw: {self.detected_raw}"
                self.card_raw_lbl.text_color = C_TEXT
                self.clone_btn.disabled = False
                App.get_running_app().last_raw = self.detected_raw
                App.get_running_app().last_type = self.detected_type
            else:
                self.card_type_lbl.text = "Not detected"
                self.card_type_lbl.text_color = C_ERR

    @mainthread
    def _on_log_done(self, text, done=False):
        self.log_box.append(text)
        if done:
            self.spinner.active = False
            self.hf_btn.disabled = False

    def go_to_clone(self):
        self.manager.transition = SlideTransition(direction='left')
        self.manager.current = 'clone'


# ─────────────────────────────────────────────────────────────────────────────

class CloneScreen(_LogScreen):
    title = "Clone → T5577"
    name = 'clone'

    def _build_content(self):
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(16),
            spacing=dp(12),
        )

        # Card info
        self.card_info = dark_card(size_hint_y=None, height=dp(80))
        self.type_lbl = MDLabel(
            text="Card: (not scanned)",
            theme_text_color="Custom",
            text_color=C_DIM,
            font_style="Subtitle1",
        )
        self.raw_lbl = MDLabel(
            text="Go to Scan first",
            theme_text_color="Custom",
            text_color=C_DIM,
            font_style="Body2",
        )
        box = MDBoxLayout(orientation='vertical')
        box.add_widget(self.type_lbl)
        box.add_widget(self.raw_lbl)
        self.card_info.add_widget(box)
        content.add_widget(self.card_info)

        # Instructions
        inst = dark_card(size_hint_y=None, height=dp(100))
        inst_box = MDBoxLayout(orientation='vertical', spacing=dp(4))
        for t in [
            "1. Scan original card first (← Scan)",
            "2. Remove original card",
            "3. Place blank T5577 card on PM3",
            "4. Tap WIPE + CLONE",
        ]:
            inst_box.add_widget(body_label(t))
        inst.add_widget(inst_box)
        content.add_widget(inst)

        # Spinner
        self.spinner = MDSpinner(
            size_hint=(None, None), size=(dp(36), dp(36)),
            pos_hint={'center_x': 0.5},
            active=False, color=C_ACCENT,
        )
        content.add_widget(self.spinner)

        # Buttons
        self.wipe_clone_btn = accent_btn(
            "WIPE + CLONE", on_press=self.do_clone
        )
        content.add_widget(self.wipe_clone_btn)

        verify_btn = MDRaisedButton(
            text="Verify (Re-read)",
            md_bg_color=C_CARD,
            theme_text_color="Custom",
            text_color=C_ACCENT,
            size_hint_y=None, height=dp(48),
        )
        verify_btn.bind(on_press=self.do_verify)
        content.add_widget(verify_btn)

        ring_btn = warn_btn("→ Ring Clone (JAKCOM)", on_press=self.go_ring)
        content.add_widget(ring_btn)

        # Log
        content.add_widget(section_label("OUTPUT"))
        self.log_box = LogBox(size_hint=(1, 1))
        content.add_widget(self.log_box)

        self.root_box.add_widget(content)

    def on_enter(self):
        app = App.get_running_app()
        raw = getattr(app, 'last_raw', None)
        card_type = getattr(app, 'last_type', None)
        if raw and card_type:
            self.type_lbl.text = f"Card: {card_type}"
            self.type_lbl.text_color = C_ACCENT
            self.raw_lbl.text = f"Raw: {raw}"
            self.raw_lbl.text_color = C_TEXT

    def do_clone(self, *_):
        app = App.get_running_app()
        raw = getattr(app, 'last_raw', None)
        if not raw:
            Snackbar(text="Scan a card first!").open()
            return
        self.log_box.clear()
        self.spinner.active = True
        self.wipe_clone_btn.disabled = True

        def _work():
            # Wipe
            ok, out = pm3.run('lf t55xx wipe', timeout=15)
            Clock.schedule_once(lambda dt: self.log(out + "\n"))
            time.sleep(1)
            # Clone
            ok2, out2 = pm3.run(f'lf hid clone -r {raw}', timeout=15)
            Clock.schedule_once(lambda dt: self.log(out2 + "\n", done=True))

        threading.Thread(target=_work, daemon=True).start()

    def do_verify(self, *_):
        self.log_box.clear()
        self.spinner.active = True
        pm3.lf_search(self._on_verify_done)

    @mainthread
    def _on_verify_done(self, text, done=False):
        self.log(text)
        if done:
            self.spinner.active = False

    @mainthread
    def log(self, text, done=False):
        self.log_box.append(text)
        if done:
            self.spinner.active = False
            self.wipe_clone_btn.disabled = False

    def go_ring(self, *_):
        self.manager.transition = SlideTransition(direction='left')
        self.manager.current = 'ring'


# ─────────────────────────────────────────────────────────────────────────────

class RingScreen(_LogScreen):
    title = "JAKCOM Ring Clone"
    name = 'ring'

    def _build_content(self):
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(16),
            spacing=dp(12),
        )

        # Header
        header = dark_card(size_hint_y=None, height=dp(100))
        h_box = MDBoxLayout(orientation='vertical', spacing=dp(4))
        h_box.add_widget(MDLabel(
            text="JAKCOM R5 Smart Ring",
            theme_text_color="Custom",
            text_color=C_ACCENT,
            font_style="Subtitle1",
            size_hint_y=None, height=dp(30),
        ))
        for t in [
            "Ring has 2× T5577 chips — password protected",
            "Passwords tried: 5469616E, 51243648, 0000000B, 00000000",
        ]:
            h_box.add_widget(MDLabel(
                text=t,
                theme_text_color="Custom",
                text_color=C_DIM,
                font_style="Body2",
                size_hint_y=None, height=dp(24),
            ))
        header.add_widget(h_box)
        content.add_widget(header)

        # Steps
        steps = dark_card(size_hint_y=None, height=dp(120))
        s_box = MDBoxLayout(orientation='vertical', spacing=dp(4))
        for t in [
            "1. Scan original card first (← Scan)",
            "2. Place JAKCOM ring flat on PM3 antenna",
            "3. Keep ring VERY STILL during clone",
            "4. Tap RING CLONE — wait 30 seconds",
        ]:
            s_box.add_widget(body_label(t))
        steps.add_widget(s_box)
        content.add_widget(steps)

        # Spinner
        self.spinner = MDSpinner(
            size_hint=(None, None), size=(dp(36), dp(36)),
            pos_hint={'center_x': 0.5},
            active=False, color=C_ACCENT,
        )
        content.add_widget(self.spinner)

        # Buttons
        self.ring_btn = accent_btn("RING CLONE", on_press=self.do_ring_clone)
        content.add_widget(self.ring_btn)

        self.recover_btn = warn_btn("Ring Recover (unbrick)", on_press=self.do_recover)
        content.add_widget(self.recover_btn)

        # Log
        content.add_widget(section_label("OUTPUT"))
        self.log_box = LogBox(size_hint=(1, 1))
        content.add_widget(self.log_box)

        self.root_box.add_widget(content)

    def do_ring_clone(self, *_):
        app = App.get_running_app()
        raw = getattr(app, 'last_raw', None)
        card_type = getattr(app, 'last_type', None)
        if not raw:
            Snackbar(text="Scan original card first!").open()
            return
        self.log_box.clear()
        self.spinner.active = True
        self.ring_btn.disabled = True
        self.log_box.append(f"[START] Ring clone — {card_type} raw={raw}\n")

        # Use default blocks for HID, or detect
        blocks = HID_BLOCKS_DEFAULT.copy()

        def _on_done(text, done=False):
            Clock.schedule_once(lambda dt: self._on_ring_log(text, done))

        pm3.ring_clone_hid(blocks, _on_done)

    @mainthread
    def _on_ring_log(self, text, done=False):
        self.log_box.append(text)
        if done:
            self.spinner.active = False
            self.ring_btn.disabled = False
            self.log_box.append("\n[DONE] Test ring at reader.\n")

    def do_recover(self, *_):
        self.log_box.clear()
        self.spinner.active = True
        self.recover_btn.disabled = True

        def _work():
            for pwd in JAKCOM_PASSWORDS:
                ok, out = pm3.run(f'lf t55xx wipe -p {pwd}', timeout=15)
                Clock.schedule_once(lambda dt, o=out: self.log_box.append(o + "\n"))
                time.sleep(0.5)
            Clock.schedule_once(lambda dt: self._recover_done())

        threading.Thread(target=_work, daemon=True).start()

    @mainthread
    def _recover_done(self):
        self.spinner.active = False
        self.recover_btn.disabled = False
        self.log_box.append("\n[DONE] Recover complete.\n")


# ─────────────────────────────────────────────────────────────────────────────

class VerifyScreen(_LogScreen):
    title = "Verify Clone"
    name = 'verify'

    def _build_content(self):
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(16),
            spacing=dp(12),
        )

        content.add_widget(body_label("Re-reads the cloned card/ring to confirm success."))

        self.spinner = MDSpinner(
            size_hint=(None, None), size=(dp(36), dp(36)),
            pos_hint={'center_x': 0.5},
            active=False, color=C_ACCENT,
        )
        content.add_widget(self.spinner)

        row = MDBoxLayout(
            orientation='horizontal', spacing=dp(12),
            size_hint_y=None, height=dp(50),
        )
        lf_btn = accent_btn("LF Verify", on_press=self.do_lf)
        hf_btn = MDRaisedButton(
            text="HF Verify",
            md_bg_color=C_CARD,
            theme_text_color="Custom",
            text_color=C_ACCENT,
            size_hint_y=None, height=dp(50),
        )
        hf_btn.bind(on_press=self.do_hf)
        row.add_widget(lf_btn)
        row.add_widget(hf_btn)
        content.add_widget(row)

        self.result_lbl = MDLabel(
            text="",
            theme_text_color="Custom",
            text_color=C_DIM,
            font_style="H6",
            halign='center',
            size_hint_y=None, height=dp(40),
        )
        content.add_widget(self.result_lbl)

        content.add_widget(section_label("OUTPUT"))
        self.log_box = LogBox(size_hint=(1, 1))
        content.add_widget(self.log_box)

        self.root_box.add_widget(content)

    def do_lf(self, *_):
        self.log_box.clear()
        self.result_lbl.text = ""
        self.spinner.active = True
        pm3.lf_search(self._on_done)

    def do_hf(self, *_):
        self.log_box.clear()
        self.result_lbl.text = ""
        self.spinner.active = True
        pm3.hf_search(self._on_done)

    @mainthread
    def _on_done(self, text, done=False):
        self.log_box.append(text)
        if done:
            self.spinner.active = False
            app = App.get_running_app()
            raw = getattr(app, 'last_raw', '')
            if raw and raw.lower() in self.log_box.label.text.lower():
                self.result_lbl.text = "✓ MATCH — Clone successful!"
                self.result_lbl.text_color = C_ACCENT
            elif done:
                self.result_lbl.text = "Read complete — check log."
                self.result_lbl.text_color = C_DIM


# ─────────────────────────────────────────────────────────────────────────────

class WipeScreen(_LogScreen):
    title = "Wipe T5577"
    name = 'wipe'

    def _build_content(self):
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(16),
            spacing=dp(12),
        )

        content.add_widget(body_label("Erase all data from a T5577 chip."))
        content.add_widget(body_label("Place blank/used T5577 on PM3 antenna."))

        self.spinner = MDSpinner(
            size_hint=(None, None), size=(dp(36), dp(36)),
            pos_hint={'center_x': 0.5},
            active=False, color=C_ACCENT,
        )
        content.add_widget(self.spinner)

        self.wipe_btn = warn_btn("WIPE T5577", on_press=self.do_wipe)
        content.add_widget(self.wipe_btn)

        content.add_widget(section_label("OUTPUT"))
        self.log_box = LogBox(size_hint=(1, 1))
        content.add_widget(self.log_box)

        self.root_box.add_widget(content)

    def do_wipe(self, *_):
        self.log_box.clear()
        self.spinner.active = True
        self.wipe_btn.disabled = True
        pm3.wipe_t5577(self._on_done)

    @mainthread
    def _on_done(self, text, done=False):
        self.log_box.append(text)
        if done:
            self.spinner.active = False
            self.wipe_btn.disabled = False


# ─────────────────────────────────────────────────────────────────────────────

class FullLogScreen(_LogScreen):
    title = "Full Log"
    name = 'log'

    def _build_content(self):
        content = MDBoxLayout(
            orientation='vertical',
            padding=dp(16),
            spacing=dp(12),
        )

        btn_row = MDBoxLayout(
            orientation='horizontal', spacing=dp(12),
            size_hint_y=None, height=dp(50),
        )
        hw_btn = accent_btn("HW Status", on_press=self.do_hw)
        clear_btn = MDFlatButton(
            text="Clear",
            theme_text_color="Custom",
            text_color=C_DIM,
        )
        clear_btn.bind(on_press=lambda x: self.log_box.clear())
        btn_row.add_widget(hw_btn)
        btn_row.add_widget(clear_btn)
        content.add_widget(btn_row)

        self.spinner = MDSpinner(
            size_hint=(None, None), size=(dp(36), dp(36)),
            pos_hint={'center_x': 0.5},
            active=False, color=C_ACCENT,
        )
        content.add_widget(self.spinner)

        self.log_box = LogBox(size_hint=(1, 1))
        content.add_widget(self.log_box)

        self.root_box.add_widget(content)

    def do_hw(self, *_):
        self.spinner.active = True
        pm3.hw_status(self._on_done)

    @mainthread
    def _on_done(self, text, done=False):
        self.log_box.append(text)
        if done:
            self.spinner.active = False


# ══════════════════════════════════════════════════════════════════════════════
#  APP
# ══════════════════════════════════════════════════════════════════════════════

class PM3App(MDApp):

    last_raw  = StringProperty("")
    last_type = StringProperty("")

    def build(self):
        self.title = "PM3 Clone Assistant"
        self.theme_cls.theme_style = "Dark"
        self.theme_cls.primary_palette = "Green"
        self.theme_cls.accent_palette  = "LightGreen"

        Window.clearcolor = C_BG

        sm = ScreenManager()
        sm.add_widget(ConnectScreen())
        sm.add_widget(HomeScreen())
        sm.add_widget(ScanScreen())
        sm.add_widget(CloneScreen())
        sm.add_widget(RingScreen())
        sm.add_widget(VerifyScreen())
        sm.add_widget(WipeScreen())
        sm.add_widget(FullLogScreen())

        # Request Android permissions
        if ANDROID:
            request_permissions([
                Permission.INTERNET,
            ])
            self._extract_binary()

        return sm

    def _extract_binary(self):
        """Copy bundled PM3 binary to app storage on first run."""
        dst = PM3_BINARY
        if os.path.exists(dst):
            return
        # Binary is in assets, copy to writable location
        try:
            from android.storage import app_storage_path
            import shutil
            src = os.path.join(os.environ.get('ANDROID_ARGUMENT', ''), 'proxmark3')
            if os.path.exists(src):
                shutil.copy2(src, dst)
                os.chmod(dst, 0o755)
        except Exception as e:
            print(f"[WARN] Binary extract failed: {e}")


if __name__ == '__main__':
    PM3App().run()
