// ALM Image Uploader - delete chosen comments from defects' Comments (by date, author or one by one).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AlmImageUploader
{
    /// One comment inside a defect's Comments: "____" line, "user, date:" and its text.
    public class CommentEntry
    {
        public string DefectId, Author, Date, Text, Html;
        public int Start, End;      // place in the Comments HTML
        public bool Tick;

        public DateTime When { get { return CommentEntries.ParseDate(Date); } }
    }

    public static class CommentEntries
    {
        // "<b>bob &lt;Bob Tester&gt;, 21/10/2026:</b>" (ALM writes the user and the date in bold)
        static readonly Regex Header = new Regex(@"<b>\s*([^<]{1,200}?),\s*(\d{1,4}[/.\-]\d{1,2}[/.\-]\d{1,4})\s*:?\s*</b>",
                                                 RegexOptions.IgnoreCase);
        static readonly Regex Separator = new Regex(@"_{10,}");

        /// The order of dates, for sorting only: day/month/year first (as written here), else month/day/year.
        public static DateTime ParseDate(string s)
        {
            DateTime d;
            foreach (var f in new[] { "d/M/yyyy", "d/M/yy", "M/d/yyyy", "yyyy-M-d", "yyyy/M/d", "d.M.yyyy", "d-M-yyyy" })
                if (DateTime.TryParseExact(s ?? "", f, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return DateTime.MinValue;
        }

        /// Where to cut before a comment: the start of the block (div / p) holding its "____" line or,
        /// without one, its header; never before 'bound' (the end of the comment before it).
        static int CutBefore(string html, int anchor, int bound)
        {
            var before = html.Substring(bound, anchor - bound);
            var m = Regex.Matches(before, @"<(div|p)\b", RegexOptions.IgnoreCase).Cast<Match>().LastOrDefault();
            if (m != null) return bound + m.Index;
            // no block of its own: step back over the tags that open the line (<br /><font><b> ...)
            int at = anchor;
            while (true)
            {
                var t = Regex.Match(html.Substring(bound, at - bound), @"(<(b|font|span|strong|u|i|em)\b[^>]*>|<br\s*/?>)\s*$", RegexOptions.IgnoreCase);
                if (!t.Success) return at;
                at = bound + t.Index;
            }
        }

        /// The comments of a defect's Comments, in order. Text before the first comment is not one.
        public static List<CommentEntry> Split(string html, string defectId)
        {
            var list = new List<CommentEntry>();
            if (string.IsNullOrEmpty(html)) return list;
            int bodyStart = 0, bodyEnd = html.Length;
            var mb = Regex.Match(html, @"<body\b[^>]*>", RegexOptions.IgnoreCase);
            if (mb.Success) bodyStart = mb.Index + mb.Length;
            int be = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (be >= bodyStart) bodyEnd = be;

            var heads = Header.Matches(html).Cast<Match>().Where(m => m.Index >= bodyStart && m.Index < bodyEnd).ToList();
            var starts = new List<int>();
            int prevEnd = bodyStart;
            foreach (var h in heads)
            {
                int anchor = h.Index;
                var sep = Separator.Matches(html.Substring(prevEnd, h.Index - prevEnd)).Cast<Match>().LastOrDefault();
                if (sep != null) anchor = prevEnd + sep.Index;
                starts.Add(CutBefore(html, anchor, prevEnd));
                prevEnd = h.Index + h.Length;
            }
            for (int i = 0; i < heads.Count; i++)
            {
                int start = starts[i], end = i + 1 < heads.Count ? starts[i + 1] : bodyEnd;
                var h = heads[i];
                var segment = html.Substring(start, end - start);
                var after = html.Substring(h.Index + h.Length, end - (h.Index + h.Length));
                list.Add(new CommentEntry
                {
                    DefectId = defectId, Start = start, End = end, Html = segment,
                    Author = WebUtility.HtmlDecode(h.Groups[1].Value).Trim(), Date = h.Groups[2].Value,
                    Text = DefectLinks.PlainText(Regex.Replace(after, @"<img\b[^>]*>", " [picture] ", RegexOptions.IgnoreCase)),
                });
            }
            return list;
        }

        static readonly HashSet<string> VoidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "br", "img", "hr", "meta", "input", "link", "wbr", "col", "area", "base" };

        /// The tags of a piece of HTML that open or close outside it (e.g. the "</div>" of a block
        /// the comment shares with the one before), in their order: they stay when it is removed.
        public static string Unmatched(string segment)
        {
            var tags = Regex.Matches(segment, @"<(/?)([a-zA-Z][a-zA-Z0-9]*)\b[^>]*?(/?)>").Cast<Match>().ToList();
            var open = new List<Match>();
            var keep = new List<Match>();
            foreach (var t in tags)
            {
                var name = t.Groups[2].Value;
                if (VoidTags.Contains(name) || t.Groups[3].Value == "/") continue;
                if (t.Groups[1].Value == "")
                    open.Add(t);
                else
                {
                    int k = open.FindLastIndex(o => string.Equals(o.Groups[2].Value, name, StringComparison.OrdinalIgnoreCase));
                    if (k >= 0) open.RemoveRange(k, open.Count - k);
                    else keep.Add(t);
                }
            }
            keep.AddRange(open);
            return string.Concat(keep.OrderBy(t => t.Index).Select(t => t.Value));
        }

        /// The Comments without the given comments (matched by their HTML in the current text).
        public static string Remove(string html, List<CommentEntry> current, IEnumerable<CommentEntry> drop)
        {
            var sb = new StringBuilder(html);
            foreach (var e in current.Where(c => drop.Any(d => d.Html == c.Html)).OrderByDescending(c => c.Start))
            {
                sb.Remove(e.Start, e.End - e.Start);
                sb.Insert(e.Start, Unmatched(e.Html));
            }
            return sb.ToString();
        }
    }

    // ====================================================================== dialog
    public class DeleteCommentsDialog : WorkDialog
    {
        readonly AlmClient client;
        readonly List<string> ids;
        readonly string field, label;
        protected readonly CheckedListBox lstDates;
        protected readonly ComboBox cboAuthor;
        protected readonly ListView lv;
        protected readonly TextBox txtPreview;
        protected readonly Button btnDelete, btnTickAll, btnUntickAll;
        readonly Label lblSummary;
        bool filling;

        protected List<CommentEntry> entries = new List<CommentEntry>();
        protected readonly Dictionary<string, string> current = new Dictionary<string, string>();
        readonly Dictionary<string, string> summaries = new Dictionary<string, string>();
        /// Defect id -> its Comments after deleting (for the caller).
        public readonly Dictionary<string, string> Changed = new Dictionary<string, string>();
        public string BackupFolder;

        const string AllAuthors = "(everyone)";

        public DeleteCommentsDialog(AlmClient client, List<string> ids, string field, string label)
        {
            this.client = client;
            this.ids = ids;
            this.field = field;
            this.label = label;
            Text = "Delete defect comments";
            ClientSize = new Size(1000, 680);
            MaximizeBox = true;
            BackupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ALM Image Uploader", "Comment backups");

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var head = new Label
            {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8),
                Text = string.Format("Choose the comments to delete from the {0} of {1} defect(s)", label, ids.Count),
            };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 2);

            // left: dates and author
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0) };
            left.Controls.Add(new Label { Text = "Tick a date to tick all its comments:", AutoSize = true }, 0, 0);
            lstDates = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            lstDates.ItemCheck += (s, e) =>
            {
                if (filling) return;
                // only the comments of that date that are shown (the chosen author's)
                var date = ((DateItem)lstDates.Items[e.Index]).Date;
                bool on = e.NewValue == CheckState.Checked;
                foreach (var c in ShownEntries().Where(c => c.Date == date)) c.Tick = on;
                BeginInvoke(new Action(Fill));
            };
            left.Controls.Add(lstDates, 0, 1);
            left.Controls.Add(new Label { Text = "Written by:", AutoSize = true, Margin = new Padding(3, 8, 3, 0) }, 0, 2);
            cboAuthor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            cboAuthor.Items.Add(AllAuthors);
            cboAuthor.SelectedIndex = 0;
            // another author only changes what is shown, never what is ticked
            cboAuthor.SelectedIndexChanged += (s, e) => { FillDates(); Fill(); };
            left.Controls.Add(cboAuthor, 0, 3);
            left.RowCount = 4;
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(left, 0, 1);

            // right: the comments and the selected one in full
            var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, CheckBoxes = true };
            lv.Columns.Add("Defect", 170);
            lv.Columns.Add("Date", 85);
            lv.Columns.Add("Written by", 140);
            lv.Columns.Add("Comment", 330);
            lv.ItemChecked += (s, e) =>
            {
                if (filling) return;
                var c = e.Item.Tag as CommentEntry;
                if (c != null) c.Tick = e.Item.Checked;
                Summary();
                BeginInvoke(new Action(SyncDates));
            };
            lv.SelectedIndexChanged += (s, e) =>
            {
                var c = lv.SelectedItems.Count == 1 ? lv.SelectedItems[0].Tag as CommentEntry : null;
                txtPreview.Text = c == null ? "" : "Defect " + c.DefectId + "  -  " + c.Author + ", " + c.Date + ":" + Environment.NewLine + c.Text;
            };
            right.Panel1.Controls.Add(lv);
            txtPreview = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
            right.Panel2.Controls.Add(txtPreview);
            t.Controls.Add(right, 1, 1);
            Shown += (s, e) => { try { right.SplitterDistance = Math.Max(100, right.Height * 70 / 100); } catch (InvalidOperationException) { } };

            lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3), Text = "Reading the comments..." };
            t.Controls.Add(lblSummary, 0, 2);
            t.SetColumnSpan(lblSummary, 2);
            var row = new FlowLayoutPanel { AutoSize = true };
            btnDelete = Btn("Delete ticked comments", true);
            btnDelete.ForeColor = Color.Firebrick;
            btnTickAll = Btn("Tick all shown");
            btnUntickAll = Btn("Untick all");
            var close = Btn("Close");
            close.DialogResult = DialogResult.Cancel;
            btnDelete.Click += (s, e) => DoDelete();
            btnTickAll.Click += (s, e) => { foreach (var c in ShownEntries()) c.Tick = true; Fill(); };
            btnUntickAll.Click += (s, e) => { foreach (var c in entries) c.Tick = false; Fill(); };
            row.Controls.AddRange(new Control[] { btnDelete, btnTickAll, btnUntickAll, close });
            t.Controls.Add(row, 0, 3);
            t.SetColumnSpan(row, 2);
            txtLog = LogBox();
            t.Controls.Add(txtLog, 0, 4);
            t.SetColumnSpan(txtLog, 2);
            t.RowCount = 5;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 78));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
            Controls.Add(t);
            CancelButton = close;
            UpdateButtons();
            Shown += (s, e) => Reload();
        }

        // ------------------------------------------------------------ read
        protected void Reload()
        {
            if (Busy) return;
            var read = new Dictionary<string, string>();
            var names = new Dictionary<string, string>();
            Run(() =>
            {
                foreach (var d in client.GetByIds("defects", "id", ids, new[] { "id", "name", field }))
                {
                    string v;
                    read[d["id"]] = d.TryGetValue(field, out v) ? v ?? "" : "";
                    names[d["id"]] = d.TryGetValue("name", out v) ? v ?? "" : "";
                }
                var missing = ids.Where(i => !read.ContainsKey(i)).ToList();
                if (missing.Count > 0) Log("Not found in ALM: defect " + string.Join(", ", missing));
            }, () =>
            {
                var ticked = new HashSet<string>(entries.Where(c => c.Tick).Select(c => c.DefectId + "\u0001" + c.Html));
                current.Clear();
                foreach (var kv in read) current[kv.Key] = kv.Value;
                foreach (var kv in names) summaries[kv.Key] = kv.Value;
                entries = ids.Where(current.ContainsKey).SelectMany(id => CommentEntries.Split(current[id], id)).ToList();
                foreach (var c in entries) c.Tick = ticked.Contains(c.DefectId + "\u0001" + c.Html);
                var authors = entries.Select(c => c.Author).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                var keep = cboAuthor.SelectedItem as string;
                cboAuthor.Items.Clear();
                cboAuthor.Items.Add(AllAuthors);
                foreach (var a in authors) cboAuthor.Items.Add(a);
                cboAuthor.SelectedItem = keep != null && cboAuthor.Items.Contains(keep) ? keep : AllAuthors;
                FillDates();
                Fill();
                Log(string.Format("{0} comment(s) in {1} defect(s).", entries.Count, current.Count));
            });
        }

        // ------------------------------------------------------------ dates
        class DateItem
        {
            public string Date;
            public int Count;
            public override string ToString() { return Date + "   (" + Count + ")"; }
        }

        string Author { get { var a = cboAuthor.SelectedItem as string; return a == null || a == AllAuthors ? null : a; } }

        IEnumerable<CommentEntry> ShownEntries()
        {
            var a = Author;
            return entries.Where(c => a == null || string.Equals(c.Author, a, StringComparison.OrdinalIgnoreCase));
        }

        /// The dates of the comments shown (the chosen author's), newest first.
        void FillDates()
        {
            filling = true;
            lstDates.Items.Clear();
            foreach (var g in ShownEntries().GroupBy(c => c.Date).OrderByDescending(g => g.First().When).ThenBy(g => g.Key))
                lstDates.Items.Add(new DateItem { Date = g.Key, Count = g.Count() }, false);
            filling = false;
            SyncDates();
        }

        /// A date is ticked when all its shown comments are.
        void SyncDates()
        {
            filling = true;
            var shown = ShownEntries().ToList();
            for (int i = 0; i < lstDates.Items.Count; i++)
            {
                var date = ((DateItem)lstDates.Items[i]).Date;
                var mine = shown.Where(c => c.Date == date).ToList();
                lstDates.SetItemChecked(i, mine.Count > 0 && mine.All(c => c.Tick));
            }
            filling = false;
        }

        // ------------------------------------------------------------ list
        protected void Fill()
        {
            SyncDates();
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var c in ShownEntries())
            {
                string name;
                summaries.TryGetValue(c.DefectId, out name);
                var text = c.Text.Length > 200 ? c.Text.Substring(0, 200) + "..." : c.Text;
                lv.Items.Add(new ListViewItem(new[] { c.DefectId + (string.IsNullOrEmpty(name) ? "" : "  " + name), c.Date, c.Author, text })
                {
                    Tag = c, Checked = c.Tick, ForeColor = c.Tick ? Color.Firebrick : SystemColors.WindowText,
                });
            }
            lv.EndUpdate();
            filling = false;
            Summary();
        }

        void Summary()
        {
            int n = entries.Count(c => c.Tick);
            lblSummary.Text = string.Format("{0} comment(s) in {1} defect(s);   {2} ticked to delete in {3} defect(s).   Shown: {4}.",
                                            entries.Count, current.Count, n, entries.Where(c => c.Tick).Select(c => c.DefectId).Distinct().Count(),
                                            Author ?? "everyone");
            UpdateButtons();
        }

        void UpdateButtons()
        {
            btnDelete.Enabled = !Busy && entries.Any(c => c.Tick);
            btnTickAll.Enabled = btnUntickAll.Enabled = !Busy && entries.Count > 0;
        }

        // ------------------------------------------------------------ delete
        protected void DoDelete()
        {
            if (Busy) return;
            var drop = entries.Where(c => c.Tick).ToList();
            if (drop.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendFormat("Delete {0} comment(s) from the {1} of {2} defect(s) in ALM?", drop.Count, label,
                            drop.Select(c => c.DefectId).Distinct().Count()).AppendLine().AppendLine();
            foreach (var c in drop.Take(20))
                sb.AppendLine("   Defect " + c.DefectId + "  |  " + c.Date + "  |  " + c.Author + "  |  "
                              + (c.Text.Length > 70 ? c.Text.Substring(0, 70) + "..." : c.Text));
            if (drop.Count > 20) sb.AppendLine("   ... and " + (drop.Count - 20) + " more (see the list).");
            sb.AppendLine().Append("The other comments stay. A copy of each defect's Comments as it is now is saved in" + Environment.NewLine + "   " + BackupFolder);
            if (!Confirm(sb.ToString())) return;

            btnDelete.Enabled = false;
            int ok = 0, failed = 0, deleted = 0;
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            Run(() =>
            {
                foreach (var g in drop.GroupBy(c => c.DefectId))
                {
                    if (stop) break;
                    var id = g.Key;
                    try
                    {
                        // the Comments now: someone may have changed them meanwhile
                        var now = client.GetByIds("defects", "id", new[] { id }, new[] { "id", field }).FirstOrDefault();
                        string html;
                        if (now == null || !now.TryGetValue(field, out html)) throw new AlmException("defect " + id + " not found");
                        html = html ?? "";
                        var fresh = CommentEntries.Split(html, id);
                        var gone = g.Where(c => !fresh.Any(f => f.Html == c.Html)).ToList();
                        if (gone.Count > 0) Log("Defect " + id + ": " + gone.Count + " ticked comment(s) changed meanwhile - left alone.");
                        var mine = g.Where(c => fresh.Any(f => f.Html == c.Html)).ToList();
                        if (mine.Count == 0) continue;

                        Directory.CreateDirectory(BackupFolder);
                        var backup = Path.Combine(BackupFolder, "defect " + id + " " + stamp + ".html");
                        File.WriteAllText(backup, html, new UTF8Encoding(false));

                        var after = CommentEntries.Remove(html, fresh, mine);
                        client.UpdateEntity("defects", id, new Dictionary<string, string> { { field, after } });

                        // check: exactly those comments are gone
                        var back = client.GetByIds("defects", "id", new[] { id }, new[] { "id", field }).FirstOrDefault();
                        string saved;
                        saved = back != null && back.TryGetValue(field, out saved) ? saved ?? "" : after;
                        int left = CommentEntries.Split(saved, id).Count;
                        lock (Changed) Changed[id] = saved;
                        deleted += mine.Count;
                        ok++;
                        Log(string.Format("[ OK ] defect {0}: deleted {1} comment(s), {2} left.  Copy of the old Comments: {3}",
                                          id, mine.Count, left, backup));
                        if (left != fresh.Count - mine.Count)
                            Log("       note: expected " + (fresh.Count - mine.Count) + " comment(s) left - check the defect in ALM (the copy above has the old text).");
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException || e is IOException || e is UnauthorizedAccessException)) throw;
                        failed++;
                        Log("[FAIL] defect " + id + ": " + e.Message);
                    }
                }
                Log(string.Format("Done: {0} comment(s) deleted from {1} defect(s), {2} failed.", deleted, ok, failed));
            }, () =>
            {
                foreach (var c in entries) c.Tick = false;
                Reload();
            });
        }
    }
}
