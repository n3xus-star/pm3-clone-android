// ALM Image Uploader - "Export results" and "Import results" dialogs.

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
    /// Base for the two dialogs: a worker thread, a log and UI-thread helpers.
    public class WorkDialog : Form
    {
        protected Thread worker;
        protected volatile bool stop;
        protected TextBox txtLog;

        protected WorkDialog()
        {
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            FormClosing += (s, e) => { if (Busy) { stop = true; e.Cancel = true; } };
        }

        protected bool Busy { get { return worker != null && worker.IsAlive; } }

        protected void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }

        protected void Log(string msg) { Ui(() => txtLog.AppendText(msg + Environment.NewLine)); }

        protected void Run(Action work, Action after)
        {
            stop = false;
            worker = new Thread(() =>
            {
                try { work(); }
                catch (Exception e)
                {
                    Log("ERROR: " + (e is WebException ? "cannot connect (" + e.Message + ")" : e.Message));
                }
                finally { Ui(after); }
            }) { IsBackground = true };
            worker.Start();
        }

        protected virtual bool Confirm(string q)
        {
            return MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        protected static TextBox LogBox()
        {
            return new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        }

        protected static Button Btn(string text, bool bold = false)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            if (bold) b.Font = new Font(b.Font, FontStyle.Bold);
            return b;
        }
    }

    // ===================================================================== export
    public class ExportDialog : WorkDialog
    {
        readonly AlmClient client;
        readonly TestLabFolder lab;
        readonly List<TestInstance> all, ticked;
        readonly string labText;
        protected readonly RadioButton rbAll, rbTicked, rbExcel, rbPdf;
        protected readonly TextBox txtFile;
        readonly Button btnExport, btnOpen;
        readonly Label lblStatus;

        public string LastFile { get; private set; }

        public ExportDialog(AlmClient client, TestLabFolder lab, List<TestInstance> ticked, string labText, string folder)
        {
            this.client = client;
            this.lab = lab;
            this.ticked = ticked;
            this.labText = labText;
            all = lab.Sets.SelectMany(s => s.Instances).ToList();
            Text = "Export test results";
            ClientSize = new Size(700, 440);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var head = new Label { Text = "Export the results of the test cases to Excel or PDF", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 3);

            t.Controls.Add(new Label { Text = "Test cases", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 0, 1);
            rbAll = new RadioButton { AutoSize = true, Checked = true, Text = string.Format("All {0} test cases in {1} ({2} test sets)", all.Count, labText, lab.Sets.Count) };
            rbTicked = new RadioButton { AutoSize = true, Text = string.Format("Only the {0} ticked test case(s)", ticked.Count), Enabled = ticked.Count > 0 };
            var scope = Stack(rbAll, rbTicked);
            t.Controls.Add(scope, 1, 1);
            t.SetColumnSpan(scope, 2);

            t.Controls.Add(new Label { Text = "Format", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 0, 2);
            rbExcel = new RadioButton { AutoSize = true, Checked = true, Text = "Excel (.xlsx)  -  edit it and import it again, also into another department's folder" };
            rbPdf = new RadioButton { AutoSize = true, Text = "PDF report  -  summary and one table per test set" };
            var format = Stack(rbExcel, rbPdf);
            t.Controls.Add(format, 1, 2);
            t.SetColumnSpan(format, 2);

            t.Controls.Add(new Label { Text = "Save as", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 3);
            var name = "ALM results - " + Util.SafeFolderName(lab.Sets.Count > 0 ? labText.Split('\\').Last() : "Test Lab") + " - " + DateTime.Now.ToString("yyyy-MM-dd");
            txtFile = new TextBox { Dock = DockStyle.Fill, Text = Path.Combine(folder, name + ".xlsx") };
            t.Controls.Add(txtFile, 1, 3);
            var browse = Btn("Browse...");
            browse.Click += (s, e) => Browse();
            t.Controls.Add(browse, 2, 3);
            rbExcel.CheckedChanged += (s, e) => txtFile.Text = Path.ChangeExtension(txtFile.Text, rbExcel.Checked ? ".xlsx" : ".pdf");

            var row = new FlowLayoutPanel { AutoSize = true };
            btnExport = Btn("Export", true);
            btnOpen = Btn("Open file");
            btnOpen.Enabled = false;
            var close = Btn("Close");
            close.DialogResult = DialogResult.Cancel;
            btnExport.Click += (s, e) => DoExport();
            btnOpen.Click += (s, e) => { if (LastFile != null && File.Exists(LastFile)) System.Diagnostics.Process.Start(LastFile); };
            row.Controls.AddRange(new Control[] { btnExport, btnOpen, close });
            lblStatus = new Label { AutoSize = true, Margin = new Padding(8, 9, 3, 3) };
            row.Controls.Add(lblStatus);
            t.Controls.Add(row, 1, 4);
            t.SetColumnSpan(row, 2);
            txtLog = LogBox();
            t.Controls.Add(txtLog, 0, 5);
            t.SetColumnSpan(txtLog, 3);
            t.RowCount = 6;
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(t);
            CancelButton = close;
        }

        /// Controls one under the other (radio buttons of one group).
        static TableLayoutPanel Stack(params Control[] items)
        {
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = items.Length, Margin = new Padding(0, 0, 0, 6) };
            foreach (var c in items)
            {
                c.Margin = new Padding(3, 3, 3, 1);
                t.Controls.Add(c);
            }
            return t;
        }

        void Browse()
        {
            using (var d = new SaveFileDialog())
            {
                d.Filter = rbExcel.Checked ? "Excel workbook (*.xlsx)|*.xlsx" : "PDF (*.pdf)|*.pdf";
                d.FileName = Path.GetFileName(txtFile.Text);
                try { d.InitialDirectory = Path.GetDirectoryName(txtFile.Text); } catch (ArgumentException) { }
                if (d.ShowDialog(this) == DialogResult.OK) txtFile.Text = d.FileName;
            }
        }

        protected void DoExport()
        {
            if (Busy) return;
            var file = txtFile.Text.Trim();
            if (file == "") return;
            bool excel = rbExcel.Checked;
            var targets = rbTicked.Checked ? ticked : all;
            btnExport.Enabled = btnOpen.Enabled = false;
            lblStatus.Text = "Reading results from ALM...";
            Run(() =>
            {
                var f = ResultFields.Read(client);
                var rows = Results.Fetch(client, lab, targets, f);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
                var title = "Test results - " + labText;
                if (excel)
                    Results.WriteExcel(file, rows, f, title);
                else
                    Results.WritePdf(file, rows, f, title, string.Format("Domain {0}  /  Project {1}     -     {2} test case(s) in {3} test set(s)     -     exported {4} by {5}",
                        client.Domain, client.Project, rows.Count, rows.Select(r => r.Set).Distinct().Count(), DateTime.Now.ToString("yyyy-MM-dd HH:mm"), client.User));
                var counts = rows.GroupBy(r => r.Get(f.Status) == "" ? "No Run" : r.Get(f.Status)).Select(g => g.Key + " " + g.Count());
                Log(string.Format("Saved {0} test case(s) to {1}", rows.Count, file));
                Log("   " + string.Join(",  ", counts));
                LastFile = file;
            }, () =>
            {
                btnExport.Enabled = true;
                btnOpen.Enabled = LastFile != null;
                lblStatus.Text = LastFile != null ? "Done." : "";
            });
        }
    }

    // ===================================================================== import
    public class ImportDialog : WorkDialog
    {
        readonly AlmClient client;
        readonly TestLabFolder lab;
        protected readonly TextBox txtFile;
        protected readonly CheckBox chkStatus, chkComment, chkAttach, chkImagesOnly;
        protected readonly ComboBox cboShow;
        protected readonly ListView lv;
        readonly Button btnApply, btnStop;
        readonly Label lblSummary;
        ResultFields fields;
        List<ImportRow> rows = new List<ImportRow>();

        /// Everything written to ALM, for the main window's tree.
        public readonly List<FieldChange> Changes = new List<FieldChange>();
        public List<ImportRow> Rows { get { return rows; } }

        public ImportDialog(AlmClient client, TestLabFolder lab, string labText, string folder)
        {
            this.client = client;
            this.lab = lab;
            Text = "Import test results from Excel";
            ClientSize = new Size(1180, 660);
            MaximizeBox = true;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var head = new Label
            {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8),
                Text = "Update the test cases in  " + labText + "  from an Excel file made by 'Export results'",
            };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 3);
            t.Controls.Add(new Label { Text = "Excel file", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
            txtFile = new TextBox { Dock = DockStyle.Fill };
            t.Controls.Add(txtFile, 1, 1);
            var browse = Btn("Browse...");
            browse.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", InitialDirectory = folder })
                    if (d.ShowDialog(this) == DialogResult.OK) LoadFile(d.FileName);
            };
            t.Controls.Add(browse, 2, 1);

            chkStatus = new CheckBox { Text = "Update Status", AutoSize = true, Checked = true };
            chkComment = new CheckBox { Text = "Update Comments", AutoSize = true, Checked = true };
            cboShow = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            cboShow.Items.AddRange(new object[] { "All rows", "Changes only", "Problems only" });
            cboShow.SelectedIndex = 0;
            cboShow.SelectedIndexChanged += (s, e) => Fill();
            chkAttach = new CheckBox { Text = "Copy attachments from the exported test cases", AutoSize = true, Margin = new Padding(16, 3, 3, 3) };
            chkImagesOnly = new CheckBox { Text = "images only", AutoSize = true, Enabled = false };
            chkStatus.CheckedChanged += (s, e) => { Fill(); };
            chkComment.CheckedChanged += (s, e) => { Fill(); };
            chkAttach.CheckedChanged += (s, e) => { chkImagesOnly.Enabled = chkAttach.Checked; Fill(); };
            var opts = new FlowLayoutPanel { AutoSize = true };
            opts.Controls.AddRange(new Control[] { chkStatus, chkComment, chkAttach, chkImagesOnly,
                                                   new Label { Text = "Show:", AutoSize = true, Margin = new Padding(16, 6, 0, 3) }, cboShow });
            t.Controls.Add(opts, 1, 2);
            t.SetColumnSpan(opts, 2);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            lv.Columns.Add("Row", 45);
            lv.Columns.Add("In the Excel file", 250);
            lv.Columns.Add("ALM test case", 250);
            lv.Columns.Add("Matched by", 105);
            lv.Columns.Add("Status", 140);
            lv.Columns.Add("Comments", 140);
            lv.Columns.Add("Attachments", 130);
            lv.Columns.Add("Result", 230);
            t.Controls.Add(lv, 0, 3);
            t.SetColumnSpan(lv, 3);

            lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3), Text = "Choose the Excel file." };
            t.Controls.Add(lblSummary, 0, 4);
            t.SetColumnSpan(lblSummary, 3);
            var row = new FlowLayoutPanel { AutoSize = true };
            btnApply = Btn("Apply changes to ALM", true);
            btnApply.Enabled = false;
            btnStop = Btn("Stop");
            btnStop.Enabled = false;
            var close = Btn("Close");
            close.DialogResult = DialogResult.Cancel;
            btnApply.Click += (s, e) => DoApply();
            btnStop.Click += (s, e) => stop = true;
            row.Controls.AddRange(new Control[] { btnApply, btnStop, close });
            t.Controls.Add(row, 0, 5);
            t.SetColumnSpan(row, 3);
            txtLog = LogBox();
            t.Controls.Add(txtLog, 0, 6);
            t.SetColumnSpan(txtLog, 3);
            t.RowCount = 7;
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 75));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            Controls.Add(t);
            CancelButton = close;
        }

        bool FieldsWanted(ImportRow r)
        {
            return r.Target != null && r.Error == null
                   && ((chkStatus.Checked && r.StatusChanges) || (chkComment.Checked && r.CommentChanges));
        }

        bool CopyWanted(ImportRow r)
        {
            return chkAttach.Checked && r.CanCopyAttachments;
        }

        bool Wanted(ImportRow r)
        {
            return FieldsWanted(r) || CopyWanted(r);
        }

        public void LoadFile(string path)
        {
            if (Busy) return;
            txtFile.Text = path;
            btnApply.Enabled = false;
            lv.Items.Clear();
            lblSummary.Text = "Reading the file and the current values in ALM...";
            ResultFields f = null;
            List<ImportRow> read = null;
            Run(() =>
            {
                f = ResultFields.Read(client);
                read = ResultsImport.Read(path, f);
                ResultsImport.Match(read, lab);
                ResultsImport.Compare(client, read, f);
                Log(string.Format("Read {0} row(s) from {1}", read.Count, Path.GetFileName(path)));
            }, () =>
            {
                if (read == null) { lblSummary.Text = "Could not read the file."; return; }
                fields = f;
                rows = read;
                chkComment.Text = "Update " + (f.Comment != null ? f.Comment.Label : "Comments");
                chkComment.Enabled = f.Comment != null && rows.Any(r => r.HasComment);
                chkStatus.Text = "Update " + f.Status.Label;
                Fill();
            });
        }

        static string Change(bool has, bool changes, string oldValue, string newValue)
        {
            if (!has) return "";
            if (!changes) return newValue == "" ? oldValue : newValue;
            return (oldValue == "" ? "(empty)" : oldValue) + "  ->  " + (newValue == "" ? "(empty)" : newValue);
        }

        protected void Fill()
        {
            if (fields == null) return;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var r in rows)
            {
                bool problem = (r.Error != null && !r.NotHere) || r.Result.StartsWith("FAILED") || r.AttachResult.StartsWith("FAILED");
                bool updated = r.Result.StartsWith("updated") || r.Result == "done";
                if (cboShow.SelectedIndex == 1 && !Wanted(r) && !updated) continue;
                if (cboShow.SelectedIndex == 2 && !problem) continue;
                var it = new ListViewItem(new[]
                {
                    r.Line.ToString(), r.SetName + "  >  " + r.CaseName,
                    r.Target == null ? "" : r.Set.PathText + "  >  " + r.Target.Label, r.Target == null ? "" : r.How,
                    Change(r.HasStatus && chkStatus.Checked, r.StatusChanges, r.OldStatus, r.NewStatus),
                    Change(r.HasComment && chkComment.Checked && fields.Comment != null, r.CommentChanges, r.OldComment, r.NewComment),
                    r.AttachResult != "" ? r.AttachResult : CopyWanted(r) ? "will copy" : "",
                    r.Error ?? (r.Result != "" ? r.Result : Wanted(r) ? "will change" : "no change"),
                }) { Tag = r };
                it.ForeColor = problem ? Color.Firebrick : r.NotHere ? Color.DarkGray : updated ? Color.ForestGreen
                             : Wanted(r) ? SystemColors.WindowText : SystemColors.GrayText;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            int change = rows.Count(Wanted), copies = rows.Count(CopyWanted), skipped = rows.Count(r => r.NotHere), bad = rows.Count(r => r.Error != null) - skipped,
                same = rows.Count - change - bad - skipped;
            lblSummary.Text = string.Format("{0} row(s):   {1} to change{5},   {2} already the same,   {3} skipped (test set not in this folder, grey),   {4} problem(s) (red).",
                                            rows.Count, change, same, skipped, bad,
                                            copies > 0 ? " (" + copies + " with attachments to copy)" : "");
            btnApply.Enabled = change > 0 && !Busy;
        }

        protected void DoApply()
        {
            if (Busy || fields == null) return;
            var todo = rows.Where(Wanted).ToList();
            if (todo.Count == 0) return;
            int fieldChanges = todo.Sum(r => FieldsWanted(r) ? (chkStatus.Checked && r.StatusChanges ? 1 : 0) + (chkComment.Checked && r.CommentChanges ? 1 : 0) : 0);
            int copyRows = todo.Count(CopyWanted);
            var question = copyRows == 0
                ? string.Format("Write {0} change(s) to ALM ({1} test case(s))?", fieldChanges, todo.Count)
                : string.Format("Write {0} change(s) to ALM and copy the attachments of {1} test case(s)?", fieldChanges, copyRows);
            if (!Confirm(question)) return;
            bool status = chkStatus.Checked, comment = chkComment.Checked, attach = chkAttach.Checked, imagesOnly = chkImagesOnly.Checked;
            btnApply.Enabled = false;
            btnStop.Enabled = true;
            int ok = 0, failed = 0;
            var f = fields;
            Run(() =>
            {
                foreach (var r in todo)
                {
                    if (stop) { Log("Stopped."); break; }
                    try
                    {
                        bool copy = attach && r.CanCopyAttachments;
                        r.Result = ResultsImport.Apply(client, r, f, status, comment);
                        // record the field changes first: they are in ALM even if copying fails below
                        lock (Changes)
                        {
                            if (comment && r.CommentChanges) Changes.Add(new FieldChange { Instance = r.Target, Field = f.Comment.Name, Value = r.NewComment });
                            if (status && r.StatusChanges) Changes.Add(new FieldChange { Instance = r.Target, Field = f.Status.Name, Value = r.NewStatus });
                        }
                        if (comment && r.CommentChanges) { r.OldComment = r.NewComment; r.CommentChanges = false; }
                        if (status && r.StatusChanges) { r.OldStatus = r.NewStatus; r.StatusChanges = false; }
                        if (copy)
                        {
                            try { r.AttachResult = ResultsImport.CopyAttachments(client, r, imagesOnly); }
                            catch (Exception e)
                            {
                                if (!(e is AlmException || e is WebException || e is IOException)) throw;
                                r.AttachResult = "FAILED: " + e.Message;
                                failed++;
                            }
                            if (r.Result == "no change") r.Result = "done";
                        }
                        ok++;
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException)) throw;
                        r.Result = "FAILED: " + e.Message;
                        failed++;
                    }
                    int done = ok + failed;
                    Ui(() => lblSummary.Text = string.Format("Writing to ALM...  {0} / {1}", done, todo.Count));
                }
                Log(string.Format("Done: {0} test case(s) updated, {1} failed.", ok, failed));
            }, () =>
            {
                btnStop.Enabled = false;
                Fill();
            });
        }
    }
}
