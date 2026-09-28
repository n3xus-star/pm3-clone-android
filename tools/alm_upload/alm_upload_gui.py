#!/usr/bin/env python3
"""
ALM Image Uploader - simple Windows GUI around alm_upload.py.

Uploads screenshots as attachments to the test cases (test instances) of every
test set under one Test Lab folder in HP / Micro Focus / OpenText ALM.

Local images are laid out as:
    <image folder>\\<test set>\\<test case>\\*.png
"Create image folders" builds that layout from ALM so the names always match.

Built into a single portable .exe with PyInstaller
(see .github/workflows/build-alm-uploader.yml).
"""

import argparse
import json
import os
import queue
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

import requests

import alm_upload as alm

try:
    # Use the Windows certificate store, so company (internal CA) HTTPS works
    import truststore
    truststore.inject_into_ssl()
except ImportError:
    pass

APP_NAME = "ALM Image Uploader"
SETTINGS_FILE = "alm_uploader_settings.json"
SAVED_FIELDS = ["url", "domain", "project", "user", "insecure", "lab_folder",
                "folder", "move", "move_to", "watch", "interval"]


def app_dir():
    if getattr(sys, "frozen", False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


def settings_paths():
    """Next to the .exe (portable); %APPDATA% if that folder is read-only."""
    paths = [os.path.join(app_dir(), SETTINGS_FILE)]
    if os.environ.get("APPDATA"):
        paths.append(os.path.join(os.environ["APPDATA"], "ALMImageUploader", SETTINGS_FILE))
    return paths


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_NAME)
        self.geometry("820x760")
        self.minsize(680, 620)
        self.msgq = queue.Queue()
        self.stop_event = threading.Event()
        self.worker = None

        self.v = {
            "url": tk.StringVar(), "domain": tk.StringVar(), "project": tk.StringVar(),
            "user": tk.StringVar(), "password": tk.StringVar(), "insecure": tk.BooleanVar(),
            "lab_folder": tk.StringVar(), "folder": tk.StringVar(),
            "move": tk.BooleanVar(), "move_to": tk.StringVar(),
            "watch": tk.BooleanVar(), "interval": tk.StringVar(value="10"),
        }
        self.summary = tk.StringVar(value="Fill in the fields, then press Check.")
        self._build()
        self._load_settings()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(100, self._drain)

    # ------------------------------------------------------------------ UI
    def _build(self):
        pad = {"padx": 6, "pady": 3}
        root = ttk.Frame(self, padding=8)
        root.pack(fill="both", expand=True)
        root.columnconfigure(0, weight=1)

        conn = ttk.LabelFrame(root, text="1. ALM login", padding=6)
        conn.grid(row=0, column=0, sticky="ew")
        conn.columnconfigure(1, weight=1)
        conn.columnconfigure(3, weight=1)
        ttk.Label(conn, text="ALM URL").grid(row=0, column=0, sticky="w", **pad)
        ttk.Entry(conn, textvariable=self.v["url"]).grid(row=0, column=1, columnspan=3, sticky="ew", **pad)
        ttk.Label(conn, text="Domain").grid(row=1, column=0, sticky="w", **pad)
        ttk.Entry(conn, textvariable=self.v["domain"]).grid(row=1, column=1, sticky="ew", **pad)
        ttk.Label(conn, text="Project").grid(row=1, column=2, sticky="w", **pad)
        ttk.Entry(conn, textvariable=self.v["project"]).grid(row=1, column=3, sticky="ew", **pad)
        ttk.Label(conn, text="Username").grid(row=2, column=0, sticky="w", **pad)
        ttk.Entry(conn, textvariable=self.v["user"]).grid(row=2, column=1, sticky="ew", **pad)
        ttk.Label(conn, text="Password").grid(row=2, column=2, sticky="w", **pad)
        ttk.Entry(conn, textvariable=self.v["password"], show="*").grid(row=2, column=3, sticky="ew", **pad)
        ttk.Checkbutton(conn, text="Skip SSL certificate check (only if the connection fails)",
                        variable=self.v["insecure"]).grid(row=3, column=1, columnspan=3, sticky="w", **pad)

        src = ttk.LabelFrame(root, text="2. Test Lab folder and image folder", padding=6)
        src.grid(row=1, column=0, sticky="ew", pady=(6, 0))
        src.columnconfigure(1, weight=1)
        ttk.Label(src, text="Test Lab folder").grid(row=0, column=0, sticky="w", **pad)
        ttk.Entry(src, textvariable=self.v["lab_folder"]).grid(row=0, column=1, sticky="ew", **pad)
        ttk.Label(src, text="e.g. Root\\Release 1\\Sprint 12  or folder ID",
                  foreground="gray").grid(row=0, column=2, sticky="w", **pad)
        ttk.Label(src, text="Image folder").grid(row=1, column=0, sticky="w", **pad)
        ttk.Entry(src, textvariable=self.v["folder"]).grid(row=1, column=1, sticky="ew", **pad)
        ttk.Button(src, text="Browse...", command=lambda: self._browse("folder")).grid(
            row=1, column=2, sticky="w", **pad)
        ttk.Label(src, text="Layout:  <image folder>\\<test set>\\<test case>\\screenshot.png",
                  foreground="gray").grid(row=2, column=1, columnspan=2, sticky="w", **pad)
        row = ttk.Frame(src)
        row.grid(row=3, column=1, columnspan=2, sticky="w", **pad)
        self.btn_check = ttk.Button(row, text="Check (preview, no upload)", command=self._on_check)
        self.btn_check.pack(side="left", padx=(0, 6))
        self.btn_make = ttk.Button(row, text="Create image folders", command=self._on_make)
        self.btn_make.pack(side="left")

        plan = ttk.LabelFrame(root, text="3. Test sets and test cases", padding=6)
        plan.grid(row=2, column=0, sticky="nsew", pady=(6, 0))
        plan.columnconfigure(0, weight=1)
        plan.rowconfigure(1, weight=1)
        ttk.Label(plan, textvariable=self.summary).grid(row=0, column=0, columnspan=2, sticky="w", **pad)
        self.tree = ttk.Treeview(plan, columns=("images", "status"), height=10)
        self.tree.heading("#0", text="Test set / test case")
        self.tree.heading("images", text="Images")
        self.tree.heading("status", text="Status")
        self.tree.column("#0", width=360)
        self.tree.column("images", width=60, anchor="center", stretch=False)
        self.tree.column("status", width=300)
        self.tree.tag_configure("warn", foreground="#b36b00")
        self.tree.tag_configure("bad", foreground="#c00000")
        sb = ttk.Scrollbar(plan, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=sb.set)
        self.tree.grid(row=1, column=0, sticky="nsew")
        sb.grid(row=1, column=1, sticky="ns")

        up = ttk.LabelFrame(root, text="4. Upload", padding=6)
        up.grid(row=3, column=0, sticky="ew", pady=(6, 0))
        up.columnconfigure(1, weight=1)
        ttk.Checkbutton(up, text="After upload, move images to", variable=self.v["move"]).grid(
            row=0, column=0, sticky="w", **pad)
        ttk.Entry(up, textvariable=self.v["move_to"]).grid(row=0, column=1, sticky="ew", **pad)
        ttk.Button(up, text="Browse...", command=lambda: self._browse("move_to")).grid(row=0, column=2, **pad)
        wf = ttk.Frame(up)
        wf.grid(row=1, column=0, columnspan=3, sticky="w", **pad)
        ttk.Checkbutton(wf, text="Keep watching: auto upload new images every",
                        variable=self.v["watch"]).pack(side="left")
        ttk.Spinbox(wf, from_=1, to=3600, width=5, textvariable=self.v["interval"]).pack(side="left", padx=4)
        ttk.Label(wf, text="seconds").pack(side="left")
        btns = ttk.Frame(up)
        btns.grid(row=2, column=0, columnspan=3, sticky="w", **pad)
        self.btn_upload = ttk.Button(btns, text="Upload", command=self._on_upload)
        self.btn_upload.pack(side="left", padx=(0, 6))
        self.btn_stop = ttk.Button(btns, text="Stop", command=self._on_stop, state="disabled")
        self.btn_stop.pack(side="left")
        ttk.Button(btns, text="Clear log", command=lambda: self.log_box.delete("1.0", "end")).pack(
            side="left", padx=6)

        self.log_box = ScrolledText(root, height=9, wrap="word")
        self.log_box.grid(row=4, column=0, sticky="nsew", pady=(6, 0))
        root.rowconfigure(2, weight=3)
        root.rowconfigure(4, weight=2)

    def _browse(self, key):
        d = filedialog.askdirectory(initialdir=self.v[key].get() or self.v["folder"].get() or None)
        if d:
            self.v[key].set(os.path.normpath(d))

    # ------------------------------------------------------------ settings
    def _load_settings(self):
        for path in settings_paths():
            try:
                with open(path, encoding="utf-8") as fh:
                    data = json.load(fh)
            except (OSError, ValueError):
                continue
            for k in SAVED_FIELDS:
                if k in data:
                    self.v[k].set(data[k])
            return

    def _save_settings(self):
        data = {k: self.v[k].get() for k in SAVED_FIELDS}  # never the password
        for path in settings_paths():
            try:
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8") as fh:
                    json.dump(data, fh, indent=2)
                return
            except OSError:
                continue

    # ------------------------------------------------- thread-safe updates
    def log(self, msg):
        self.msgq.put(str(msg))

    def ui(self, fn):
        """Run fn on the Tk thread."""
        self.msgq.put(fn)

    def _drain(self):
        try:
            while True:
                msg = self.msgq.get_nowait()
                if callable(msg):
                    msg()
                else:
                    self.log_box.insert("end", msg + "\n")
                    self.log_box.see("end")
        except queue.Empty:
            pass
        self.after(100, self._drain)

    # ------------------------------------------------------------- actions
    def _validate(self, need_folder_exists=True):
        names = {"url": "ALM URL", "domain": "Domain", "project": "Project", "user": "Username",
                 "password": "Password", "lab_folder": "Test Lab folder", "folder": "Image folder"}
        missing = [label for k, label in names.items() if not str(self.v[k].get()).strip()]
        if missing:
            messagebox.showwarning(APP_NAME, "Please fill in: " + ", ".join(missing))
            return False
        folder = self.v["folder"].get().strip()
        if need_folder_exists and not os.path.isdir(folder):
            messagebox.showwarning(APP_NAME, "Image folder not found:\n{}\n\n"
                                             "Tip: press 'Create image folders' first.".format(folder))
            return False
        return True

    def _validate_upload(self):
        if self.v["move"].get():
            move_to = self.v["move_to"].get().strip()
            if not move_to:
                messagebox.showwarning(APP_NAME, "Choose the folder to move uploaded images to.")
                return False
            folder = os.path.abspath(self.v["folder"].get().strip())
            if os.path.abspath(move_to) == folder or \
                    os.path.abspath(move_to).startswith(folder + os.sep):
                messagebox.showwarning(APP_NAME, "The 'move to' folder must be outside the image folder.")
                return False
        try:
            if float(self.v["interval"].get()) <= 0:
                raise ValueError
        except ValueError:
            messagebox.showwarning(APP_NAME, "Watch interval must be a positive number.")
            return False
        return True

    def _on_check(self):
        if self._validate():
            self._start(self._job_check)

    def _on_make(self):
        if not self._validate(need_folder_exists=False):
            return
        folder = self.v["folder"].get().strip()
        if not messagebox.askyesno(APP_NAME, "Create one folder per test set and test case inside\n"
                                             "{}\n\nExisting folders and files are kept.".format(folder)):
            return
        self._start(self._job_make)

    def _on_upload(self):
        if self._validate() and self._validate_upload():
            self._start(self._job_upload)

    def _on_stop(self):
        self.stop_event.set()
        self.log("Stopping after the current image...")

    def _on_close(self):
        if self.worker and self.worker.is_alive():
            if not messagebox.askyesno(APP_NAME, "Still working. Stop and exit?"):
                return
            self.stop_event.set()
        self._save_settings()
        self.destroy()

    def _set_busy(self, busy):
        for b in (self.btn_check, self.btn_make, self.btn_upload):
            b.config(state="disabled" if busy else "normal")
        self.btn_stop.config(state="normal" if busy else "disabled")

    def _start(self, target):
        self._save_settings()
        # jobs run in a worker thread and must not touch Tk variables
        self.cfg = {k: var.get() for k, var in self.v.items()}
        self.stop_event.clear()
        self._set_busy(True)
        self.worker = threading.Thread(target=self._guard, args=(target,), daemon=True)
        self.worker.start()

    def _guard(self, target):
        try:
            target()
        except (alm.AlmError, requests.RequestException, OSError) as e:
            self.log("ERROR: {}".format(e))
        except Exception as e:  # keep the window alive on unexpected errors
            self.log("UNEXPECTED ERROR: {!r}".format(e))
        finally:
            self.ui(lambda: self._set_busy(False))

    # ---------------------------------------------------------------- jobs
    def _connect(self):
        insecure = self.cfg["insecure"]
        if insecure:
            requests.packages.urllib3.disable_warnings()
        client = alm.AlmClient(self.cfg["url"].strip(), self.cfg["domain"].strip(),
                               self.cfg["project"].strip(), verify=not insecure)
        self.log("Connecting to {} ...".format(client.base))
        client.login(self.cfg["user"].strip(), self.cfg["password"])
        return client

    def _load_lab(self, client):
        self.log("Reading Test Lab folder '{}' ...".format(self.cfg["lab_folder"].strip()))
        lab = alm.TestLabFolder(client, self.cfg["lab_folder"].strip())
        self.log("Found {} test set(s) with {} test case(s).".format(len(lab.sets), lab.instance_count))
        return lab

    def _show_plan(self, lab, folder):
        """Fill the tree with every test case and how many images it will get."""
        plan = lab.plan(folder) if os.path.isdir(folder) else []
        per_inst, bad = {}, []
        for item in plan:
            if item["instance"]:
                per_inst.setdefault(item["instance"]["id"], []).append(item["path"])
            else:
                bad.append(item)
        empty = sum(1 for ts in lab.sets for i in ts["instances"] if i["id"] not in per_inst)

        def fill():
            self.tree.delete(*self.tree.get_children())
            for ts in lab.sets:
                n = sum(len(per_inst.get(i["id"], [])) for i in ts["instances"])
                parent = self.tree.insert("", "end", text="\\".join(ts["path"]),
                                          values=(n, "test set id {}".format(ts["id"])),
                                          open=len(lab.sets) <= 5)
                for i in ts["instances"]:
                    k = len(per_inst.get(i["id"], []))
                    self.tree.insert(parent, "end", text=i["label"],
                                     values=(k, "ready" if k else "no images"),
                                     tags=() if k else ("warn",))
            if bad:
                parent = self.tree.insert("", "end", text="Images NOT matched", open=True,
                                          values=(len(bad), "fix the folder name"), tags=("bad",))
                for item in bad:
                    self.tree.insert(parent, "end", text=os.path.relpath(item["path"], folder),
                                     values=("", item["error"]), tags=("bad",))
            self.summary.set("{} image(s) ready for {} test case(s).   {} test case(s) without "
                             "images.   {} image(s) not matched.".format(
                                 len(plan) - len(bad), len(per_inst), empty, len(bad)))
        self.ui(fill)
        return plan

    def _job_check(self):
        client = self._connect()
        try:
            self._show_plan(self._load_lab(client), self.cfg["folder"].strip())
            self.log("Check done. Nothing was uploaded.")
        finally:
            client.logout()

    def _job_make(self):
        client = self._connect()
        try:
            lab = self._load_lab(client)
            folder = self.cfg["folder"].strip()
            made = lab.make_folders(folder)
            self.log("Created {} new test case folder(s) in {}".format(made, folder))
            self._show_plan(lab, folder)
            if os.name == "nt":
                os.startfile(folder)
        finally:
            client.logout()

    def _job_upload(self):
        folder = self.cfg["folder"].strip()
        watch = self.cfg["watch"]
        interval = float(self.cfg["interval"])
        args = argparse.Namespace(
            entity="test-instances", prefix="", allow_duplicates=False, dry_run=False,
            move_to=self.cfg["move_to"].strip() if self.cfg["move"] else None,
            delete_after=False, folder=folder,
        )
        client = self._connect()
        up = None
        try:
            lab = self._load_lab(client)
            plan = self._show_plan(lab, folder)

            def resolver(path):
                _ts, inst, err = lab.resolve(folder, path)
                return [inst["id"]] if inst else err

            up = alm.Uploader(client, args, resolver, log=self.log)
            todo = [item["path"] for item in plan if item["instance"]]
            self.log("Uploading {} image(s)...".format(len(todo)))
            for path in todo:
                if self.stop_event.is_set():
                    break
                up.handle(path)
            up.done.update(item["path"] for item in plan)  # unmatched: reported in the tree

            if watch and not self.stop_event.is_set():
                self.log("Watching the image folder every {:g}s. Press Stop to finish.".format(interval))
                while not self.stop_event.wait(interval):
                    for path in alm.list_images(folder, True):
                        if self.stop_event.is_set():
                            break
                        if path not in up.done and alm.file_is_stable(path):
                            up.handle(path)
        finally:
            client.logout()
            if up:
                self.log("Done: {} uploaded, {} skipped, {} failed.".format(
                    up.ok, up.skipped, up.failed))
                if up.ok and not watch:
                    self.ui(lambda: messagebox.showinfo(
                        APP_NAME, "{} image(s) uploaded.\n{} skipped, {} failed.".format(
                            up.ok, up.skipped, up.failed)))


def main():
    App().mainloop()


if __name__ == "__main__":
    main()
