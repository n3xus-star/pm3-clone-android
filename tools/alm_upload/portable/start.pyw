r"""Launcher for the portable Windows build (python\ + app\ folders side by side).

Points Tcl/Tk at the bundled libraries, then starts the GUI. Any start-up
error is written to start_error.log and shown in a message box, because
pythonw.exe has no console to print it to.
"""
import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
LIB = os.path.join(sys.prefix, "Library", "lib")
os.environ.setdefault("TCL_LIBRARY", os.path.join(LIB, "tcl8.6"))
os.environ.setdefault("TK_LIBRARY", os.path.join(LIB, "tk8.6"))
if hasattr(os, "add_dll_directory"):
    os.add_dll_directory(sys.prefix)
sys.path.insert(0, HERE)

try:
    import alm_upload_gui
    alm_upload_gui.main()
except Exception:
    msg = traceback.format_exc()
    try:
        with open(os.path.join(HERE, "start_error.log"), "w", encoding="utf-8") as fh:
            fh.write(msg)
    except OSError:
        pass
    try:
        import ctypes
        ctypes.windll.user32.MessageBoxW(None, msg[-1500:], "ALM Image Uploader - error", 0x10)
    except Exception:
        pass
    raise
