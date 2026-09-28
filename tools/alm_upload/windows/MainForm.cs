// ALM Image Uploader - Windows Forms UI.
// Step 1: log in.  Step 2: choose domain and project.  Step 3: choose the Test Lab
// folder and image folder, check, and upload.

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

        // step 1
        Panel pageLogin;
        TextBox txtUrl, txtUser, txtPassword;
        CheckBox chkInsecure;
        Button btnLogin;
        Label lblLoginStatus;
        // step 2
        Panel pageProject;
        ComboBox cboDomain, cboProject;
        Button btnNext, btnLogout2;
        Label lblWho2, lblProjectStatus;
        // step 3
        Panel pageWork;
        Label lblWho3, lblSummary;
        TextBox txtLabFolder, txtFolder, txtMoveTo, txtLog;
        CheckBox chkMove, chkWatch;
        NumericUpDown numInterval;
        Button btnPickLab, btnCheck, btnMake, btnUpload, btnStop;
        TreeView tree;

        AlmClient client;
        string labFolderId, labFolderPath;   // set by the Test Lab folder picker
        readonly Dictionary<string, string> settings = new Dictionary<string, string>();

        Thread worker;
        volatile bool stopRequested;
        Config cfg;

        class Config
        {
            public string LabFolder, LabFolderText, Folder, MoveTo;
            public bool Watch;
            public int Interval;
        }

        public MainForm()
        {
            Text = AppName;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(860, 820);
            MinimumSize = new Size(720, 640);
            StartPosition = FormStartPosition.CenterScreen;
            LoadSettings();
            BuildLoginPage();
            BuildProjectPage();
            BuildWorkPage();
            Shown += (s, e) => ShowPage(pageLogin);
            FormClosing += OnClosing;
        }

        // ------------------------------------------------------------ helpers
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

        static Button B(string text, bool bold = false)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            if (bold) b.Font = new Font(b.Font, FontStyle.Bold);
            return b;
        }

        static TableLayoutPanel Grid(int columns)
        {
            return new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
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

        /// A centred card for the login / project steps. Rows 0-1 hold the title and subtitle.
        Panel Card(string title, out Label subtitle, out TableLayoutPanel body)
        {
            var page = new Panel { Dock = DockStyle.Fill, Visible = false };
            var card = new TableLayoutPanel
            {
                ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(20), BackColor = SystemColors.Window,
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            var t = new Label
            {
                Text = title, AutoSize = true, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
                Margin = new Padding(3, 0, 3, 4),
            };
            card.Controls.Add(t, 0, 0);
            card.SetColumnSpan(t, 2);
            subtitle = new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 14),
                MaximumSize = new Size(480, 0),
            };
            card.Controls.Add(subtitle, 0, 1);
            card.SetColumnSpan(subtitle, 2);
            page.Controls.Add(card);
            EventHandler centre = (s, e) => card.Location = new Point(Math.Max(0, (page.Width - card.Width) / 2),
                                                                      Math.Max(0, (page.Height - card.Height) / 3));
            page.Resize += centre;
            card.SizeChanged += centre;
            body = card;
            return page;
        }

        void ShowPage(Panel page)
        {
            foreach (var p in new[] { pageLogin, pageProject, pageWork })
                p.Visible = p == page;
            if (page == pageLogin)
            {
                AcceptButton = btnLogin;
                (txtUrl.Text == "" ? txtUrl : txtUser.Text == "" ? txtUser : txtPassword).Focus();
            }
            else if (page == pageProject)
            {
                AcceptButton = btnNext;
                cboProject.Focus();
            }
            else AcceptButton = null;
        }

        // ------------------------------------------------------ step 1: login
        void BuildLoginPage()
        {
            TableLayoutPanel c;
            Label sub;
            pageLogin = Card("Step 1 of 3:  Log in to ALM", out sub, out c);
            sub.Text = "Enter the ALM address and your ALM username and password.";
            c.Controls.Add(L("ALM URL"), 0, 2);
            c.Controls.Add(txtUrl = T(), 1, 2);
            var eg = L("e.g. https://alm.company.com/qcbin", true);
            eg.Margin = new Padding(3, 0, 3, 6);
            c.Controls.Add(eg, 1, 3);
            c.Controls.Add(L("Username"), 0, 4);
            c.Controls.Add(txtUser = T(), 1, 4);
            c.Controls.Add(L("Password"), 0, 5);
            c.Controls.Add(txtPassword = T(true), 1, 5);
            chkInsecure = new CheckBox { Text = "Skip SSL certificate check (only if login fails)", AutoSize = true };
            c.Controls.Add(chkInsecure, 1, 6);
            btnLogin = B("Log in  >", true);
            btnLogin.Margin = new Padding(3, 12, 3, 3);
            btnLogin.Click += OnLogin;
            c.Controls.Add(btnLogin, 1, 7);
            lblLoginStatus = new Label { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.Firebrick };
            c.Controls.Add(lblLoginStatus, 1, 8);

            txtUrl.Text = Setting("url");
            txtUser.Text = Setting("user");
            chkInsecure.Checked = Setting("insecure") == "1";
            Controls.Add(pageLogin);
        }

        void OnLogin(object sender, EventArgs e)
        {
            if (Busy()) return;
            if (txtUrl.Text.Trim() == "" || txtUser.Text.Trim() == "" || txtPassword.Text == "")
            {
                lblLoginStatus.ForeColor = Color.Firebrick;
                lblLoginStatus.Text = "Please fill in ALM URL, username and password.";
                return;
            }
            SaveSettings();
            string url = txtUrl.Text.Trim(), user = txtUser.Text.Trim(), pass = txtPassword.Text;
            bool insecure = chkInsecure.Checked;
            lblLoginStatus.ForeColor = SystemColors.GrayText;
            lblLoginStatus.Text = "Logging in...";
            SetLoginBusy(true);
            RunBackground(() =>
            {
                var c = new AlmClient(url, insecure);
                c.Login(user, pass);
                var domains = c.GetDomains();
                Ui(() =>
                {
                    client = c;
                    lblLoginStatus.Text = "";
                    txtPassword.Text = "";
                    lblWho2.Text = "Logged in as " + user + " at " + c.Base;
                    lblProjectStatus.Text = domains.Count == 0 ? "Your account has no domains in this ALM." : "";
                    ShowPage(pageProject);
                    FillCombo(cboDomain, domains, Setting("domain"));
                });
            }, err => Ui(() =>
            {
                lblLoginStatus.ForeColor = Color.Firebrick;
                lblLoginStatus.Text = "Login failed: " + err;
            }), () => Ui(() => SetLoginBusy(false)));
        }

        void SetLoginBusy(bool busy)
        {
            btnLogin.Enabled = txtUrl.Enabled = txtUser.Enabled = txtPassword.Enabled = !busy;
            UseWaitCursor = busy;
        }

        // ------------------------------------------- step 2: domain / project
        void BuildProjectPage()
        {
            TableLayoutPanel c;
            pageProject = Card("Step 2 of 3:  Choose domain and project", out lblWho2, out c);
            c.Controls.Add(L("Domain"), 0, 2);
            cboDomain = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            c.Controls.Add(cboDomain, 1, 2);
            c.Controls.Add(L("Project"), 0, 3);
            cboProject = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            c.Controls.Add(cboProject, 1, 3);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            btnNext = B("Next  >", true);
            btnNext.Enabled = false;
            btnLogout2 = B("Log out");
            btnNext.Click += OnProjectChosen;
            btnLogout2.Click += (s, e) => { if (!Busy()) Logout(); };
            row.Controls.Add(btnNext);
            row.Controls.Add(btnLogout2);
            c.Controls.Add(row, 1, 4);
            lblProjectStatus = new Label { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.Firebrick };
            c.Controls.Add(lblProjectStatus, 1, 5);
            cboDomain.SelectedIndexChanged += OnDomainChanged;
            Controls.Add(pageProject);
        }

        static void FillCombo(ComboBox cbo, List<string> items, string preferred)
        {
            cbo.BeginUpdate();
            cbo.Items.Clear();
            foreach (var i in items) cbo.Items.Add(i);
            cbo.EndUpdate();
            int idx = items.FindIndex(i => string.Equals(i, preferred, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && items.Count > 0) idx = 0;
            cbo.SelectedIndex = idx;   // fires SelectedIndexChanged
        }

        void OnDomainChanged(object sender, EventArgs e)
        {
            cboProject.Items.Clear();
            btnNext.Enabled = false;
            var domain = cboDomain.SelectedItem as string;
            if (domain == null || client == null) return;
            lblProjectStatus.ForeColor = SystemColors.GrayText;
            lblProjectStatus.Text = "Loading projects...";
            cboDomain.Enabled = false;
            var c = client;
            RunBackground(() =>
            {
                var projects = c.GetProjects(domain);
                Ui(() =>
                {
                    if (cboDomain.SelectedItem as string != domain) return;   // changed meanwhile
                    lblProjectStatus.ForeColor = Color.Firebrick;
                    lblProjectStatus.Text = projects.Count == 0 ? "No projects in this domain for your account." : "";
                    FillCombo(cboProject, projects, Setting("project"));
                    btnNext.Enabled = projects.Count > 0;
                });
            }, err => Ui(() =>
            {
                lblProjectStatus.ForeColor = Color.Firebrick;
                lblProjectStatus.Text = "Could not load projects: " + err;
            }), () => Ui(() => cboDomain.Enabled = true));
        }

        void OnProjectChosen(object sender, EventArgs e)
        {
            var domain = cboDomain.SelectedItem as string;
            var project = cboProject.SelectedItem as string;
            if (domain == null || project == null || Busy()) return;
            bool changed = client.Domain != domain || client.Project != project;
            client.Domain = domain;
            client.Project = project;
            settings["domain"] = domain;
            settings["project"] = project;
            SaveSettings();
            lblWho3.Text = string.Format("{0}   |   Domain: {1}   |   Project: {2}", client.User, domain, project);
            if (changed)
            {
                tree.Nodes.Clear();
                lblSummary.Text = "Choose the Test Lab folder and image folder, then press Check.";
            }
            ShowPage(pageWork);
        }

        void Logout()
        {
            var c = client;
            client = null;
            if (c != null)
                ThreadPool.QueueUserWorkItem(_ => { try { c.Logout(); } catch (Exception) { } });
            tree.Nodes.Clear();
            lblSummary.Text = "Choose the Test Lab folder and image folder, then press Check.";
            cboDomain.Items.Clear();
            cboProject.Items.Clear();
            lblLoginStatus.Text = "";
            ShowPage(pageLogin);
        }

        // ------------------------------------------------ step 3: the work
        void BuildWorkPage()
        {
            pageWork = new Panel { Dock = DockStyle.Fill, Visible = false };
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // header: who / where + change project / log out
            var head = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblWho3 = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 8, 3, 3) };
            var btnChange = B("<  Change project");
            var btnLogout = B("Log out");
            btnChange.Click += (s, e) => { if (!Busy()) ShowPage(pageProject); };
            btnLogout.Click += (s, e) => { if (!Busy()) Logout(); };
            head.Controls.Add(lblWho3, 0, 0);
            head.Controls.Add(btnChange, 1, 0);
            head.Controls.Add(btnLogout, 2, 0);
            main.Controls.Add(head);

            // folders
            var g2 = Grid(3);
            g2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g2.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g2.Controls.Add(L("Test Lab folder"), 0, 0);
            g2.Controls.Add(txtLabFolder = T(), 1, 0);
            btnPickLab = B("Browse ALM...");
            btnPickLab.Click += OnPickLabFolder;
            g2.Controls.Add(btnPickLab, 2, 0);
            g2.Controls.Add(L("Image folder"), 0, 1);
            g2.Controls.Add(txtFolder = T(), 1, 1);
            var browse1 = B("Browse...");
            browse1.Click += (s, e) => BrowseLocal(txtFolder);
            g2.Controls.Add(browse1, 2, 1);
            var hint = L("Layout:  <image folder>\\<test set>\\<test case>\\screenshot.png", true);
            g2.Controls.Add(hint, 1, 2);
            g2.SetColumnSpan(hint, 2);
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            btnMake = B("Create image folders");
            btnCheck = B("Check (preview, no upload)");
            btnMake.Click += OnMake;
            btnCheck.Click += (s, e) => { if (CheckFields(true)) Start(JobCheck); };
            row.Controls.Add(btnMake);
            row.Controls.Add(btnCheck);
            g2.Controls.Add(row, 1, 3);
            g2.SetColumnSpan(row, 2);
            main.Controls.Add(Box("Step 3 of 3:  Test Lab folder and image folder", g2));

            // plan
            var g3 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            g3.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g3.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            lblSummary = L("Choose the Test Lab folder and image folder, then press Check.");
            g3.Controls.Add(lblSummary, 0, 0);
            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            g3.Controls.Add(tree, 0, 1);
            var box3 = new GroupBox { Text = "Test sets and test cases", Dock = DockStyle.Fill, Padding = new Padding(6) };
            box3.Controls.Add(g3);
            main.Controls.Add(box3);

            // upload
            var g4 = Grid(3);
            g4.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g4.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            g4.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            chkMove = new CheckBox { Text = "After upload, move images to", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            g4.Controls.Add(chkMove, 0, 0);
            g4.Controls.Add(txtMoveTo = T(), 1, 0);
            var browse2 = B("Browse...");
            browse2.Click += (s, e) => BrowseLocal(txtMoveTo);
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
            btnUpload = B("Upload", true);
            btnStop = B("Stop");
            btnStop.Enabled = false;
            var btnClear = B("Clear log");
            btnUpload.Click += (s, e) => { if (CheckFields(true) && ValidateUpload()) Start(JobUpload); };
            btnStop.Click += (s, e) => { stopRequested = true; Log("Stopping after the current image..."); };
            btnClear.Click += (s, e) => txtLog.Clear();
            brow.Controls.Add(btnUpload);
            brow.Controls.Add(btnStop);
            brow.Controls.Add(btnClear);
            g4.Controls.Add(brow, 0, 2);
            g4.SetColumnSpan(brow, 3);
            main.Controls.Add(Box("Upload", g4));

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
            pageWork.Controls.Add(main);

            txtLabFolder.Text = Setting("lab_folder");
            if (Setting("lab_folder_id") != "")
            {
                labFolderId = Setting("lab_folder_id");
                labFolderPath = txtLabFolder.Text;
            }
            // typing a path by hand drops the id picked from the tree
            txtLabFolder.TextChanged += (s, e) => { if (txtLabFolder.Text != labFolderPath) labFolderId = null; };
            txtFolder.Text = Setting("folder");
            chkMove.Checked = Setting("move") == "1";
            txtMoveTo.Text = Setting("move_to");
            chkWatch.Checked = Setting("watch") == "1";
            decimal n;
            if (decimal.TryParse(Setting("interval"), out n))
                numInterval.Value = Math.Max(1, Math.Min(3600, n));
            Controls.Add(pageWork);
        }

        void BrowseLocal(TextBox target)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = target.Text != "" ? target.Text : txtFolder.Text;
                if (d.ShowDialog(this) == DialogResult.OK)
                    target.Text = d.SelectedPath;
            }
        }

        void OnPickLabFolder(object sender, EventArgs e)
        {
            if (Busy()) return;
            using (var dlg = new TestLabFolderPicker(client))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                labFolderPath = dlg.SelectedPath;
                txtLabFolder.Text = labFolderPath;
                labFolderId = dlg.SelectedId;
                SaveSettings();
            }
        }

        // ------------------------------------------------------------ settings
        static IEnumerable<string> SettingsPaths()
        {
            yield return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), SettingsFile);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "ALMImageUploader", SettingsFile);
        }

        string Setting(string key)
        {
            string v;
            return settings.TryGetValue(key, out v) ? v : "";
        }

        void LoadSettings()
        {
            foreach (var path in SettingsPaths())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) settings[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
                    return;
                }
                catch (IOException) { }
            }
        }

        void SaveSettings()
        {
            // never the password
            settings["url"] = txtUrl.Text.Trim();
            settings["user"] = txtUser.Text.Trim();
            settings["insecure"] = chkInsecure.Checked ? "1" : "0";
            if (txtLabFolder != null)
            {
                settings["lab_folder"] = txtLabFolder.Text.Trim();
                settings["lab_folder_id"] = labFolderId ?? "";
                settings["folder"] = txtFolder.Text.Trim();
                settings["move"] = chkMove.Checked ? "1" : "0";
                settings["move_to"] = txtMoveTo.Text.Trim();
                settings["watch"] = chkWatch.Checked ? "1" : "0";
                settings["interval"] = numInterval.Value.ToString();
            }
            var lines = settings.Select(kv => kv.Key + "=" + kv.Value).ToArray();
            foreach (var path in SettingsPaths())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllLines(path, lines);
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
            if (txtLabFolder.Text.Trim() == "")
            {
                Warn("Choose the Test Lab folder (press 'Browse ALM...').");
                return false;
            }
            if (txtFolder.Text.Trim() == "")
            {
                Warn("Choose the image folder on this computer.");
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
            if (Busy())
            {
                if (MessageBox.Show(this, "Still working. Stop and exit?", AppName, MessageBoxButtons.YesNo) != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                stopRequested = true;
            }
            SaveSettings();
            if (client != null)
                try { client.Logout(); } catch (Exception) { }
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

        bool Busy()
        {
            return worker != null && worker.IsAlive;
        }

        static string Describe(Exception e)
        {
            if (e is AlmException) return e.Message;
            if (e is WebException || e is UriFormatException) return "cannot connect (" + e.Message + ")";
            if (e is System.Xml.XmlException) return "unexpected reply from the server - is the ALM URL correct?";
            return e.Message;
        }

        /// Runs work off the UI thread; onError gets a readable message.
        void RunBackground(Action work, Action<string> onError, Action always)
        {
            worker = new Thread(() =>
            {
                try { work(); }
                catch (Exception e) { onError(Describe(e)); }
                finally { always(); }
            }) { IsBackground = true };
            worker.Start();
        }

        void SetBusy(bool busy)
        {
            btnCheck.Enabled = btnMake.Enabled = btnUpload.Enabled = btnPickLab.Enabled = !busy;
            btnStop.Enabled = busy;
        }

        void Start(Action job)
        {
            SaveSettings();
            // jobs run on a worker thread and must not touch the controls
            cfg = new Config
            {
                LabFolder = labFolderId ?? txtLabFolder.Text.Trim(), LabFolderText = txtLabFolder.Text.Trim(),
                Folder = txtFolder.Text.Trim(), MoveTo = chkMove.Checked ? txtMoveTo.Text.Trim() : null,
                Watch = chkWatch.Checked, Interval = (int)numInterval.Value,
            };
            stopRequested = false;
            SetBusy(true);
            RunBackground(job, err => Log("ERROR: " + err), () => Ui(() => SetBusy(false)));
        }

        // ---------------------------------------------------------------- jobs
        TestLabFolder LoadLab()
        {
            Log("Reading Test Lab folder '" + cfg.LabFolderText + "' ...");
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
            ShowPlan(LoadLab());
            Log("Check done. Nothing was uploaded.");
        }

        void JobMake()
        {
            var lab = LoadLab();
            int made = lab.MakeFolders(cfg.Folder);
            Log(string.Format("Created {0} new test case folder(s) in {1}", made, cfg.Folder));
            ShowPlan(lab);
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                System.Diagnostics.Process.Start("explorer.exe", "\"" + cfg.Folder + "\"");
        }

        void JobUpload()
        {
            Uploader up = null;
            try
            {
                var lab = LoadLab();
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

    /// Dialog showing the Test Lab folder tree from ALM, loaded as folders are expanded.
    public class TestLabFolderPicker : Form
    {
        const string Placeholder = "...";
        readonly AlmClient client;
        readonly TreeView tree;
        readonly Button ok;
        readonly Label status;
        public string SelectedId, SelectedPath;

        public TestLabFolderPicker(AlmClient client)
        {
            this.client = client;
            Text = "Choose Test Lab folder";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(500, 540);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label
            {
                AutoSize = true, Margin = new Padding(3, 3, 3, 6), MaximumSize = new Size(470, 0),
                Text = "Choose a folder. Every test set inside it, including its sub folders, is included.",
            });
            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            layout.Controls.Add(tree);
            var row = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, AutoSize = true };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
            ok = new Button { Text = "Choose", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            row.Controls.Add(status, 0, 0);
            row.Controls.Add(ok, 1, 0);
            row.Controls.Add(cancel, 2, 0);
            layout.Controls.Add(row);
            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;

            tree.BeforeExpand += (s, e) => LoadChildren(e.Node);
            tree.AfterSelect += (s, e) =>
            {
                var folder = e.Node.Tag as string;
                ok.Enabled = folder != null;
                if (folder == null) return;
                SelectedId = folder;
                SelectedPath = e.Node.FullPath;
            };
            Shown += (s, e) => LoadRoot();
        }

        public TreeView Tree { get { return tree; } }

        void SetStatus(bool busy, string msg)
        {
            status.Text = msg;
            UseWaitCursor = busy;
            Application.DoEvents();
        }

        void LoadRoot()
        {
            try
            {
                SetStatus(true, "Loading...");
                var root = tree.Nodes.Add("Root");
                root.Tag = client.GetTestLabRootId();
                root.Nodes.Add(Placeholder);
                root.Expand();
                SetStatus(false, "");
            }
            catch (Exception e)
            {
                SetStatus(false, "Could not read Test Lab: " + e.Message);
            }
        }

        void LoadChildren(TreeNode node)
        {
            if (node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder || node.Nodes[0].Tag != null)
                return;   // already loaded
            try
            {
                SetStatus(true, "Loading " + node.Text + "...");
                var id = (string)node.Tag;
                var folders = client.GetChildFolders(id);
                var sets = client.GetTestSetsIn(id);
                tree.BeginUpdate();
                node.Nodes.Clear();
                foreach (var f in folders)
                {
                    var child = node.Nodes.Add(f["name"]);
                    child.Tag = f["id"];
                    child.Nodes.Add(Placeholder);
                }
                foreach (var ts in sets)
                    node.Nodes.Add("(test set)  " + ts["name"]).ForeColor = SystemColors.GrayText;
                if (folders.Count == 0 && sets.Count == 0)
                    node.Nodes.Add("(empty)").ForeColor = SystemColors.GrayText;
                tree.EndUpdate();
                SetStatus(false, "");
            }
            catch (Exception e)
            {
                tree.EndUpdate();
                SetStatus(false, "Could not read folder: " + e.Message);
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
