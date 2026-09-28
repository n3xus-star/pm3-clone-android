// ALM Image Uploader - Windows Forms UI.
// Step 1: log in.  Step 2: choose domain and project.  Step 3: load the Test Lab folder
// and the image folder, assign images to test cases, and upload.

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
        static readonly Color Done = Color.ForestGreen, Hint = Color.SteelBlue, Warn = Color.DarkOrange;

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
        Label lblWho3, lblSummary, lblPreview, lblCase;
        TextBox txtLabFolder, txtFolder, txtImgFilter, txtTestFilter, txtLog;
        CheckBox chkOnlyUnassigned, chkHideUploaded;
        Button btnPickLab, btnLoad, btnAssign, btnAccept, btnUnassign, btnRemoveFromCase, btnUpload, btnStop;
        ListView lvImages;
        PictureBox picPreview;
        TreeView tree;
        ListBox lstCaseImages;
        SplitContainer split;

        AlmClient client;
        string labFolderId, labFolderPath;   // set by the Test Lab folder picker
        readonly Dictionary<string, string> settings = new Dictionary<string, string>();

        // loaded data (step 3)
        TestLabFolder lab;
        string imageRoot;
        List<ImageItem> images = new List<ImageItem>();

        Thread worker;
        volatile bool stopRequested;

        public MainForm()
        {
            Text = AppName;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1180, 820);
            MinimumSize = new Size(900, 640);
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

        static TableLayoutPanel Rows(params SizeType[] rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var r in rows)
                t.RowStyles.Add(r == SizeType.AutoSize ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Percent, 50));
            return t;
        }

        static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
            f.Controls.AddRange(controls);
            return f;
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
            else
            {
                AcceptButton = null;
                if (split.Width > 200) split.SplitterDistance = split.Width / 2;
            }
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
            if (changed) ClearLoaded();
            ShowPage(pageWork);
        }

        void Logout()
        {
            var c = client;
            client = null;
            if (c != null)
                ThreadPool.QueueUserWorkItem(_ => { try { c.Logout(); } catch (Exception) { } });
            ClearLoaded();
            cboDomain.Items.Clear();
            cboProject.Items.Clear();
            lblLoginStatus.Text = "";
            ShowPage(pageLogin);
        }

        // ------------------------------------------------ step 3: layout
        void BuildWorkPage()
        {
            pageWork = new Panel { Dock = DockStyle.Fill, Visible = false };
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));

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
            main.Controls.Add(head, 0, 0);

            // sources
            var src = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top };
            src.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            src.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            src.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            src.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            src.Controls.Add(L("Step 3 of 3:   Test Lab folder"), 0, 0);
            src.Controls.Add(txtLabFolder = T(), 1, 0);
            btnPickLab = B("Browse ALM...");
            btnPickLab.Click += OnPickLabFolder;
            src.Controls.Add(btnPickLab, 2, 0);
            src.Controls.Add(L("Image folder"), 0, 1);
            src.Controls.Add(txtFolder = T(), 1, 1);
            var browse = B("Browse...");
            browse.Click += (s, e) => BrowseLocal(txtFolder);
            src.Controls.Add(browse, 2, 1);
            btnLoad = B("Load", true);
            btnLoad.Click += OnLoad;
            src.Controls.Add(btnLoad, 3, 0);
            src.SetRowSpan(btnLoad, 2);
            btnLoad.Dock = DockStyle.Fill;
            main.Controls.Add(src, 0, 1);

            // images (left) | test cases (right)
            split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            split.Panel1.Controls.Add(BuildImagesPanel());
            split.Panel2.Controls.Add(BuildTestsPanel());
            main.Controls.Add(split, 0, 2);

            // bottom: summary + upload
            var bottom = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 9, 3, 3), Text = "Choose the Test Lab folder and the image folder, then press Load." };
            btnUpload = B("Upload assigned images", true);
            btnStop = B("Stop");
            btnStop.Enabled = false;
            btnUpload.Click += OnUpload;
            btnStop.Click += (s, e) => { stopRequested = true; Log("Stopping after the current image..."); };
            bottom.Controls.Add(lblSummary, 0, 0);
            bottom.Controls.Add(btnUpload, 1, 0);
            bottom.Controls.Add(btnStop, 2, 0);
            main.Controls.Add(bottom, 0, 3);

            txtLog = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window,
            };
            main.Controls.Add(txtLog, 0, 4);
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
            EnableAssignUi(false);
            Controls.Add(pageWork);
        }

        Control BuildImagesPanel()
        {
            var p = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize);
            p.RowStyles[2] = new RowStyle(SizeType.Percent, 58);
            p.RowStyles[4] = new RowStyle(SizeType.Percent, 42);
            p.Controls.Add(new Label { Text = "Images", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) }, 0, 0);

            txtImgFilter = new TextBox { Width = 170, Margin = new Padding(3, 4, 3, 3) };
            chkOnlyUnassigned = new CheckBox { Text = "Only not assigned", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
            chkHideUploaded = new CheckBox { Text = "Hide uploaded", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
            txtImgFilter.TextChanged += (s, e) => RefreshImages();
            chkOnlyUnassigned.CheckedChanged += (s, e) => RefreshImages();
            chkHideUploaded.CheckedChanged += (s, e) => RefreshImages();
            p.Controls.Add(Flow(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) },
                                txtImgFilter, chkOnlyUnassigned, chkHideUploaded), 0, 1);

            lvImages = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
                MultiSelect = true, GridLines = true,
            };
            lvImages.Columns.Add("Image", 190);
            lvImages.Columns.Add("Test case (assigned / suggested)", 260);
            lvImages.Columns.Add("Status", 80);
            lvImages.SelectedIndexChanged += (s, e) => OnImageSelected();
            lvImages.ItemDrag += (s, e) => { if (lvImages.SelectedItems.Count > 0) lvImages.DoDragDrop(SelectedImages(), DragDropEffects.Copy); };
            lvImages.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem it in lvImages.Items) it.Selected = true; }
            };
            p.Controls.Add(lvImages, 0, 2);

            lblPreview = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 0), MaximumSize = new Size(560, 0) };
            p.Controls.Add(lblPreview, 0, 3);
            picPreview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.ControlDark };
            p.Controls.Add(picPreview, 0, 4);

            btnAccept = B("Accept suggestion");
            btnUnassign = B("Remove assignment");
            btnAccept.Click += (s, e) => AcceptSuggestions();
            btnUnassign.Click += (s, e) => UnassignSelected();
            p.Controls.Add(Flow(btnAccept, btnUnassign,
                new Label { Text = "Tip: Ctrl/Shift+click to pick many images, then drag them onto a test case.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 9, 3, 3) }), 0, 5);
            return p;
        }

        Control BuildTestsPanel()
        {
            var p = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize);
            p.RowStyles[2] = new RowStyle(SizeType.Percent, 68);
            p.RowStyles[5] = new RowStyle(SizeType.Percent, 32);
            p.Controls.Add(new Label { Text = "Test sets and test cases", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) }, 0, 0);
            txtTestFilter = new TextBox { Width = 220, Margin = new Padding(3, 4, 3, 3) };
            txtTestFilter.TextChanged += (s, e) => RefreshTree();
            p.Controls.Add(Flow(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) }, txtTestFilter), 0, 1);

            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true };
            tree.AfterSelect += (s, e) => OnCaseSelected();
            tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is TestInstance) AssignSelectedTo((TestInstance)e.Node.Tag); };
            tree.DragOver += (s, e) =>
            {
                var node = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
                e.Effect = node != null && node.Tag is TestInstance && e.Data.GetDataPresent(typeof(List<ImageItem>))
                           ? DragDropEffects.Copy : DragDropEffects.None;
                if (node != null && node.Tag is TestInstance) tree.SelectedNode = node;
            };
            tree.DragDrop += (s, e) =>
            {
                var node = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
                var dropped = e.Data.GetData(typeof(List<ImageItem>)) as List<ImageItem>;
                if (node != null && node.Tag is TestInstance && dropped != null)
                    Assign(dropped, (TestInstance)node.Tag);
            };
            p.Controls.Add(tree, 0, 2);

            btnAssign = B("<  Assign selected images to this test case", true);
            btnAssign.Click += (s, e) =>
            {
                var inst = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as TestInstance;
                if (inst == null) { Say("Select a test case (not a test set) on the right first."); return; }
                AssignSelectedTo(inst);
            };
            p.Controls.Add(Flow(btnAssign), 0, 3);

            lblCase = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 0), Text = "Images in the selected test case:" };
            p.Controls.Add(lblCase, 0, 4);
            lstCaseImages = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
            p.Controls.Add(lstCaseImages, 0, 5);
            btnRemoveFromCase = B("Remove selected from this test case");
            btnRemoveFromCase.Click += (s, e) => RemoveFromCase();
            p.Controls.Add(Flow(btnRemoveFromCase), 0, 6);
            return p;
        }

        void BrowseLocal(TextBox target)
        {
            using (var d = new FolderBrowserDialog())
            {
                d.SelectedPath = target.Text;
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

        protected virtual void Say(string msg, bool warning = false)
        {
            MessageBox.Show(this, msg, AppName, MessageBoxButtons.OK, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        protected virtual bool Confirm(string question)
        {
            return MessageBox.Show(this, question, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // ------------------------------------------------------------ load
        void OnLoad(object sender, EventArgs e)
        {
            if (Busy()) return;
            if (txtLabFolder.Text.Trim() == "") { Say("Choose the Test Lab folder first (press 'Browse ALM...')."); return; }
            var folder = txtFolder.Text.Trim();
            if (!Directory.Exists(folder)) { Say("Image folder not found:\n" + folder); return; }
            SaveSettings();
            string labArg = labFolderId ?? txtLabFolder.Text.Trim(), labText = txtLabFolder.Text.Trim();
            string domain = client.Domain, project = client.Project;
            SetBusy(true);
            RunBackground(() =>
            {
                Log("Reading Test Lab folder '" + labText + "' ...");
                var l = new TestLabFolder(client, labArg);
                Log(string.Format("Found {0} test set(s) with {1} test case(s).", l.Sets.Count, l.InstanceCount));
                var imgs = Util.ListImages(folder).Select(f => new ImageItem { File = f, Rel = Util.RelPath(folder, f) }).ToList();
                AssignmentStore.Load(folder, domain, project, l, imgs);
                int fromFolders = 0;
                foreach (var img in imgs.Where(i => i.Assigned.Count == 0))
                {
                    var r = l.Resolve(folder, img.File);    // already sorted into <test set>\<test case> folders
                    if (r.Instance != null) { img.Assigned.Add(r.Instance); fromFolders++; }
                }
                var sug = new Suggester(l);
                foreach (var img in imgs.Where(i => i.Assigned.Count == 0))
                    img.Suggestion = sug.Suggest(img.Rel);
                Log(string.Format("Found {0} image(s): {1} already assigned, {2} with a suggestion.",
                    imgs.Count, imgs.Count(i => i.Assigned.Count > 0), imgs.Count(i => i.Suggestion != null)));
                if (fromFolders > 0) Log(fromFolders + " image(s) assigned from their <test set>\\<test case> folder.");
                Ui(() =>
                {
                    lab = l;
                    imageRoot = folder;
                    images = imgs;
                    if (fromFolders > 0) SaveAssignments();
                    EnableAssignUi(true);
                    RefreshImages();
                    RefreshTree();
                    UpdateSummary();
                });
            }, err => Log("ERROR: " + err), () => Ui(() => SetBusy(false)));
        }

        void ClearLoaded()
        {
            lab = null;
            images = new List<ImageItem>();
            if (lvImages == null) return;
            lvImages.Items.Clear();
            tree.Nodes.Clear();
            lstCaseImages.Items.Clear();
            SetPreview(null);
            EnableAssignUi(false);
            lblSummary.Text = "Choose the Test Lab folder and the image folder, then press Load.";
        }

        void EnableAssignUi(bool on)
        {
            foreach (var c in new Control[] { btnAssign, btnAccept, btnUnassign, btnRemoveFromCase, btnUpload })
                c.Enabled = on;
        }

        // ------------------------------------------------------------ lists
        static string CaseText(TestLabFolder l, TestInstance i)
        {
            return l.SetOf(i).Name + "  >  " + i.Label;
        }

        List<ImageItem> SelectedImages()
        {
            return lvImages.SelectedItems.Cast<ListViewItem>().Select(it => (ImageItem)it.Tag).ToList();
        }

        void FillImageItem(ListViewItem it)
        {
            var img = (ImageItem)it.Tag;
            string target, status;
            Color color = SystemColors.WindowText;
            if (img.Assigned.Count > 0)
            {
                target = string.Join(";  ", img.Assigned.Select(i => CaseText(lab, i)));
                int up = img.Assigned.Count(i => img.Uploaded.Contains(i.Id));
                status = up == img.Assigned.Count ? "uploaded" : up > 0 ? up + "/" + img.Assigned.Count + " uploaded" : "to upload";
                if (up == img.Assigned.Count) color = Done;
            }
            else if (img.Suggestion != null)
            {
                target = "suggest:  " + CaseText(lab, img.Suggestion);
                status = "";
                color = Hint;
            }
            else
            {
                target = "";
                status = "not assigned";
                color = Warn;
            }
            it.SubItems[1].Text = target;
            it.SubItems[2].Text = status;
            it.ForeColor = color;
        }

        void RefreshImages()
        {
            if (lab == null) return;
            var q = Util.Norm(txtImgFilter.Text);
            var selected = new HashSet<ImageItem>(SelectedImages());
            lvImages.BeginUpdate();
            lvImages.Items.Clear();
            foreach (var img in images)
            {
                if (chkOnlyUnassigned.Checked && img.Assigned.Count > 0) continue;
                if (chkHideUploaded.Checked && img.FullyUploaded) continue;
                if (q != "" && !Util.Norm(img.Rel).Contains(q)) continue;
                var it = new ListViewItem(new[] { img.Rel, "", "" }) { Tag = img };
                FillImageItem(it);
                lvImages.Items.Add(it);
                if (selected.Contains(img)) it.Selected = true;
            }
            lvImages.EndUpdate();
        }

        void RefreshImageRows(IEnumerable<ImageItem> changed)
        {
            var set = new HashSet<ImageItem>(changed);
            foreach (ListViewItem it in lvImages.Items)
                if (set.Contains((ImageItem)it.Tag)) FillImageItem(it);
        }

        int CountFor(TestInstance inst)
        {
            return images.Count(i => i.Assigned.Contains(inst));
        }

        string NodeText(TestInstance inst)
        {
            int n = CountFor(inst);
            return n == 0 ? inst.Label : string.Format("{0}   ({1} image{2})", inst.Label, n, n == 1 ? "" : "s");
        }

        string NodeText(TestSet ts)
        {
            int n = ts.Instances.Count(i => CountFor(i) > 0);
            return string.Format("{0}   ({1}/{2} test cases have images)", ts.PathText, n, ts.Instances.Count);
        }

        void RefreshTree()
        {
            if (lab == null) return;
            var q = Util.Norm(txtTestFilter.Text);
            var selected = tree.SelectedNode == null ? null : tree.SelectedNode.Tag;
            var expanded = new HashSet<object>(tree.Nodes.Cast<TreeNode>().Where(n => n.IsExpanded).Select(n => n.Tag));
            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode select = null;
            foreach (var ts in lab.Sets)
            {
                bool setMatch = q == "" || Util.Norm(ts.PathText).Contains(q);
                var cases = ts.Instances.Where(i => setMatch || Util.Norm(i.Label).Contains(q)).ToList();
                if (cases.Count == 0) continue;
                var sn = tree.Nodes.Add(NodeText(ts));
                sn.Tag = ts;
                foreach (var inst in cases)
                {
                    var cn = sn.Nodes.Add(NodeText(inst));
                    cn.Tag = inst;
                    if (CountFor(inst) > 0) cn.ForeColor = Done;
                    if (inst == selected) select = cn;
                }
                if (ts == selected) select = sn;
                if (q != "" || expanded.Contains(ts)) sn.Expand();
            }
            tree.EndUpdate();
            if (select != null) { tree.SelectedNode = select; select.EnsureVisible(); }
        }

        void RefreshTreeCounts()
        {
            foreach (TreeNode sn in tree.Nodes)
            {
                sn.Text = NodeText((TestSet)sn.Tag);
                foreach (TreeNode cn in sn.Nodes)
                {
                    var inst = (TestInstance)cn.Tag;
                    cn.Text = NodeText(inst);
                    cn.ForeColor = CountFor(inst) > 0 ? Done : SystemColors.WindowText;
                }
            }
        }

        void UpdateSummary()
        {
            if (lab == null) return;
            int assigned = images.Count(i => i.Assigned.Count > 0);
            int pairs = images.Sum(i => i.Assigned.Count);
            int uploaded = images.Sum(i => i.Uploaded.Count(id => i.Assigned.Any(a => a.Id == id)));
            int cases = lab.Sets.Sum(s => s.Instances.Count(inst => CountFor(inst) > 0));
            lblSummary.Text = string.Format(
                "{0} images:  {1} assigned,  {2} not assigned.     {3} of {4} test cases have images.     {5} of {6} attachments uploaded.",
                images.Count, assigned, images.Count - assigned, cases, lab.InstanceCount, uploaded, pairs);
        }

        void OnImageSelected()
        {
            var sel = SelectedImages();
            if (sel.Count != 1)
            {
                SetPreview(null);
                lblPreview.Text = sel.Count > 1 ? sel.Count + " images selected" : "";
                return;
            }
            var img = sel[0];
            SetPreview(img.File);
            lblPreview.Text = img.Rel + (img.Assigned.Count > 0 ? "   ->   " + string.Join(";  ", img.Assigned.Select(i => CaseText(lab, i))) : "");
            var show = img.Assigned.Count > 0 ? img.Assigned[0] : img.Suggestion;
            if (show != null) SelectCase(show);
        }

        void SelectCase(TestInstance inst)
        {
            foreach (TreeNode sn in tree.Nodes)
                foreach (TreeNode cn in sn.Nodes)
                    if (cn.Tag == inst) { tree.SelectedNode = cn; cn.EnsureVisible(); return; }
        }

        void SetPreview(string file)
        {
            var old = picPreview.Image;
            picPreview.Image = null;
            if (old != null) old.Dispose();
            if (file == null) return;
            try
            {
                // copy into memory so the file is not kept locked
                using (var fs = File.OpenRead(file))
                using (var img = Image.FromStream(fs))
                    picPreview.Image = new Bitmap(img);
            }
            catch (Exception) { }
        }

        void OnCaseSelected()
        {
            lstCaseImages.Items.Clear();
            var inst = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as TestInstance;
            if (inst == null)
            {
                lblCase.Text = "Images in the selected test case:";
                return;
            }
            lblCase.Text = "Images in  " + CaseText(lab, inst) + "  (test case ID " + inst.Id + "):";
            foreach (var img in images.Where(i => i.Assigned.Contains(inst)))
                lstCaseImages.Items.Add(new CaseImage { Image = img, Uploaded = img.Uploaded.Contains(inst.Id) });
        }

        class CaseImage
        {
            public ImageItem Image;
            public bool Uploaded;
            public override string ToString() { return Image.Rel + (Uploaded ? "   (uploaded)" : ""); }
        }

        // ------------------------------------------------------ assigning
        void AssignSelectedTo(TestInstance inst)
        {
            var sel = SelectedImages();
            if (sel.Count == 0) { Say("Select one or more images on the left first."); return; }
            Assign(sel, inst);
        }

        void Assign(List<ImageItem> imgs, TestInstance inst)
        {
            if (Busy()) return;
            foreach (var img in imgs.Where(i => !i.Assigned.Contains(inst)))
                img.Assigned.Add(inst);
            Log(string.Format("Assigned {0} image(s) to {1}", imgs.Count, CaseText(lab, inst)));
            AfterChange(imgs);
        }

        void AcceptSuggestions()
        {
            if (Busy()) return;
            var sel = SelectedImages().Where(i => i.Assigned.Count == 0 && i.Suggestion != null).ToList();
            if (sel.Count == 0) { Say("Select images that show a 'suggest:' test case first."); return; }
            foreach (var img in sel) img.Assigned.Add(img.Suggestion);
            Log(string.Format("Accepted the suggestion for {0} image(s).", sel.Count));
            AfterChange(sel);
        }

        void UnassignSelected()
        {
            if (Busy()) return;
            var sel = SelectedImages();
            // uploaded ones stay: they are already in ALM
            int removed = 0;
            foreach (var img in sel)
                removed += img.Assigned.RemoveAll(i => !img.Uploaded.Contains(i.Id));
            if (removed > 0) Log(string.Format("Removed {0} assignment(s) that were not uploaded yet.", removed));
            AfterChange(sel);
        }

        void RemoveFromCase()
        {
            if (Busy()) return;
            var inst = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as TestInstance;
            if (inst == null) return;
            var sel = lstCaseImages.SelectedItems.Cast<CaseImage>().Select(c => c.Image).ToList();
            var notUploaded = sel.Where(i => !i.Uploaded.Contains(inst.Id)).ToList();
            foreach (var img in notUploaded) img.Assigned.Remove(inst);
            if (notUploaded.Count < sel.Count) Say("Images that are already uploaded stay in ALM and are kept in the list.");
            AfterChange(notUploaded);
        }

        void AfterChange(IEnumerable<ImageItem> changed)
        {
            SaveAssignments();
            if (chkOnlyUnassigned.Checked) RefreshImages(); else RefreshImageRows(changed);
            RefreshTreeCounts();
            OnCaseSelected();
            UpdateSummary();
        }

        void SaveAssignments()
        {
            try { AssignmentStore.Save(imageRoot, client.Domain, client.Project, lab, images); }
            catch (Exception e) { Log("Could not save assignments to the image folder: " + e.Message); }
        }

        // ------------------------------------------------------------ upload
        void OnUpload(object sender, EventArgs e)
        {
            if (Busy() || lab == null) return;
            var todo = images.SelectMany(img => img.Assigned.Where(i => !img.Uploaded.Contains(i.Id))
                                                             .Select(i => new KeyValuePair<ImageItem, TestInstance>(img, i))).ToList();
            if (todo.Count == 0) { Say("Nothing to upload: assign images to test cases first."); return; }
            int cases = todo.Select(p => p.Value).Distinct().Count();
            var q = string.Format("Upload {0} image attachment(s) to {1} test case(s) in ALM?", todo.Count, cases);
            if (!Confirm(q)) return;
            stopRequested = false;
            SetBusy(true);
            var l = lab;
            RunBackground(() =>
            {
                var up = new Uploader(client, imageRoot, null, Log);
                Log(string.Format("Uploading {0} attachment(s)...", todo.Count));
                foreach (var pair in todo)
                {
                    if (stopRequested) break;
                    if (up.UploadTo(pair.Key.File, l.SetOf(pair.Value), pair.Value))
                    {
                        var img = pair.Key;
                        Ui(() =>
                        {
                            img.Uploaded.Add(pair.Value.Id);
                            SaveAssignments();
                            RefreshImageRows(new[] { img });
                            UpdateSummary();
                        });
                    }
                }
                Log(string.Format("Done: {0} uploaded, {1} already there, {2} failed.", up.Ok, up.Skipped, up.Failed));
                Ui(() =>
                {
                    OnCaseSelected();
                    if (chkHideUploaded.Checked) RefreshImages();
                    Say(string.Format("{0} uploaded, {1} already there, {2} failed.", up.Ok, up.Skipped, up.Failed), up.Failed > 0);
                });
            }, err => Log("ERROR: " + err), () => Ui(() => SetBusy(false)));
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

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (Busy())
            {
                if (!Confirm("Still working. Stop and exit?"))
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
            btnLoad.Enabled = btnPickLab.Enabled = !busy;
            EnableAssignUi(!busy && lab != null);
            btnStop.Enabled = busy;
            UseWaitCursor = busy;
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
