// ALM Image Uploader - give test cases with the same title (the name without its number,
// e.g. "Login_Excp" of "1.1_Login_Excp", "2.1_Login_Excp", ...) the files of one of them.
// Only assignments change here; nothing is sent to ALM until Upload.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AlmImageUploader
{
    public class FillPlanRow
    {
        public TestInstance Target, Source;
        public List<ImageItem> Files;
        public bool Tick = true;
    }

    public class FillSameTitleDialog : Form
    {
        readonly TestLabFolder lab;
        readonly List<ImageItem> images;
        readonly HashSet<TestInstance> ticked;
        protected readonly CheckBox chkOnlyEmpty, chkAnySet, chkOnlyTicked;
        protected readonly ListView lv;
        protected readonly Button btnApply;
        readonly Label lblInfo;
        List<FillPlanRow> plan = new List<FillPlanRow>();
        bool filling;

        /// The rows the user kept ticked, after Assign was pressed.
        public List<FillPlanRow> Chosen = new List<FillPlanRow>();

        /// "1.1_Login_Excp" -> "login excp"; "[2]3.1 Login Excp" -> "login excp".
        public static string Title(string testName)
        {
            var t = Regex.Replace(testName ?? "", @"^\s*(\[\d+\])?\s*\d+(\.\d+)*[\s_\-.)]*", "");
            return Util.Norm(t == "" ? testName : t);
        }

        public FillSameTitleDialog(TestLabFolder lab, List<ImageItem> images, HashSet<TestInstance> ticked)
        {
            this.lab = lab;
            this.images = images;
            this.ticked = ticked;
            Text = "Fill test cases with the same title";
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(940, 600);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
            t.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(900, 0), Margin = new Padding(3, 3, 3, 8),
                Text = "Test cases whose names differ only in the number in front (1.1_Login_Excp, 2.1_Login_Excp, 3.1_Login_Excp ...) "
                       + "get the same files as the first of them that has files. Only the assignments change; Upload sends them to ALM.",
            });
            chkOnlyEmpty = new CheckBox { Text = "only test cases that have no files yet", AutoSize = true, Checked = true };
            chkAnySet = new CheckBox { Text = "also across test sets (not only within the same test set)", AutoSize = true };
            chkOnlyTicked = new CheckBox { Text = "only the ticked test cases (" + ticked.Count + ")", AutoSize = true, Enabled = ticked.Count > 0, Checked = ticked.Count > 0 };
            foreach (var c in new[] { chkOnlyEmpty, chkAnySet, chkOnlyTicked }) c.CheckedChanged += (s, e) => Build();
            var opts = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            opts.Controls.AddRange(new Control[] { chkOnlyEmpty, chkAnySet, chkOnlyTicked });
            t.Controls.Add(opts);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, CheckBoxes = true, HideSelection = false };
            lv.Columns.Add("Test set", 170);
            lv.Columns.Add("Test case (gets the files)", 230);
            lv.Columns.Add("Files from", 230);
            lv.Columns.Add("Files", 270);
            lv.ItemChecked += (s, e) =>
            {
                if (filling) return;
                var r = e.Item.Tag as FillPlanRow;
                if (r != null) r.Tick = e.Item.Checked;
                Info();
            };
            t.Controls.Add(lv);
            lblInfo = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            t.Controls.Add(lblInfo);
            var row = new FlowLayoutPanel { AutoSize = true };
            btnApply = new Button { Text = "Assign to the ticked test cases", AutoSize = true, Padding = new Padding(6, 2, 6, 2), Font = new Font(Font, FontStyle.Bold) };
            var tickAll = new Button { Text = "Tick all", AutoSize = true };
            var untick = new Button { Text = "Untick all", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            tickAll.Click += (s, e) => { foreach (var r in plan) r.Tick = true; Fill(); };
            untick.Click += (s, e) => { foreach (var r in plan) r.Tick = false; Fill(); };
            btnApply.Click += (s, e) =>
            {
                Chosen = plan.Where(r => r.Tick).ToList();
                if (Chosen.Count == 0) return;
                DialogResult = DialogResult.OK;
            };
            row.Controls.AddRange(new Control[] { btnApply, tickAll, untick, cancel });
            t.Controls.Add(row);
            t.RowCount = 5;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(t);
            CancelButton = cancel;
            Load += (s, e) => ScreenFit.Fit(this);
            Build();
        }

        List<ImageItem> FilesOf(TestInstance i) { return images.Where(img => img.Assigned.Contains(i)).ToList(); }

        /// Works out which test case gets the files of which one.
        public void Build()
        {
            bool onlyEmpty = chkOnlyEmpty.Checked, anySet = chkAnySet.Checked, onlyTicked = chkOnlyTicked.Checked && ticked.Count > 0;
            var files = new Dictionary<TestInstance, List<ImageItem>>();
            foreach (var img in images)
                foreach (var i in img.Assigned)
                {
                    List<ImageItem> l;
                    if (!files.TryGetValue(i, out l)) files[i] = l = new List<ImageItem>();
                    l.Add(img);
                }
            plan = new List<FillPlanRow>();
            var all = lab.Sets.SelectMany(s => s.Instances.Select(i => new { Set = s, Inst = i })).ToList();
            foreach (var g in all.GroupBy(x => (anySet ? "" : x.Set.Id + "|") + Title(x.Inst.TestName)))
            {
                // the first (by test set, then name) that has files is the one copied
                var ordered = g.OrderBy(x => x.Set.PathText, StringComparer.OrdinalIgnoreCase)
                               .ThenBy(x => x.Inst.Label, Comparer<string>.Create(Util.NaturalCompare)).ToList();
                var src = ordered.FirstOrDefault(x => files.ContainsKey(x.Inst));
                if (src == null) continue;
                var srcFiles = files[src.Inst];
                foreach (var x in ordered)
                {
                    if (x.Inst == src.Inst) continue;
                    if (onlyTicked && !ticked.Contains(x.Inst)) continue;
                    List<ImageItem> have;
                    files.TryGetValue(x.Inst, out have);
                    if (onlyEmpty && have != null && have.Count > 0) continue;
                    var add = srcFiles.Where(f => have == null || !have.Contains(f)).ToList();
                    if (add.Count == 0) continue;
                    plan.Add(new FillPlanRow { Target = x.Inst, Source = src.Inst, Files = add });
                }
            }
            Fill();
        }

        void Fill()
        {
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var r in plan)
                lv.Items.Add(new ListViewItem(new[]
                {
                    lab.SetOf(r.Target).PathText, r.Target.Label,
                    (lab.SetOf(r.Source) == lab.SetOf(r.Target) ? "" : lab.SetOf(r.Source).Name + " > ") + r.Source.Label,
                    r.Files.Count + ":  " + string.Join(", ", r.Files.Select(f => f.Rel)),
                }) { Tag = r, Checked = r.Tick });
            lv.EndUpdate();
            filling = false;
            Info();
        }

        void Info()
        {
            var on = plan.Where(r => r.Tick).ToList();
            lblInfo.Text = plan.Count == 0
                ? "Nothing to fill: no test case without files has a same-title test case with files" + (chkOnlyEmpty.Checked ? "." : " (or they have them all already).")
                : string.Format("{0} test case(s) ticked to get files ({1} assignments to upload).", on.Count, on.Sum(r => r.Files.Count));
            btnApply.Enabled = on.Count > 0;
        }
    }
}
