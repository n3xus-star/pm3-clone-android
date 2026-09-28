// ALM Image Uploader - Windows Forms UI.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace AlmImageUploader
{
    public class MainForm : Form
    {
        const string AppName = "ALM Image Uploader";
        const string SettingsFile = "ALMImageUploader.ini";

        TextBox txtUrl, txtDomain, txtProject, txtUser, txtPassword, txtLabFolder, txtFolder, txtMoveTo, txtLog;
        CheckBox chkInsecure, chkMove, chkWatch;
        NumericUpDown numInterval;
        Button btnCheck, btnMake, btnUpload, btnStop;
        Label lblSummary;
        TreeView tree;

        Thread worker;
        volatile bool stopRequested;
        Config cfg;

        class Config
        {
            public string Url, Domain, Project, User, Password, LabFolder, Folder, MoveTo;
            public bool Insecure, Move, Watch;
            public int Interval;
        }

        public MainForm()
        {
            Text = AppName;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(840, 800);
            MinimumSize = new Size(700, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            LoadSettings();
            FormClosing += OnClosing;
        }

        // ------------------------------------------------------------------ UI
        static TableLayoutPanel Grid(int columns)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            return t;
        }

        static Label L(string text, bool gray = false)
        {
            return new Label
            {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3),
                ForeColor = gray ? SystemColors.GrayText : SystemColors.ControlText,
            };
        }

        static TextBox T(bool password = false)
        {
            return new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = password, Margin = new Padding(3, 4, 3, 3) };
        }

        static GroupBox Box(string title, Control content)
        {
            var g = new GroupBox
            {
                Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            };
            g.Controls.Add(content);
            return g;
        }

        void BuildUi()
        {
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // 1. login
            var g1 = Grid(4);
            g1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            g1.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            g1.Controls.Add(L("ALM URL"), 0, 0);
            g1.Controls.Add(txtUrl = T(), 1, 0);
            g1.SetColumnSpan(txtUrl, 3);
            g1.Controls.Add(L("Domain"), 0, 1);
            g1.Controls.Add(txtDomain = T(), 1, 1);
            g1.Controls.Add(L("Project"), 2, 1);
            g1.Controls.Add(txtProject = T(), 3, 1);
            g1.Controls.Add(L("Username"), 0, 2);
            g1.Controls.Add(txtUser = T(), 1, 2);
            g1.Controls.Add(L("Password"), 2, 2);
            g1.Controls.Add(txtPassword = T(true), 3, 2);
            chkInsecure = new CheckBox { Text = "Skip SSL certificate check (only if the connection fails)", AutoSize = true };
            g1.Controls.Add(chkInsecure, 1, 3);
            g1.SetColumnSpan(chkInsecure, 3);
            main.Controls.Add(Box("1. ALM login", g1));

            // 2. folders
            var g2 = Grid(3);
            g2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g2.Controls.Add(L("Test Lab folder"), 0, 0);
            g2.Controls.Add(txtLabFolder = T(), 1, 0);
            g2.Controls.Add(L("e.g. Root\\Release 1\\Sprint 12  or folder ID", true), 2, 0);
            g2.Controls.Add(L("Image folder"), 0, 1);
            g2.Controls.Add(txtFolder = T(), 1, 1);
            var browse1 = new Button { Text = "Browse...", AutoSize = true };
            browse1.Click += (s, e) => Browse(txtFolder);
            g2.Controls.Add(browse1, 2, 1);
            var hint = L("Layout:  <image folder>\\<test set>\\<test case>\\screenshot.png", true);
            g2.Controls.Add(hint, 1, 2);
            g2.SetColumnSpan(hint, 2);
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            btnCheck = new Button { Text = "Check (preview, no upload)", AutoSize = true };
            btnMake = new Button { Text = "Create image folders", AutoSize = true };
            btnCheck.Click += (s, e) => { if (CheckFields(true)) Start(JobCheck); };
            btnMake.Click += OnMake;
            row.Controls.Add(btnCheck);
            row.Controls.Add(btnMake);
            g2.Controls.Add(row, 1, 3);
            g2.SetColumnSpan(row, 2);
            main.Controls.Add(Box("2. Test Lab folder and image folder", g2));

            // 3. plan
            var g3 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            g3.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g3.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            lblSummary = L("Fill in the fields, then press Check.");
            g3.Controls.Add(lblSummary, 0, 0);
            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            g3.Controls.Add(tree, 0, 1);
            var box3 = new GroupBox { Text = "3. Test sets and test cases", Dock = DockStyle.Fill, Padding = new Padding(6) };
            box3.Controls.Add(g3);
            main.Controls.Add(box3);

            // 4. upload
            var g4 = Grid(3);
            g4.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g4.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            chkMove = new CheckBox { Text = "After upload, move images to", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            g4.Controls.Add(chkMove, 0, 0);
            g4.Controls.Add(txtMoveTo = T(), 1, 0);
            var browse2 = new Button { Text = "Browse...", AutoSize = true };
            browse2.Click += (s, e) => Browse(txtMoveTo);
            g4.Controls.Add(browse2, 2, 0);
            var wrow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            chkWatch = new CheckBox { Text = "Keep watching: auto upload new images every", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            numInterval = new NumericUpDown { Minimum = 1, Maximum = 3600, Value = 10, Width = 60 };
            wrow.Controls.Add(chkWatch);
            wrow.Controls.Add(numInterval);
            wrow.Controls.Add(L("seconds"));
            g4.Controls.Add(wrow, 0, 1);
            g4.SetColumnSpan(wrow, 3);
            var brow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            btnUpload = new Button { Text = "Upload", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
            btnStop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
            var btnClear = new Button { Text = "Clear log", AutoSize = true };
            btnUpload.Click += (s, e) => { if (CheckFields(true) && ValidateUpload()) Start(JobUpload); };
            btnStop.Click += (s, e) => { stopRequested = true; Log("Stopping after the current image..."); };
            btnClear.Click += (s, e) => txtLog.Clear();
            brow.Controls.Add(btnUpload);
            brow.Controls.Add(btnStop);
            brow.Controls.Add(btnClear);
            g4.Controls.Add(brow, 0, 2);
            g4.SetColumnSpan(brow, 3);
            main.Controls.Add(Box("4. Upload", g4));

            // log
            txtLog = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window,
            };
            main.Controls.Add(txtLog);

            main.RowCount = 5;
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            Controls.Add(main);
        }

        void Browse(TextBox target)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = target.Text != "" ? target.Text : txtFolder.Text;
                if (d.ShowDialog(this) == DialogResult.OK)
                    target.Text = d.SelectedPath;
            }
        }

        // ------------------------------------------------------------ settings
        static IEnumerable<string> SettingsPaths()
        {
            yield return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), SettingsFile);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "ALMImageUploader", SettingsFile);
        }

        Dictionary<string, Control> SavedFields()
        {
            return new Dictionary<string, Control>
            {
                { "url", txtUrl }, { "domain", txtDomain }, { "project", txtProject }, { "user", txtUser },
                { "insecure", chkInsecure }, { "lab_folder", txtLabFolder }, { "folder", txtFolder },
                { "move", chkMove }, { "move_to", txtMoveTo }, { "watch", chkWatch }, { "interval", numInterval },
            };
        }

        void LoadSettings()
        {
            foreach (var path in SettingsPaths())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var fields = SavedFields();
                    foreach (var line in File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        Control c;
                        if (!fields.TryGetValue(line.Substring(0, eq), out c)) continue;
                        var v = line.Substring(eq + 1);
                        if (c is CheckBox) ((CheckBox)c).Checked = v == "1";
                        else if (c is NumericUpDown)
                        {
                            decimal n;
                            if (decimal.TryParse(v, out n))
                                ((NumericUpDown)c).Value = Math.Max(1, Math.Min(3600, n));
                        }
                        else c.Text = v;
                    }
                    return;
                }
                catch (IOException) { }
            }
        }

        void SaveSettings()
        {
            // never the password
            var lines = SavedFields().Select(kv => kv.Key + "=" + (
                kv.Value is CheckBox ? (((CheckBox)kv.Value).Checked ? "1" : "0") :
                kv.Value is NumericUpDown ? ((NumericUpDown)kv.Value).Value.ToString() : kv.Value.Text));
            foreach (var path in SettingsPaths())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllLines(path, lines.ToArray());
                    return;
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                }
            }
        }

        // ---------------------------------------------------------- validation
        bool CheckFields(bool needFolder)
        {
            var names = new List<KeyValuePair<string, TextBox>>
            {
                new KeyValuePair<string, TextBox>("ALM URL", txtUrl),
                new KeyValuePair<string, TextBox>("Domain", txtDomain),
                new KeyValuePair<string, TextBox>("Project", txtProject),
                new KeyValuePair<string, TextBox>("Username", txtUser),
                new KeyValuePair<string, TextBox>("Password", txtPassword),
                new KeyValuePair<string, TextBox>("Test Lab folder", txtLabFolder),
                new KeyValuePair<string, TextBox>("Image folder", txtFolder),
            };
            var missing = names.Where(kv => kv.Value.Text.Trim() == "").Select(kv => kv.Key).ToList();
            if (missing.Count > 0)
            {
                Warn("Please fill in: " + string.Join(", ", missing));
                return false;
            }
            if (needFolder && !Directory.Exists(txtFolder.Text.Trim()))
            {
                Warn("Image folder not found:\n" + txtFolder.Text + "\n\nTip: press 'Create image folders' first.");
                return false;
            }
            return true;
        }

        bool ValidateUpload()
        {
            if (!chkMove.Checked) return true;
            var moveTo = txtMoveTo.Text.Trim();
            if (moveTo == "")
            {
                Warn("Choose the folder to move uploaded images to.");
                return false;
            }
            var folder = Path.GetFullPath(txtFolder.Text.Trim()).TrimEnd('\\', '/');
            var full = Path.GetFullPath(moveTo).TrimEnd('\\', '/');
            if (full.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Warn("The 'move to' folder must be outside the image folder.");
                return false;
            }
            return true;
        }

        void Warn(string msg)
        {
            MessageBox.Show(this, msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void OnMake(object sender, EventArgs e)
        {
            if (!CheckFields(false)) return;
            var msg = "Create one folder per test set and test case inside\n" + txtFolder.Text.Trim() +
                      "\n\nExisting folders and files are kept.";
            if (MessageBox.Show(this, msg, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Start(JobMake);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (worker != null && worker.IsAlive)
            {
                if (MessageBox.Show(this, "Still working. Stop and exit?", AppName, MessageBoxButtons.YesNo) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                stopRequested = true;
            }
            SaveSettings();
        }

        // ------------------------------------------------------------- threads
        void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        void Log(string msg)
        {
            Ui(() => txtLog.AppendText(msg + Environment.NewLine));
        }

        void SetBusy(bool busy)
        {
            btnCheck.Enabled = btnMake.Enabled = btnUpload.Enabled = !busy;
            btnStop.Enabled = busy;
            UseWaitCursor = busy && !(cfg != null && cfg.Watch);
        }

        void Start(Action job)
        {
            SaveSettings();
            // jobs run on a worker thread and must not touch the controls
            cfg = new Config
            {
                Url = txtUrl.Text.Trim(), Domain = txtDomain.Text.Trim(), Project = txtProject.Text.Trim(),
                User = txtUser.Text.Trim(), Password = txtPassword.Text, LabFolder = txtLabFolder.Text.Trim(),
                Folder = txtFolder.Text.Trim(), MoveTo = chkMove.Checked ? txtMoveTo.Text.Trim() : null,
                Insecure = chkInsecure.Checked, Move = chkMove.Checked, Watch = chkWatch.Checked,
                Interval = (int)numInterval.Value,
            };
            stopRequested = false;
            SetBusy(true);
            worker = new Thread(() =>
            {
                try { job(); }
                catch (AlmException e) { Log("ERROR: " + e.Message); }
                catch (WebException e) { Log("CONNECTION ERROR: " + e.Message); }
                catch (IOException e) { Log("FILE ERROR: " + e.Message); }
                catch (UnauthorizedAccessException e) { Log("FILE ERROR: " + e.Message); }
                catch (Exception e) { Log("UNEXPECTED ERROR: " + e); }
                finally { Ui(() => SetBusy(false)); }
            }) { IsBackground = true };
            worker.Start();
        }

        // ---------------------------------------------------------------- jobs
        AlmClient Connect()
        {
            var client = new AlmClient(cfg.Url, cfg.Domain, cfg.Project, cfg.Insecure);
            Log("Connecting to " + client.Base + " ...");
            client.Login(cfg.User, cfg.Password);
            return client;
        }

        TestLabFolder LoadLab(AlmClient client)
        {
            Log("Reading Test Lab folder '" + cfg.LabFolder + "' ...");
            var lab = new TestLabFolder(client, cfg.LabFolder);
            Log(string.Format("Found {0} test set(s) with {1} test case(s).", lab.Sets.Count, lab.InstanceCount));
            return lab;
        }

        List<PlanItem> ShowPlan(TestLabFolder lab)
        {
            var plan = Directory.Exists(cfg.Folder) ? lab.Plan(cfg.Folder) : new List<PlanItem>();
            var perInst = plan.Where(p => p.Instance != null).GroupBy(p => p.Instance.Id)
                              .ToDictionary(g => g.Key, g => g.Count());
            var bad = plan.Where(p => p.Instance == null).ToList();
            int empty = lab.Sets.Sum(s => s.Instances.Count(i => !perInst.ContainsKey(i.Id)));
            Ui(() =>
            {
                tree.BeginUpdate();
                tree.Nodes.Clear();
                foreach (var ts in lab.Sets)
                {
                    int n = ts.Instances.Sum(i => perInst.ContainsKey(i.Id) ? perInst[i.Id] : 0);
                    var parent = tree.Nodes.Add(string.Format("{0}   ({1} image(s), test set ID {2})", ts.PathText, n, ts.Id));
                    parent.NodeFont = new Font(tree.Font, FontStyle.Bold);
                    parent.Text = parent.Text; // re-measure with the bold font
                    foreach (var i in ts.Instances)
                    {
                        int k;
                        perInst.TryGetValue(i.Id, out k);
                        var node = parent.Nodes.Add(k > 0 ? string.Format("{0}   -  {1} image(s), ready", i.Label, k)
                                                          : i.Label + "   -  no images");
                        if (k == 0) node.ForeColor = Color.DarkOrange;
                    }
                    if (lab.Sets.Count <= 5) parent.Expand();
                }
                if (bad.Count > 0)
                {
                    var parent = tree.Nodes.Add(string.Format("Images NOT matched ({0}) - fix the folder name", bad.Count));
                    parent.ForeColor = Color.Firebrick;
                    foreach (var item in bad)
                        parent.Nodes.Add(Util.RelPath(cfg.Folder, item.File) + "   -  " + item.Error).ForeColor = Color.Firebrick;
                    parent.Expand();
                }
                tree.EndUpdate();
                lblSummary.Text = string.Format(
                    "{0} image(s) ready for {1} test case(s).    {2} test case(s) without images.    {3} image(s) not matched.",
                    plan.Count - bad.Count, perInst.Count, empty, bad.Count);
            });
            return plan;
        }

        void JobCheck()
        {
            var client = Connect();
            try
            {
                ShowPlan(LoadLab(client));
                Log("Check done. Nothing was uploaded.");
            }
            finally { client.Logout(); }
        }

        void JobMake()
        {
            var client = Connect();
            try
            {
                var lab = LoadLab(client);
                int made = lab.MakeFolders(cfg.Folder);
                Log(string.Format("Created {0} new test case folder(s) in {1}", made, cfg.Folder));
                ShowPlan(lab);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + cfg.Folder + "\"");
            }
            finally { client.Logout(); }
        }

        void JobUpload()
        {
            var client = Connect();
            Uploader up = null;
            try
            {
                var lab = LoadLab(client);
                var plan = ShowPlan(lab);
                up = new Uploader(client, cfg.Folder, cfg.MoveTo, Log);
                var todo = plan.Where(p => p.Instance != null).ToList();
                Log(string.Format("Uploading {0} image(s)...", todo.Count));
                foreach (var item in todo)
                {
                    if (stopRequested) break;
                    up.Handle(item);
                }
                foreach (var item in plan) up.Done.Add(item.File); // unmatched ones are shown in the list

                if (cfg.Watch && !stopRequested)
                {
                    Log(string.Format("Watching the image folder every {0}s. Press Stop to finish.", cfg.Interval));
                    while (!stopRequested)
                    {
                        for (int t = 0; t < cfg.Interval * 10 && !stopRequested; t++)
                            Thread.Sleep(100);
                        if (stopRequested) break;
                        foreach (var file in Util.ListImages(cfg.Folder))
                        {
                            if (stopRequested) break;
                            if (!up.Done.Contains(file) && Util.FileIsStable(file))
                                up.Handle(lab.Resolve(cfg.Folder, file));
                        }
                    }
                }
            }
            finally
            {
                client.Logout();
                if (up != null)
                {
                    Log(string.Format("Done: {0} uploaded, {1} skipped, {2} failed.", up.Ok, up.Skipped, up.Failed));
                    if (up.Ok > 0 && !cfg.Watch)
                    {
                        var msg = string.Format("{0} image(s) uploaded.\n{1} skipped, {2} failed.", up.Ok, up.Skipped, up.Failed);
                        Ui(() => MessageBox.Show(this, msg, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information));
                    }
                }
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
