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
        static readonly Color Done = Color.ForestGreen, Hint = Color.SteelBlue, HintWeak = Color.MediumPurple,
                              Warn = Color.DarkOrange, NaColor = Color.SlateGray;

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
        ComboBox cboImgFilter, cboTreeSort, cboTreeFilter;
        int imgSortCol;           // 0 image, 1 test case, 2 status
        bool imgSortDesc;
        AlmClient.FieldDef commentField;   // the "Comments" column, shown as [NA] etc. in the tree
        Button btnPickLab, btnLoad, btnAssign, btnAccept, btnUnassign, btnClearAssign, btnRemoveFromCase, btnUpload, btnStop,
               btnUntick, btnTickAll, btnSetField, btnDownload, btnSelectAll, btnDeselectAll;
        Label lblTicked, lblImagesTitle;
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
        readonly HashSet<TestInstance> ticked = new HashSet<TestInstance>();
        bool syncingChecks;

        Thread worker;
        volatile bool stopRequested;

        public MainForm()
        {
            Text = AppName + "  v" + AppInfo.Version;
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
            sub.Text = "Enter the ALM address and your ALM username and password.      (version " + AppInfo.Version + ")";
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
            lblImagesTitle = new Label { Text = "Images", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) };
            p.Controls.Add(lblImagesTitle, 0, 0);

            txtImgFilter = new TextBox { Width = 105, Margin = new Padding(3, 4, 3, 3) };
            cboImgFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(3, 4, 3, 3) };
            cboImgFilter.Items.AddRange(new object[] { "All images", "Not assigned", "With suggestion", "Assigned, not uploaded", "Uploaded" });
            cboImgFilter.SelectedIndex = 0;
            txtImgFilter.TextChanged += (s, e) => RefreshImages();
            cboImgFilter.SelectedIndexChanged += (s, e) => RefreshImages();
            btnSelectAll = B("Select all");
            btnDeselectAll = B("Deselect all");
            btnSelectAll.Click += (s, e) => SelectAllImages(true);
            btnDeselectAll.Click += (s, e) => SelectAllImages(false);
            p.Controls.Add(Flow(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) }, txtImgFilter,
                                new Label { Text = "Show:", AutoSize = true, Margin = new Padding(8, 7, 0, 3) }, cboImgFilter,
                                btnSelectAll, btnDeselectAll), 0, 1);

            lvImages = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
                MultiSelect = true, GridLines = true,
            };
            lvImages.Columns.Add("Image", 190);
            lvImages.Columns.Add("Test case (assigned / suggested)", 260);
            lvImages.Columns.Add("Status", 80);
            lvImages.SelectedIndexChanged += (s, e) => OnImageSelected();
            lvImages.ColumnClick += (s, e) =>
            {
                // click a column title to sort by it, again to reverse
                imgSortDesc = imgSortCol == e.Column && !imgSortDesc;
                imgSortCol = e.Column;
                RefreshImages();
            };
            lvImages.ItemDrag += (s, e) => { if (lvImages.SelectedItems.Count > 0) lvImages.DoDragDrop(SelectedImages(), DragDropEffects.Copy); };
            lvImages.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.A) SelectAllImages(true); };
            p.Controls.Add(lvImages, 0, 2);

            lblPreview = new Label { AutoSize = true, Margin = new Padding(3, 6, 3, 0), MaximumSize = new Size(560, 0) };
            p.Controls.Add(lblPreview, 0, 3);
            picPreview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = SystemColors.ControlDark };
            p.Controls.Add(picPreview, 0, 4);

            btnAccept = B("Accept suggestion");
            btnUnassign = B("Remove assignment");
            btnClearAssign = B("Clear all assignments");
            btnAccept.Click += (s, e) => AcceptSuggestions();
            btnUnassign.Click += (s, e) => UnassignSelected();
            btnClearAssign.Click += (s, e) => ClearAllAssignments();
            p.Controls.Add(Flow(btnAccept, btnUnassign, btnClearAssign,
                new Label { Text = "Tip: Ctrl/Shift+click or Ctrl+A to pick many images.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 9, 3, 3) }), 0, 5);
            return p;
        }

        Control BuildTestsPanel()
        {
            var p = Rows(SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize);
            p.RowStyles[2] = new RowStyle(SizeType.Percent, 68);
            p.RowStyles[5] = new RowStyle(SizeType.Percent, 32);
            p.Controls.Add(new Label { Text = "Test sets and test cases", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) }, 0, 0);
            txtTestFilter = new TextBox { Width = 110, Margin = new Padding(3, 4, 3, 3) };
            txtTestFilter.TextChanged += (s, e) => RefreshTree();
            cboTreeSort = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125, Margin = new Padding(3, 4, 3, 3) };
            cboTreeSort.Items.AddRange(new object[] { SortNameAsc, SortNameDesc, SortMostImages, SortFewestImages, SortId, SortAlm });
            cboTreeSort.SelectedIndex = 0;
            cboTreeSort.SelectedIndexChanged += (s, e) => RefreshTree();
            cboTreeFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Margin = new Padding(3, 4, 3, 3) };
            cboTreeFilter.SelectedIndexChanged += (s, e) => RefreshTree();
            FillTreeFilter();
            p.Controls.Add(Flow(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) }, txtTestFilter,
                                new Label { Text = "Sort:", AutoSize = true, Margin = new Padding(6, 7, 0, 3) }, cboTreeSort,
                                new Label { Text = "Show:", AutoSize = true, Margin = new Padding(6, 7, 0, 3) }, cboTreeFilter), 0, 1);

            tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true, CheckBoxes = true };
            tree.AfterSelect += (s, e) => OnCaseSelected();
            tree.AfterCheck += OnNodeChecked;
            tree.NodeMouseDoubleClick += (s, e) => { if (e.Node.Tag is TestInstance) AssignSelectedTo(new List<TestInstance> { (TestInstance)e.Node.Tag }); };
            tree.DragOver += (s, e) =>
            {
                var node = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
                e.Effect = node != null && e.Data.GetDataPresent(typeof(List<ImageItem>)) ? DragDropEffects.Copy : DragDropEffects.None;
                if (node != null) tree.SelectedNode = node;
            };
            tree.DragDrop += (s, e) =>
            {
                var node = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y)));
                var dropped = e.Data.GetData(typeof(List<ImageItem>)) as List<ImageItem>;
                if (node == null || dropped == null) return;
                // onto a test set: every test case in it
                var targets = node.Tag is TestSet ? ((TestSet)node.Tag).Instances.ToList() : new List<TestInstance> { (TestInstance)node.Tag };
                Assign(dropped, targets);
            };
            p.Controls.Add(tree, 0, 2);

            btnAssign = B("<  Assign selected images to ticked test cases", true);
            btnAssign.Click += (s, e) => AssignSelectedTo(Targets());
            btnTickAll = B("Tick all");
            btnTickAll.Click += (s, e) => TickAllShown();
            btnUntick = B("Untick all");
            btnUntick.Click += (s, e) => { ticked.Clear(); RefreshTree(); UpdateTicked(); };
            btnSetField = B("Set field (e.g. Comments)...");
            btnSetField.Click += (s, e) => OpenSetField();
            btnDownload = B("Download attachments...");
            btnDownload.Click += (s, e) => OpenDownload();
            lblTicked = new Label { AutoSize = true, Margin = new Padding(6, 9, 3, 3), ForeColor = SystemColors.GrayText };
            p.Controls.Add(Flow(btnAssign, btnTickAll, btnUntick, btnSetField, btnDownload, lblTicked), 0, 3);

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
                AlmClient.FieldDef cf = null;
                try
                {
                    cf = client.GetFieldDefs("test-instance")
                               .FirstOrDefault(f => f.Active && f.Label.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch (Exception ex)
                {
                    if (!(ex is AlmException || ex is WebException || ex is System.Xml.XmlException)) throw;
                    Log("Could not read the test case fields (the Comments indicator is off): " + ex.Message);
                }
                var l = new TestLabFolder(client, labArg, cf == null ? null : new[] { cf.Name });
                if (cf != null) Log("Showing the '" + cf.Label + "' column of each test case, e.g. [NA].");
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
                foreach (var img in imgs)
                {
                    bool weak;
                    img.NameSuggestion = sug.Suggest(img.Rel, out weak);
                    img.NameSuggestionWeak = weak;
                }
                ImageItem.RefreshSuggestions(imgs);
                Log(string.Format("Found {0} image(s): {1} already assigned, {2} suggested, {3} maybe, {4} without a suggestion.",
                    imgs.Count, imgs.Count(i => i.Assigned.Count > 0),
                    imgs.Count(i => i.Suggestion != null && !i.SuggestWeak), imgs.Count(i => i.Suggestion != null && i.SuggestWeak),
                    imgs.Count(i => i.Assigned.Count == 0 && i.Suggestion == null)));
                if (fromFolders > 0) Log(fromFolders + " image(s) assigned from their <test set>\\<test case> folder.");
                Ui(() =>
                {
                    lab = l;
                    imageRoot = folder;
                    images = imgs;
                    ticked.Clear();
                    commentField = cf;
                    FillTreeFilter();
                    // a new load always starts with every image and test case visible
                    txtImgFilter.Text = "";
                    cboImgFilter.SelectedIndex = 0;
                    txtTestFilter.Text = "";
                    cboTreeFilter.SelectedIndex = 0;
                    if (fromFolders > 0) SaveAssignments();
                    EnableAssignUi(true);
                    RefreshImages();
                    RefreshTree();
                    UpdateSummary();
                    UpdateTicked();
                });
            }, err => Log("ERROR: " + err), () => Ui(() => SetBusy(false)));
        }

        void ClearLoaded()
        {
            lab = null;
            commentField = null;
            images = new List<ImageItem>();
            ticked.Clear();
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
            foreach (var c in new Control[] { btnAssign, btnAccept, btnUnassign, btnClearAssign, btnRemoveFromCase, btnUpload, btnUntick, btnTickAll, btnSetField, btnDownload,
                                              btnSelectAll, btnDeselectAll })
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
                var cases = string.Join(";  ", img.Suggested.Select(i => CaseText(lab, i)));
                if (img.SuggestLike != null)
                    target = "suggest (like " + img.SuggestLike + "):  " + cases;
                else
                    target = (img.SuggestWeak ? "maybe:  " : "suggest:  ") + cases;
                status = img.SuggestWeak ? "maybe" : "suggested";
                color = img.SuggestWeak ? HintWeak : Hint;
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
            var rows = new List<ListViewItem>();
            foreach (var img in images)
            {
                if (!ImageFilter(img)) continue;
                if (q != "" && !Util.Norm(img.Rel).Contains(q)) continue;
                var it = new ListViewItem(new[] { img.Rel, "", "" }) { Tag = img };
                FillImageItem(it);
                rows.Add(it);
            }
            Comparison<ListViewItem> cmp;
            if (imgSortCol == 2)
                cmp = (x, y) => StatusRank((ImageItem)x.Tag).CompareTo(StatusRank((ImageItem)y.Tag));
            else
                cmp = (x, y) => Util.NaturalCompare(x.SubItems[imgSortCol].Text, y.SubItems[imgSortCol].Text);
            rows.Sort((x, y) =>
            {
                int c = cmp(x, y);
                if (c == 0) c = Util.NaturalCompare(x.Text, y.Text);
                return imgSortDesc ? -c : c;
            });
            lvImages.Items.AddRange(rows.ToArray());
            lblImagesTitle.Text = rows.Count == images.Count ? string.Format("Images ({0})", images.Count)
                                                             : string.Format("Images  (showing {0} of {1})", rows.Count, images.Count);
            foreach (var it in rows) if (selected.Contains((ImageItem)it.Tag)) it.Selected = true;
            string[] titles = { "Image", "Test case (assigned / suggested)", "Status" };
            for (int c = 0; c < titles.Length; c++)
                lvImages.Columns[c].Text = titles[c] + (c == imgSortCol ? (imgSortDesc ? "  \u25BC" : "  \u25B2") : "");
            lvImages.EndUpdate();
        }

        bool ImageFilter(ImageItem img)
        {
            switch (cboImgFilter.SelectedIndex)
            {
                case 1: return img.Assigned.Count == 0;
                case 2: return img.Assigned.Count == 0 && img.Suggestion != null;
                case 3: return img.Assigned.Count > 0 && !img.FullyUploaded;
                case 4: return img.FullyUploaded;
                default: return true;
            }
        }

        static int StatusRank(ImageItem img)
        {
            if (img.Assigned.Count == 0) return img.Suggestion == null ? 0 : 1;   // not assigned, suggested
            return img.FullyUploaded ? 3 : 2;                                      // to upload, uploaded
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

        string Comment(TestInstance inst)
        {
            string v;
            return commentField != null && inst.Values.TryGetValue(commentField.Name, out v) ? (v ?? "").Trim() : "";
        }

        bool IsNA(TestInstance inst)
        {
            var c = Comment(inst).Replace("/", "");
            return c.Equals("NA", StringComparison.OrdinalIgnoreCase);
        }

        string NodeText(TestInstance inst)
        {
            int n = CountFor(inst);
            var text = n == 0 ? inst.Label : string.Format("{0}   ({1} image{2})", inst.Label, n, n == 1 ? "" : "s");
            var c = Comment(inst);
            return c == "" ? text : text + "   [" + c + "]";
        }

        string NodeText(TestSet ts)
        {
            int n = ts.Instances.Count(i => CountFor(i) > 0);
            int na = ts.Instances.Count(IsNA);
            return string.Format("{0}   ({1}/{2} test cases have images{3})", ts.PathText, n, ts.Instances.Count,
                                 na > 0 ? ",  " + na + " NA" : "");
        }

        Color CaseColor(TestInstance inst)
        {
            if (CountFor(inst) > 0) return Done;
            return IsNA(inst) ? NaColor : SystemColors.WindowText;
        }

        // ---- tree sorting and filtering
        const string SortNameAsc = "Name A to Z", SortNameDesc = "Name Z to A", SortMostImages = "Most images first",
                     SortFewestImages = "Fewest images first", SortId = "Test case ID", SortAlm = "ALM test order";

        class TreeFilter
        {
            public string Text;
            public Func<TestInstance, bool> Match;
            public override string ToString() { return Text; }
        }

        void FillTreeFilter()
        {
            var keep = cboTreeFilter.SelectedItem == null ? null : cboTreeFilter.SelectedItem.ToString();
            var items = new List<TreeFilter>
            {
                new TreeFilter { Text = "All test cases", Match = i => true },
                new TreeFilter { Text = "With images", Match = i => CountFor(i) > 0 },
                new TreeFilter { Text = "Without images", Match = i => CountFor(i) == 0 },
                new TreeFilter { Text = "Ticked", Match = i => ticked.Contains(i) },
                new TreeFilter { Text = "Waiting for upload", Match = i => images.Any(img => img.Assigned.Contains(i) && !img.Uploaded.Contains(i.Id)) },
            };
            if (commentField != null && lab != null)
            {
                var label = commentField.Label;
                items.Add(new TreeFilter { Text = label + ": NA", Match = IsNA });
                items.Add(new TreeFilter { Text = label + ": not NA", Match = i => !IsNA(i) });
                items.Add(new TreeFilter { Text = label + ": (empty)", Match = i => Comment(i) == "" });
                foreach (var v in lab.Sets.SelectMany(s => s.Instances).Select(Comment)
                                     .Where(v => v != "" && !v.Replace("/", "").Equals("NA", StringComparison.OrdinalIgnoreCase))
                                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                {
                    var value = v;
                    items.Add(new TreeFilter { Text = label + ": " + value, Match = i => Comment(i).Equals(value, StringComparison.OrdinalIgnoreCase) });
                }
            }
            cboTreeFilter.BeginUpdate();
            cboTreeFilter.Items.Clear();
            cboTreeFilter.Items.AddRange(items.ToArray());
            cboTreeFilter.EndUpdate();
            int idx = items.FindIndex(f => f.Text == keep);
            cboTreeFilter.SelectedIndex = idx >= 0 ? idx : 0;
        }

        IEnumerable<TestInstance> SortCases(IEnumerable<TestInstance> cases, Dictionary<TestInstance, int> counts)
        {
            switch (cboTreeSort.SelectedItem as string)
            {
                case SortNameDesc: return cases.OrderByDescending(i => i.Label, Natural);
                case SortMostImages: return cases.OrderByDescending(i => counts[i]).ThenBy(i => i.Label, Natural);
                case SortFewestImages: return cases.OrderBy(i => counts[i]).ThenBy(i => i.Label, Natural);
                case SortId: return cases.OrderBy(i => Util.ToInt(i.Id));
                case SortAlm: return cases;
                default: return cases.OrderBy(i => i.Label, Natural);
            }
        }

        IEnumerable<TestSet> SortSets(IEnumerable<TestSet> sets, Dictionary<TestInstance, int> counts)
        {
            Func<TestSet, int> withImages = ts => ts.Instances.Count(i => counts[i] > 0);
            switch (cboTreeSort.SelectedItem as string)
            {
                case SortNameDesc: return sets.OrderByDescending(s => s.PathText, Natural);
                case SortMostImages: return sets.OrderByDescending(withImages).ThenBy(s => s.PathText, Natural);
                case SortFewestImages: return sets.OrderBy(withImages).ThenBy(s => s.PathText, Natural);
                case SortId: return sets.OrderBy(s => Util.ToInt(s.Id));
                default: return sets.OrderBy(s => s.PathText, Natural);
            }
        }

        static readonly IComparer<string> Natural = Comparer<string>.Create(Util.NaturalCompare);

        void RefreshTree()
        {
            if (lab == null) return;
            var q = Util.Norm(txtTestFilter.Text);
            var selected = tree.SelectedNode == null ? null : tree.SelectedNode.Tag;
            var expanded = new HashSet<object>(tree.Nodes.Cast<TreeNode>().Where(n => n.IsExpanded).Select(n => n.Tag));
            var filter = cboTreeFilter.SelectedItem as TreeFilter;
            var counts = lab.Sets.SelectMany(s => s.Instances).ToDictionary(i => i, CountFor);
            bool narrowed = q != "" || (filter != null && cboTreeFilter.SelectedIndex > 0);
            tree.BeginUpdate();
            tree.Nodes.Clear();
            TreeNode select = null;
            foreach (var ts in SortSets(lab.Sets, counts))
            {
                bool setMatch = q == "" || Util.Norm(ts.PathText).Contains(q);
                var cases = SortCases(ts.Instances.Where(i => (setMatch || Util.Norm(i.Label).Contains(q))
                                                              && (filter == null || filter.Match(i))), counts).ToList();
                if (cases.Count == 0) continue;
                var sn = tree.Nodes.Add(NodeText(ts));
                sn.Tag = ts;
                syncingChecks = true;
                foreach (var inst in cases)
                {
                    var cn = sn.Nodes.Add(NodeText(inst));
                    cn.Tag = inst;
                    cn.Checked = ticked.Contains(inst);
                    cn.ForeColor = CaseColor(inst);
                    if (inst == selected) select = cn;
                }
                sn.Checked = cases.All(ticked.Contains);
                syncingChecks = false;
                if (ts == selected) select = sn;
                if (narrowed || expanded.Contains(ts)) sn.Expand();
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
                    cn.ForeColor = CaseColor(inst);
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
            int na = lab.Sets.Sum(s => s.Instances.Count(IsNA));
            lblSummary.Text = string.Format(
                "{0} images:  {1} assigned,  {2} not assigned.     {3} of {4} test cases have images{7}.     {5} of {6} attachments uploaded.",
                images.Count, assigned, images.Count - assigned, cases, lab.InstanceCount, uploaded, pairs,
                na > 0 ? ",  " + na + " are NA" : "");
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
            lblPreview.Text = img.Rel + (img.Assigned.Count == 0 ? ""
                : img.Assigned.Count <= 3 ? "   ->   " + string.Join(";  ", img.Assigned.Select(i => CaseText(lab, i)))
                : "   ->   assigned to " + img.Assigned.Count + " test cases");
            // the tree selection is left alone: it may be the target the user picked for "Assign"
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
        /// Where "Assign" goes: the ticked test cases; else the selected test set (all its
        /// test cases) or the selected test case.
        List<TestInstance> Targets()
        {
            if (ticked.Count > 0)
                return lab.Sets.SelectMany(s => s.Instances).Where(ticked.Contains).ToList();
            var node = tree.SelectedNode;
            if (node == null) return new List<TestInstance>();
            if (node.Tag is TestSet) return ((TestSet)node.Tag).Instances.ToList();
            return new List<TestInstance> { (TestInstance)node.Tag };
        }

        void AssignSelectedTo(List<TestInstance> targets)
        {
            var sel = SelectedImages();
            if (sel.Count == 0) { Say("Select one or more images on the left first."); return; }
            if (targets.Count == 0) { Say("Tick the test cases or test sets on the right first (or select one)."); return; }
            if (targets.Count > 1 && !Confirm(string.Format("Assign {0} image(s) to each of {1} test cases?", sel.Count, targets.Count)))
                return;
            Assign(sel, targets);
        }

        void Assign(List<ImageItem> imgs, List<TestInstance> targets)
        {
            if (Busy()) return;
            foreach (var inst in targets)
                foreach (var img in imgs.Where(i => !i.Assigned.Contains(inst)))
                    img.Assigned.Add(inst);
            Log(targets.Count == 1
                ? string.Format("Assigned {0} image(s) to {1}", imgs.Count, CaseText(lab, targets[0]))
                : string.Format("Assigned {0} image(s) to each of {1} test cases", imgs.Count, targets.Count));
            AfterChange(imgs);
        }

        void ClearAllAssignments()
        {
            if (Busy()) return;
            int n = images.Sum(i => i.Assigned.Count(a => !i.Uploaded.Contains(a.Id)));
            if (n == 0) { Say("There are no assignments waiting for upload."); return; }
            if (!Confirm(string.Format("Remove all {0} assignment(s) that are not uploaded yet?", n))) return;
            foreach (var img in images) img.Assigned.RemoveAll(a => !img.Uploaded.Contains(a.Id));
            Log(string.Format("Cleared {0} assignment(s).", n));
            AfterChange(images);
        }

        /// Ticks every test case the tree shows now (so a Search / Show filter narrows it down).
        void TickAllShown()
        {
            if (lab == null) return;
            foreach (TreeNode sn in tree.Nodes)
                foreach (TreeNode cn in sn.Nodes)
                    ticked.Add((TestInstance)cn.Tag);
            RefreshTree();
            UpdateTicked();
        }

        /// Selects / deselects every image the list shows now (after Search / Show).
        void SelectAllImages(bool select)
        {
            lvImages.BeginUpdate();
            foreach (ListViewItem it in lvImages.Items) it.Selected = select;
            lvImages.EndUpdate();
            lvImages.Focus();
        }

        void OnNodeChecked(object sender, TreeViewEventArgs e)
        {
            if (syncingChecks) return;
            syncingChecks = true;
            if (e.Node.Tag is TestSet)
            {
                foreach (TreeNode cn in e.Node.Nodes)
                {
                    cn.Checked = e.Node.Checked;
                    if (e.Node.Checked) ticked.Add((TestInstance)cn.Tag); else ticked.Remove((TestInstance)cn.Tag);
                }
            }
            else
            {
                if (e.Node.Checked) ticked.Add((TestInstance)e.Node.Tag); else ticked.Remove((TestInstance)e.Node.Tag);
                e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
            }
            syncingChecks = false;
            UpdateTicked();
        }

        void UpdateTicked()
        {
            lblTicked.Text = ticked.Count == 0 ? "tick test sets / test cases" : ticked.Count + " test case(s) ticked";
            lblTicked.ForeColor = ticked.Count == 0 ? SystemColors.GrayText : Color.RoyalBlue;
        }

        void OpenDownload()
        {
            if (Busy() || lab == null) return;
            var targets = Targets();
            if (targets.Count == 0) { Say("Tick the test cases or test sets to download from first."); return; }
            var dest = Setting("download_folder");
            if (dest == "")
                dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ALM Download");
            using (var dlg = new DownloadDialog(client, lab, targets, dest))
            {
                dlg.ShowDialog(this);
                settings["download_folder"] = dlg.Destination;
                SaveSettings();
            }
        }

        void OpenSetField()
        {
            if (Busy() || lab == null) return;
            var targets = Targets();
            if (targets.Count == 0) { Say("Tick the test cases or test sets whose field you want to change first."); return; }
            using (var dlg = new SetFieldDialog(client, lab, targets))
            {
                dlg.ShowDialog(this);
                ApplyFieldChanges(dlg.Changes);
            }
        }

        /// Keep the [NA] indicator in step with what the Set field dialog changed in ALM.
        void ApplyFieldChanges(List<FieldChange> changes)
        {
            foreach (var ch in changes)
                ch.Instance.Values[ch.Field] = ch.Value;
            if (changes.Count == 0) return;
            FillTreeFilter();
            RefreshTree();
            UpdateSummary();
        }

        void AcceptSuggestions()
        {
            if (Busy()) return;
            var sel = SelectedImages().Where(i => i.Assigned.Count == 0 && i.Suggestion != null).ToList();
            if (sel.Count == 0) { Say("Select images that show a 'suggest' or 'maybe' test case first."); return; }
            foreach (var img in sel) img.Assigned.AddRange(img.Suggested);
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
            ImageItem.RefreshSuggestions(images);   // e.g. "1.3 Login-5" follows "1.3 Login-4"
            if (cboImgFilter.SelectedIndex != 0) RefreshImages(); else RefreshImageRows(images);
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
                    if (cboImgFilter.SelectedIndex != 0) RefreshImages();
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

    /// Sets one field (e.g. the "Comments" selection list) to the same value on many test cases.
    public class FieldChange
    {
        public TestInstance Instance;
        public string Field, Value;
    }

    public class SetFieldDialog : Form
    {
        /// Everything this dialog changed in ALM, in order.
        public readonly List<FieldChange> Changes = new List<FieldChange>();

        // structural fields that must not be changed from here
        static readonly HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "id", "cycle-id", "test-id", "test-order", "test-config-id", "ver-stamp", "last-modified", "subtype-id", "iterations" };

        readonly AlmClient client;
        readonly List<TestInstance> targets;
        protected readonly ComboBox cboField, cboValue;
        protected readonly Button btnApply, btnStop;
        readonly Label lblNow, lblProgress;
        readonly TextBox txtLog;
        Thread worker;
        volatile bool stop;
        int loadSeq;

        public SetFieldDialog(AlmClient client, TestLabFolder lab, List<TestInstance> targets)
        {
            this.client = client;
            this.targets = targets;
            Text = "Set field on test cases";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(620, 460);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int sets = targets.Select(i => i.SetId).Distinct().Count();
            var head = new Label
            {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10),
                Text = string.Format("{0} test case(s) in {1} test set(s)", targets.Count, sets),
            };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 2);
            t.Controls.Add(new Label { Text = "Field", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
            cboField = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            t.Controls.Add(cboField, 1, 1);
            t.Controls.Add(new Label { Text = "New value", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 2);
            cboValue = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
            t.Controls.Add(cboValue, 1, 2);
            lblNow = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 8), MaximumSize = new Size(480, 0) };
            t.Controls.Add(lblNow, 1, 3);
            var row = new FlowLayoutPanel { AutoSize = true };
            btnApply = new Button { Text = "Apply to all " + targets.Count, AutoSize = true, Enabled = false };
            btnApply.Font = new Font(btnApply.Font, FontStyle.Bold);
            btnStop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            btnApply.Click += (s, e) => Apply();
            btnStop.Click += (s, e) => stop = true;
            row.Controls.Add(btnApply);
            row.Controls.Add(btnStop);
            row.Controls.Add(close);
            lblProgress = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3) };
            row.Controls.Add(lblProgress);
            t.Controls.Add(row, 1, 4);
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            t.Controls.Add(txtLog, 0, 5);
            t.SetColumnSpan(txtLog, 2);
            t.RowCount = 6;
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(t);
            CancelButton = close;

            cboField.SelectedIndexChanged += (s, e) => OnFieldChanged();
            cboValue.TextChanged += (s, e) => btnApply.Enabled = cboField.SelectedItem != null && !Busy();
            FormClosing += (s, e) => { if (Busy()) { stop = true; e.Cancel = true; } };
            Shown += (s, e) => LoadFields();
        }

        protected virtual bool Confirm(string q)
        {
            return MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        bool Busy() { return worker != null && worker.IsAlive; }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        void Log(string msg) { Ui(() => txtLog.AppendText(msg + Environment.NewLine)); }

        void Run(Action work, Action after)
        {
            worker = new Thread(() =>
            {
                try { work(); }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                finally { Ui(after); }
            }) { IsBackground = true };
            worker.Start();
        }

        void LoadFields()
        {
            lblNow.Text = "Loading fields from ALM...";
            List<AlmClient.FieldDef> defs = null;
            Run(() => defs = client.GetFieldDefs("test-instance"), () =>
            {
                if (defs == null) { lblNow.Text = "Could not read the fields."; return; }
                var usable = defs.Where(f => f.Editable && f.Active && !Hidden.Contains(f.Name))
                                 .OrderBy(f => f.System).ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var f in usable) cboField.Items.Add(f);
                lblNow.Text = usable.Count == 0 ? "No editable fields found." : "";
                // the Comments column is the usual one
                var pick = usable.FindIndex(f => f.Label.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0);
                if (usable.Count > 0) cboField.SelectedIndex = pick >= 0 ? pick : 0;
            });
        }

        void OnFieldChanged()
        {
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            cboValue.Items.Clear();
            cboValue.Text = "";
            btnApply.Enabled = false;
            if (f == null) return;
            cboValue.DropDownStyle = f.ListId != "" ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
            lblNow.Text = "Loading...";
            int seq = ++loadSeq;
            List<string> values = null;
            Dictionary<string, int> now = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try
                {
                    if (f.ListId != "") values = client.GetListValues(f.ListId);
                    // what the ticked test cases have today
                    now = client.GetByIds("test-instances", "id", targets.Select(i => i.Id), new[] { "id", f.Name })
                                .GroupBy(d => { string v; return d.TryGetValue(f.Name, out v) && v != "" ? v : "(empty)"; })
                                .ToDictionary(g => g.Key, g => g.Count());
                }
                catch (Exception e) { err = e.Message; }
                Ui(() =>
                {
                    if (seq != loadSeq) return;   // another field was chosen meanwhile
                    if (values != null) foreach (var v in values) cboValue.Items.Add(v);
                    lblNow.Text = err != null ? "Could not read current values: " + err
                        : "Now: " + string.Join(",   ", now.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " (" + kv.Value + ")"));
                    if (f.ListId != "" && values != null && values.Count == 0) lblNow.Text += "\nThis selection list is empty.";
                    btnApply.Enabled = !Busy() && (f.ListId == "" || cboValue.Items.Count > 0);
                });
            });
        }

        protected void Apply()
        {
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            if (f == null || Busy()) return;
            var value = cboValue.Text;
            if (f.ListId != "" && value == "") return;
            if (!Confirm(string.Format("Set '{0}' to '{1}' on {2} test case(s)?", f.Label, value == "" ? "(empty)" : value, targets.Count)))
                return;
            stop = false;
            btnApply.Enabled = cboField.Enabled = cboValue.Enabled = false;
            btnStop.Enabled = true;
            int ok = 0, failed = 0;
            Run(() =>
            {
                var change = new Dictionary<string, string> { { f.Name, value } };
                foreach (var inst in targets)
                {
                    if (stop) { Log("Stopped."); break; }
                    try
                    {
                        client.UpdateEntity("test-instances", inst.Id, change);
                        lock (Changes) Changes.Add(new FieldChange { Instance = inst, Field = f.Name, Value = value });
                        ok++;
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException)) throw;
                        failed++;
                        Log("[FAIL] " + inst.Label + " (" + inst.Id + "): " + e.Message);
                    }
                    int done = ok + failed;
                    Ui(() => lblProgress.Text = done + " / " + targets.Count);
                }
                Log(string.Format("Done: '{0}' = '{1}' on {2} test case(s), {3} failed.", f.Label, value, ok, failed));
            }, () =>
            {
                btnStop.Enabled = false;
                cboField.Enabled = cboValue.Enabled = true;
                OnFieldChanged();   // show the new current values
            });
        }
    }

    /// Downloads the attachments of many test cases into <folder>\<test set>\<test case>\,
    /// the same layout "Load" understands, so the files can be uploaded again as they are.
    public class DownloadDialog : Form
    {
        public const string NameNumbered = "Test case name + number      e.g. 1.1_Land_Screen_1.png";
        public const string NameBoth = "Test case name + original name      e.g. 1.1_Land_Screen - Title 12.png";
        public const string NameOriginal = "Original name      e.g. Title 12.png";

        readonly AlmClient client;
        readonly TestLabFolder lab;
        readonly List<TestInstance> targets;
        protected readonly TextBox txtDest, txtLog;
        protected readonly ComboBox cboNaming;
        protected readonly CheckBox chkImagesOnly, chkSkipExisting;
        readonly Button btnGo, btnStop, btnOpen;
        readonly Label lblProgress;
        Thread worker;
        volatile bool stop;

        public string Destination { get { return txtDest.Text.Trim(); } }

        public DownloadDialog(AlmClient client, TestLabFolder lab, List<TestInstance> targets, string destination)
        {
            this.client = client;
            this.lab = lab;
            this.targets = targets;
            Text = "Download attachments";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(680, 500);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var head = new Label
            {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10),
                Text = string.Format("Download the attachments of {0} test case(s) in {1} test set(s)",
                                     targets.Count, targets.Select(i => i.SetId).Distinct().Count()),
            };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 3);
            t.Controls.Add(new Label { Text = "Save to", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
            txtDest = new TextBox { Dock = DockStyle.Fill, Text = destination };
            t.Controls.Add(txtDest, 1, 1);
            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = txtDest.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) txtDest.Text = d.SelectedPath;
            };
            t.Controls.Add(browse, 2, 1);
            t.Controls.Add(new Label { Text = "File names", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 2);
            cboNaming = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            cboNaming.Items.AddRange(new object[] { NameNumbered, NameBoth, NameOriginal });
            cboNaming.SelectedIndex = 0;
            t.Controls.Add(cboNaming, 1, 2);
            t.SetColumnSpan(cboNaming, 2);
            chkImagesOnly = new CheckBox { Text = "Images only (png, jpg, gif, bmp, tif, webp)", AutoSize = true, Checked = true };
            chkSkipExisting = new CheckBox { Text = "Skip files that already exist (to continue an earlier download)", AutoSize = true, Checked = true };
            t.Controls.Add(chkImagesOnly, 1, 3);
            t.SetColumnSpan(chkImagesOnly, 2);
            t.Controls.Add(chkSkipExisting, 1, 4);
            t.SetColumnSpan(chkSkipExisting, 2);
            var layout = new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6), MaximumSize = new Size(540, 0),
                Text = "Saved as  <Save to>\\<test set>\\<test case>\\<file>.  Choose that folder as the image folder and press Load: "
                     + "every file is assigned to its test case again, ready to upload.",
            };
            t.Controls.Add(layout, 1, 5);
            t.SetColumnSpan(layout, 2);
            var row = new FlowLayoutPanel { AutoSize = true };
            btnGo = new Button { Text = "Download", AutoSize = true };
            btnGo.Font = new Font(btnGo.Font, FontStyle.Bold);
            btnStop = new Button { Text = "Stop", AutoSize = true, Enabled = false };
            btnOpen = new Button { Text = "Open folder", AutoSize = true, Enabled = false };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
            btnGo.Click += (s, e) => StartDownload();
            btnStop.Click += (s, e) => stop = true;
            btnOpen.Click += (s, e) => { if (Directory.Exists(Destination)) System.Diagnostics.Process.Start("explorer.exe", "\"" + Destination + "\""); };
            row.Controls.AddRange(new Control[] { btnGo, btnStop, btnOpen, close });
            lblProgress = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3) };
            row.Controls.Add(lblProgress);
            t.Controls.Add(row, 1, 6);
            t.SetColumnSpan(row, 2);
            txtLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            t.Controls.Add(txtLog, 0, 7);
            t.SetColumnSpan(txtLog, 3);
            t.RowCount = 8;
            for (int i = 0; i < 7; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(t);
            CancelButton = close;
            FormClosing += (s, e) => { if (worker != null && worker.IsAlive) { stop = true; e.Cancel = true; } };
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        void Log(string msg) { Ui(() => txtLog.AppendText(msg + Environment.NewLine)); }

        /// File name for the n-th downloaded attachment of a test case.
        public static string FileName(string naming, TestInstance inst, string original, int n)
        {
            var ext = Path.GetExtension(original);
            var label = Util.SafeFolderName(inst.Label);
            if (naming == NameOriginal) return Util.SafeFolderName(original);
            if (naming == NameBoth) return label + " - " + Util.SafeFolderName(original);
            return label + "_" + n + ext;
        }

        protected void StartDownload()
        {
            var dest = Destination;
            if (dest == "") return;
            string naming = (string)cboNaming.SelectedItem;
            bool imagesOnly = chkImagesOnly.Checked, skipExisting = chkSkipExisting.Checked;
            stop = false;
            btnGo.Enabled = btnOpen.Enabled = false;
            btnStop.Enabled = true;
            int files = 0, skipped = 0, failed = 0, cases = 0;
            worker = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(dest);
                    for (int k = 0; k < targets.Count && !stop; k++)
                    {
                        var inst = targets[k];
                        var ts = lab.SetOf(inst);
                        var where = ts.Name + " > " + inst.Label;
                        int shownCase = k + 1, shownFiles = files;
                        Ui(() => lblProgress.Text = string.Format("test case {0} / {1}   -   {2} file(s)", shownCase, targets.Count, shownFiles));
                        try
                        {
                            var atts = client.GetAttachments("test-instances", inst.Id)
                                             .Where(a => !imagesOnly || Util.ImageExts.Contains(Path.GetExtension(a.Name).ToLowerInvariant()))
                                             .ToList();
                            if (atts.Count == 0) continue;
                            var dir = lab.LocalSetPath(ts).Aggregate(dest, (acc, part) => Path.Combine(acc, Util.SafeFolderName(part)));
                            dir = Path.Combine(dir, Util.SafeFolderName(inst.Label));
                            Directory.CreateDirectory(dir);
                            int got = 0, n = 0;
                            foreach (var att in atts)
                            {
                                if (stop) break;
                                var path = Path.Combine(dir, FileName(naming, inst, att.Name, ++n));
                                if (skipExisting && File.Exists(path)) { skipped++; continue; }
                                try
                                {
                                    File.WriteAllBytes(path, client.DownloadAttachment("test-instances", inst.Id, att));
                                    got++;
                                    files++;
                                }
                                catch (Exception e)
                                {
                                    if (!(e is AlmException || e is WebException || e is IOException)) throw;
                                    failed++;
                                    Log("[FAIL] " + where + " / " + att.Name + ": " + e.Message);
                                }
                            }
                            cases++;
                            Log(string.Format("[ OK ] {0}: {1} file(s)", where, got));
                        }
                        catch (Exception e)
                        {
                            if (!(e is AlmException || e is WebException || e is IOException)) throw;
                            failed++;
                            Log("[FAIL] " + where + ": " + e.Message);
                        }
                    }
                    Log(string.Format("{0}: {1} file(s) from {2} test case(s) saved in {3}.  {4} already there, {5} failed.",
                                      stop ? "Stopped" : "Done", files, cases, dest, skipped, failed));
                }
                catch (Exception e) { Log("ERROR: " + e.Message); }
                finally
                {
                    Ui(() =>
                    {
                        btnGo.Enabled = btnOpen.Enabled = true;
                        btnStop.Enabled = false;
                        lblProgress.Text = string.Format("{0} file(s) saved", files);
                    });
                }
            }) { IsBackground = true };
            worker.Start();
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
