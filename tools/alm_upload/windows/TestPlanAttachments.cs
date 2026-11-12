// ALM Image Uploader - bulk delete of the attachments of the tests in a Test Plan folder.
// Only attachments are deleted: the tests, their design steps and the Test Lab test cases stay.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace AlmImageUploader
{
    /// Tree of the Test Plan folders, loaded as folders are expanded.
    public class TestPlanFolderPicker : Form
    {
        const string Placeholder = "...";
        readonly AlmClient client;
        readonly TreeView tree;
        readonly Button ok;
        readonly Label status;
        public string SelectedId, SelectedPath;

        public TestPlanFolderPicker(AlmClient client)
        {
            this.client = client;
            Text = "Choose Test Plan folder";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(500, 540);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Margin = new Padding(3, 3, 3, 6), MaximumSize = new Size(470, 0),
                                            Text = "Choose a Test Plan folder. Its tests (and those of its sub folders, if chosen) are listed." });
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
                var id = e.Node.Tag as string;
                ok.Enabled = id != null;
                if (id == null) return;
                SelectedId = id;
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
                foreach (var f in client.GetTestPlanRoots())
                {
                    var n = tree.Nodes.Add(f["name"]);
                    n.Tag = f["id"];
                    n.Nodes.Add(Placeholder);
                }
                if (tree.Nodes.Count == 1) tree.Nodes[0].Expand();
                SetStatus(false, tree.Nodes.Count == 0 ? "No Test Plan folders found." : "");
            }
            catch (Exception e)
            {
                SetStatus(false, "Could not read Test Plan: " + e.Message);
            }
        }

        void LoadChildren(TreeNode node)
        {
            if (node.Nodes.Count != 1 || node.Nodes[0].Text != Placeholder || node.Nodes[0].Tag != null) return;
            try
            {
                SetStatus(true, "Loading " + node.Text + "...");
                var folders = client.GetTestFoldersIn((string)node.Tag);
                tree.BeginUpdate();
                node.Nodes.Clear();
                foreach (var f in folders)
                {
                    var child = node.Nodes.Add(f["name"]);
                    child.Tag = f["id"];
                    child.Nodes.Add(Placeholder);
                }
                if (folders.Count == 0) node.Nodes.Add("(no sub folders)").ForeColor = SystemColors.GrayText;
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

    /// One attachment of a test (or of one of its design steps) in Test Plan.
    public class PlanAttachment
    {
        public string Folder, TestName, TestId, Entity, OwnerId, OwnerText;
        public AlmClient.Attachment Att;
        public bool Tick, Deleted;
        public bool IsImage { get { return Util.ImageExts.Contains(Path.GetExtension(Att.Name ?? "").ToLowerInvariant()); } }
    }

    public class TestPlanAttachmentsDialog : WorkDialog
    {
        readonly AlmClient client;
        protected readonly TextBox txtFolder, txtSearch;
        protected readonly CheckBox chkSub, chkSteps;
        protected readonly ComboBox cboType;
        protected readonly ListView lv;
        protected readonly Button btnFind, btnDelete, btnStop, btnTickAll, btnUntickAll;
        readonly Label lblSummary;
        bool filling;
        protected string folderId, folderPath;
        protected List<PlanAttachment> items = new List<PlanAttachment>();

        public const string TypeAll = "All files", TypeImages = "Images", TypeOther = "Other files";

        public TestPlanAttachmentsDialog(AlmClient client)
        {
            this.client = client;
            Text = "Test Plan attachments";
            ClientSize = new Size(1000, 680);
            MaximizeBox = true;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var head = new Label
            {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 4),
                Text = "Delete images and other attachments of the tests in a Test Plan folder",
            };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 3);
            var note = new Label
            {
                AutoSize = true, ForeColor = Color.DarkGreen, Margin = new Padding(3, 0, 3, 8), MaximumSize = new Size(940, 0),
                Text = "Only the attachments are deleted. The tests, their design steps and the test cases in Test Lab are never deleted or changed.",
            };
            t.Controls.Add(note, 0, 1);
            t.SetColumnSpan(note, 3);

            t.Controls.Add(new Label { Text = "Test Plan folder", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 2);
            txtFolder = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = SystemColors.Window };
            t.Controls.Add(txtFolder, 1, 2);
            var browse = Btn("Browse...");
            browse.Click += (s, e) => Browse();
            t.Controls.Add(browse, 2, 2);

            chkSub = new CheckBox { Text = "include sub folders", AutoSize = true, Checked = true };
            chkSteps = new CheckBox { Text = "also the attachments of the design steps", AutoSize = true };
            btnFind = Btn("Find attachments", true);
            btnFind.Click += (s, e) => Find();
            var opts = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            opts.Controls.AddRange(new Control[] { chkSub, chkSteps, btnFind });
            t.Controls.Add(opts, 1, 3);
            t.SetColumnSpan(opts, 2);

            cboType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            cboType.Items.AddRange(new object[] { TypeAll, TypeImages, TypeOther });
            cboType.SelectedIndex = 0;
            cboType.SelectedIndexChanged += (s, e) => Fill();
            txtSearch = new TextBox { Width = 160 };
            txtSearch.TextChanged += (s, e) => Fill();
            btnTickAll = Btn("Tick all shown");
            btnUntickAll = Btn("Untick all");
            btnTickAll.Click += (s, e) => { foreach (var a in ShownItems()) a.Tick = true; Fill(); };
            btnUntickAll.Click += (s, e) => { foreach (var a in items) a.Tick = false; Fill(); };
            var filter = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            filter.Controls.AddRange(new Control[] { new Label { Text = "Show:", AutoSize = true, Margin = new Padding(3, 7, 0, 3) }, cboType,
                                                     new Label { Text = "Search:", AutoSize = true, Margin = new Padding(12, 7, 0, 3) }, txtSearch,
                                                     btnTickAll, btnUntickAll });
            t.Controls.Add(filter, 1, 4);
            t.SetColumnSpan(filter, 2);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, CheckBoxes = true };
            lv.Columns.Add("Folder", 230);
            lv.Columns.Add("Test", 260);
            lv.Columns.Add("On", 70);
            lv.Columns.Add("File", 270);
            lv.Columns.Add("Size", 80);
            lv.ItemChecked += (s, e) =>
            {
                if (filling) return;
                var a = e.Item.Tag as PlanAttachment;
                if (a == null || a.Deleted) return;
                a.Tick = e.Item.Checked;
                Summary();
            };
            t.Controls.Add(lv, 0, 5);
            t.SetColumnSpan(lv, 3);
            lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3), Text = "Choose a Test Plan folder, then press Find attachments." };
            t.Controls.Add(lblSummary, 0, 6);
            t.SetColumnSpan(lblSummary, 3);

            var row = new FlowLayoutPanel { AutoSize = true };
            btnDelete = Btn("Delete ticked attachments", true);
            btnDelete.ForeColor = Color.Firebrick;
            btnStop = Btn("Stop");
            btnStop.Enabled = false;
            var close = Btn("Close");
            close.DialogResult = DialogResult.Cancel;
            btnDelete.Click += (s, e) => DoDelete();
            btnStop.Click += (s, e) => stop = true;
            row.Controls.AddRange(new Control[] { btnDelete, btnStop, close });
            t.Controls.Add(row, 0, 7);
            t.SetColumnSpan(row, 3);
            txtLog = LogBox();
            t.Controls.Add(txtLog, 0, 8);
            t.SetColumnSpan(txtLog, 3);
            t.RowCount = 9;
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            AddScrolling(t, new Size(860, 520));
            CancelButton = close;
            UpdateButtons();
        }

        void Browse()
        {
            if (Busy) return;
            using (var d = new TestPlanFolderPicker(client))
                if (d.ShowDialog(this) == DialogResult.OK) SetFolder(d.SelectedId, d.SelectedPath);
        }

        public void SetFolder(string id, string path)
        {
            folderId = id;
            folderPath = path;
            txtFolder.Text = path;
            items = new List<PlanAttachment>();
            Fill();
        }

        // ------------------------------------------------------------ find
        protected void Find()
        {
            if (Busy || folderId == null) return;
            bool sub = chkSub.Checked, steps = chkSteps.Checked;
            string rootId = folderId, rootPath = folderPath;
            var found = new List<PlanAttachment>();
            stop = false;
            btnStop.Enabled = true;
            lblSummary.Text = "Reading the Test Plan folders and tests...";
            UpdateButtons(true);
            Run(() =>
            {
                // the folder and (when chosen) all folders below it, with their paths
                var paths = new Dictionary<string, string> { { rootId, rootPath } };
                var frontier = new List<string> { rootId };
                while (sub && frontier.Count > 0 && !stop)
                {
                    var next = new List<string>();
                    foreach (var f in client.GetByIds("test-folders", "parent-id", frontier, new[] { "id", "name", "parent-id" }))
                    {
                        string id = f["id"], parent;
                        if (paths.ContainsKey(id) || !f.TryGetValue("parent-id", out parent) || !paths.ContainsKey(parent)) continue;
                        paths[id] = paths[parent] + "\\" + (f.ContainsKey("name") ? f["name"] : id);
                        next.Add(id);
                    }
                    frontier = next;
                }
                var tests = client.GetByIds("tests", "parent-id", paths.Keys, new[] { "id", "name", "parent-id" })
                                  .Where(x => paths.ContainsKey(x["parent-id"]))
                                  .OrderBy(x => paths[x["parent-id"]], StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(x => x.ContainsKey("name") ? x["name"] : "", Comparer<string>.Create(Util.NaturalCompare)).ToList();
                Log(string.Format("{0} folder(s), {1} test(s).", paths.Count, tests.Count));
                var stepsOf = new Dictionary<string, List<Dictionary<string, string>>>();
                if (steps && tests.Count > 0)
                    foreach (var g in client.GetByIds("design-steps", "parent-id", tests.Select(x => x["id"]), new[] { "id", "parent-id", "name", "step-order" })
                                            .GroupBy(x => x["parent-id"]))
                        stepsOf[g.Key] = g.OrderBy(x => Util.ToInt(x.ContainsKey("step-order") ? x["step-order"] : "")).ToList();
                int k = 0;
                foreach (var test in tests)
                {
                    if (stop) { Log("Stopped - the list shows what was read so far."); break; }
                    k++;
                    string name = test.ContainsKey("name") ? test["name"] : test["id"], folder = paths[test["parent-id"]];
                    int shown = k;
                    Ui(() => lblSummary.Text = string.Format("Reading attachments: test {0} / {1}   -   {2} file(s)", shown, tests.Count, found.Count));
                    try
                    {
                        foreach (var a in client.GetAttachments("tests", test["id"]))
                            found.Add(new PlanAttachment { Folder = folder, TestName = name, TestId = test["id"], Entity = "tests", OwnerId = test["id"], OwnerText = "Test", Att = a });
                        List<Dictionary<string, string>> st;
                        if (stepsOf.TryGetValue(test["id"], out st))
                            foreach (var step in st)
                                foreach (var a in client.GetAttachments("design-steps", step["id"]))
                                    found.Add(new PlanAttachment { Folder = folder, TestName = name, TestId = test["id"], Entity = "design-steps", OwnerId = step["id"],
                                                                   OwnerText = step.ContainsKey("name") && step["name"] != "" ? step["name"] : "Step " + step["id"], Att = a });
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException)) throw;
                        Log("Could not read the attachments of " + folder + " > " + name + ": " + e.Message);
                    }
                }
                Log(string.Format("Found {0} attachment(s) ({1} image(s)) on {2} test(s).",
                                  found.Count, found.Count(a => a.IsImage), found.Select(a => a.TestId).Distinct().Count()));
            }, () =>
            {
                items = found;
                btnStop.Enabled = false;
                Fill();
            });
        }

        // ------------------------------------------------------------ list
        IEnumerable<PlanAttachment> ShownItems()
        {
            var type = (string)cboType.SelectedItem;
            var q = txtSearch.Text.Trim();
            return items.Where(a => !a.Deleted
                                    && (type == TypeAll || (type == TypeImages) == a.IsImage)
                                    && (q == "" || (a.Folder + " " + a.TestName + " " + a.Att.Name).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        static string SizeText(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#") + " MB";
        }

        protected void Fill()
        {
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var a in ShownItems())
                lv.Items.Add(new ListViewItem(new[] { a.Folder, a.TestName, a.OwnerText, a.Att.Name, SizeText(a.Att.Size) })
                             { Tag = a, Checked = a.Tick, ForeColor = a.Tick ? Color.Firebrick : SystemColors.WindowText });
            lv.EndUpdate();
            filling = false;
            Summary();
        }

        void Summary()
        {
            if (folderId == null) { UpdateButtons(); return; }
            var live = items.Where(a => !a.Deleted).ToList();
            int ticked = live.Count(a => a.Tick);
            lblSummary.Text = live.Count == 0 && items.Count == 0 ? "Press Find attachments."
                : string.Format("{0} attachment(s) on {1} test(s), {2} shown;   {3} ticked to delete ({4}).",
                                live.Count, live.Select(a => a.TestId).Distinct().Count(), lv.Items.Count, ticked,
                                SizeText(live.Where(a => a.Tick).Sum(a => Math.Max(0, a.Att.Size))));
            UpdateButtons();
        }

        void UpdateButtons(bool busy = false)
        {
            busy = busy || Busy;
            btnFind.Enabled = !busy && folderId != null;
            btnDelete.Enabled = !busy && items.Any(a => a.Tick && !a.Deleted);
            btnTickAll.Enabled = btnUntickAll.Enabled = !busy && items.Count > 0;
        }

        // ------------------------------------------------------------ delete
        protected void DoDelete()
        {
            if (Busy) return;
            var todo = items.Where(a => a.Tick && !a.Deleted).ToList();
            if (todo.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendFormat("Delete {0} attachment(s) from {1} test(s) in Test Plan?", todo.Count, todo.Select(a => a.TestId).Distinct().Count()).AppendLine().AppendLine();
            foreach (var a in todo.Take(15))
                sb.AppendLine("   " + a.Folder + " > " + a.TestName + (a.Entity == "tests" ? "" : " (" + a.OwnerText + ")") + ":  " + a.Att.Name);
            if (todo.Count > 15) sb.AppendLine("   ... and " + (todo.Count - 15) + " more (ticked in the list).");
            sb.AppendLine().AppendLine("Only these attachments are deleted. The tests, their design steps and the test cases in Test Lab stay as they are.");
            sb.Append("Deleted attachments cannot be brought back.");
            if (!Confirm(sb.ToString())) return;
            stop = false;
            btnStop.Enabled = true;
            UpdateButtons(true);
            int ok = 0, failed = 0;
            Run(() =>
            {
                foreach (var a in todo)
                {
                    if (stop) { Log("Stopped."); break; }
                    try
                    {
                        client.DeleteAttachment(a.Entity, a.OwnerId, a.Att);
                        a.Deleted = true;
                        ok++;
                        Log("[DEL ] " + a.Folder + " > " + a.TestName + (a.Entity == "tests" ? "" : " (" + a.OwnerText + ")") + ":  " + a.Att.Name);
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException)) throw;
                        failed++;
                        Log("[FAIL] " + a.Folder + " > " + a.TestName + ":  " + a.Att.Name + " - " + e.Message);
                    }
                    int done = ok + failed;
                    Ui(() => lblSummary.Text = string.Format("Deleting...  {0} / {1}", done, todo.Count));
                }
                // read back what is left on those tests / steps
                int left = 0;
                foreach (var owner in todo.Where(a => a.Deleted).Select(a => a.Entity + "|" + a.OwnerId).Distinct())
                {
                    var parts = owner.Split('|');
                    try
                    {
                        var names = client.ListAttachmentNames(parts[0], parts[1]);
                        left += todo.Count(a => a.Deleted && a.Entity == parts[0] && a.OwnerId == parts[1] && names.Contains(a.Att.Name));
                    }
                    catch (Exception e) { if (!(e is AlmException || e is WebException)) throw; }
                }
                Log(string.Format("Done: {0} attachment(s) deleted, {1} failed.{2}", ok, failed,
                                  left > 0 ? "  Warning: " + left + " of them are still listed in ALM." : "  Checked in ALM: they are gone."));
            }, () =>
            {
                btnStop.Enabled = false;
                Fill();
            });
        }
    }
}
