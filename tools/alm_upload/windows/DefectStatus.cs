// ALM Image Uploader - change test cases through the defects linked to them.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AlmImageUploader
{
    /// One defect ID asked for, with what ALM says about it.
    public class DefectInfo
    {
        public string Id, Summary = "", Status = "", Problem, LinkError;
        public bool Found;
        /// Linked entities that are not test cases in Test Lab, e.g. "test 501".
        public readonly List<string> LinkedTo = new List<string>();
        /// Values for this defect from the imported list, by field name, e.g. { "status": "Passed" }.
        public readonly Dictionary<string, string> ListValues = new Dictionary<string, string>();
    }

    /// One test case (test instance) linked to one or more of the defects.
    public class DefectCase
    {
        public TestInstance Inst;
        public string SetPath = "";
        public int Order;
        public readonly List<DefectInfo> Defects = new List<DefectInfo>();
        /// Values in ALM now, by field name.
        public readonly Dictionary<string, string> Current = new Dictionary<string, string>();
        /// The fields that change and their new values.
        public readonly Dictionary<string, string> New = new Dictionary<string, string>();
        public string Error, Result = "";
        public bool Ticked = true;

        public string DefectIds { get { return string.Join(", ", Defects.Select(d => d.Id)); } }

        public string Now(string field)
        {
            string v;
            return Current.TryGetValue(field, out v) ? v ?? "" : "";
        }
    }

    /// A list of defect IDs read from Excel / CSV / text; other columns hold values per defect.
    public class DefectListFile
    {
        public readonly List<string> Ids = new List<string>();
        public readonly List<string> Columns = new List<string>();
        /// defect id -> column title -> value
        public readonly Dictionary<string, Dictionary<string, string>> Values = new Dictionary<string, Dictionary<string, string>>();
    }

    public static class DefectLinks
    {
        static readonly string[] LinkFields = { "id", "first-endpoint-id", "second-endpoint-id", "second-endpoint-type" };
        static readonly string[] IdTitles = { "defect id", "defect", "defects", "defect no", "defect number", "defect #", "bug id", "bug", "id" };

        /// Defect IDs typed or pasted: any numbers, separated by commas, spaces or new lines.
        public static List<string> ParseIds(string text)
        {
            return Regex.Matches(text ?? "", @"\d+").Cast<Match>().Select(m => m.Value.TrimStart('0'))
                        .Where(v => v != "").Distinct().ToList();
        }

        // ------------------------------------------------------------------ list files
        public static DefectListFile ReadList(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var rows = ext == ".xlsx" || ext == ".xlsm" ? Xlsx.Read(path, "Defects") : ReadText(path);
            var list = new DefectListFile();
            int headRow = -1, idCol = -1;
            // the title row: the best defect column title ("Defect ID" before a plain "ID")
            for (int r = 0; r < Math.Min(rows.Count, 10) && headRow < 0; r++)
            {
                int best = int.MaxValue;
                for (int c = 0; c < rows[r].Length; c++)
                {
                    int k = Array.IndexOf(IdTitles, Util.Norm(rows[r][c]));
                    if (k >= 0 && k < best) { best = k; headRow = r; idCol = c; }
                }
            }
            if (headRow < 0)
            {
                // no title row: the first column that holds numbers
                int width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
                for (int c = 0; c < width && idCol < 0; c++)
                    if (rows.Any(r => c < r.Length && Regex.IsMatch(r[c] ?? "", @"^\s*#?\d+(\.0+)?\s*$"))) idCol = c;
                if (idCol < 0) idCol = 0;
            }
            var head = headRow >= 0 ? rows[headRow] : new string[0];
            for (int c = 0; c < head.Length; c++)
                if (c != idCol && (head[c] ?? "").Trim() != "") list.Columns.Add(head[c].Trim());
            for (int r = headRow + 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var cell = idCol < row.Length ? row[idCol] ?? "" : "";
                var m = Regex.Match(cell, @"^\s*#?(\d+)\.0+\s*$");
                var ids = m.Success ? new List<string> { m.Groups[1].Value.TrimStart('0') } : ParseIds(cell);
                foreach (var id in ids)
                {
                    if (!list.Ids.Contains(id)) list.Ids.Add(id);
                    Dictionary<string, string> vals;
                    if (!list.Values.TryGetValue(id, out vals)) list.Values[id] = vals = new Dictionary<string, string>();
                    for (int c = 0; c < head.Length; c++)
                    {
                        if (c == idCol || (head[c] ?? "").Trim() == "") continue;
                        var v = c < row.Length ? (row[c] ?? "").Trim() : "";
                        if (v != "" || !vals.ContainsKey(head[c].Trim())) vals[head[c].Trim()] = v;
                    }
                }
            }
            return list;
        }

        /// CSV or text: the separator is the most common of , ; and tab in the first line.
        static List<string[]> ReadText(string path)
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var first = lines.FirstOrDefault(l => l.Trim() != "") ?? "";
            char sep = new[] { ',', ';', '\t' }.OrderByDescending(ch => first.Count(x => x == ch)).First();
            if (first.Count(x => x == sep) == 0) sep = '\0';
            return lines.Select(l => sep == '\0' ? new[] { l } : SplitCsv(l, sep)).ToList();
        }

        static string[] SplitCsv(string line, char sep)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else sb.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == sep) { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells.ToArray();
        }

        // ------------------------------------------------------------------ ALM
        static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? v ?? "" : "";
        }

        static string Kind(string type)
        {
            var t = (type ?? "").Trim().ToLowerInvariant();
            if (t == "test-instance" || t == "test-instances" || t == "testcycl" || t == "test-instance-link") return "instance";
            if (t == "run" || t == "runs") return "run";
            if (t == "run-step" || t == "run-steps" || t == "step" || t == "steps") return "step";
            return t;
        }

        /// The links of the defects. ALM wants the defect ids of a link query as a comma separated
        /// list ("{first-endpoint-id[1,2,3]}", not "1 OR 2"); when that fails, one defect at a time.
        static List<Dictionary<string, string>> ReadLinks(AlmClient client, List<DefectInfo> found, Action<string> log)
        {
            var links = new List<Dictionary<string, string>>();
            var ids = found.Select(d => d.Id).Distinct().ToList();
            try
            {
                for (int n = 0; n < ids.Count; n += 50)
                    links.AddRange(client.GetEntities("defect-links", "{first-endpoint-id[" + string.Join(",", ids.Skip(n).Take(50)) + "]}", LinkFields));
                // other ALM versions may want "1 OR 2" instead: ask again for the defects without links
                var none = ids.Where(id => !links.Any(l => Get(l, "first-endpoint-id") == id)).ToList();
                if (none.Count > 1)
                {
                    try
                    {
                        var known = new HashSet<string>(links.Select(l => Get(l, "id")));
                        links.AddRange(client.GetByIds("defect-links", "first-endpoint-id", none, LinkFields)
                                             .Where(l => Get(l, "id") == "" || known.Add(Get(l, "id"))));
                    }
                    catch (AlmException) { }
                }
                return links;
            }
            catch (AlmException e)
            {
                log("Reading the links of each defect one by one (" + e.Message + ")");
                links.Clear();
            }
            foreach (var d in found)
            {
                try
                {
                    List<Dictionary<string, string>> mine;
                    try { mine = client.GetEntities("defect-links", "{first-endpoint-id[" + d.Id + "]}", LinkFields); }
                    catch (AlmException first)
                    {
                        try { mine = client.GetEntities("defects/" + d.Id + "/defect-links", null, LinkFields); }
                        catch (AlmException) { throw first; }
                    }
                    foreach (var l in mine)
                    {
                        if (Get(l, "first-endpoint-id") == "") l["first-endpoint-id"] = d.Id;
                        links.Add(l);
                    }
                }
                catch (AlmException e)
                {
                    d.LinkError = e.Message;
                    log("Could not read the links of defect " + d.Id + ": " + e.Message);
                }
            }
            return links;
        }

        /// Reads the defects, their links, and the test cases the links lead to (directly, or through a
        /// run / run step of the test case), with the current values of the given fields.
        public static List<DefectCase> Find(AlmClient client, List<DefectInfo> defects, IEnumerable<string> fields,
                                            TestLabFolder lab, Action<string> log)
        {
            var byId = defects.ToDictionary(d => d.Id);
            foreach (var b in client.GetByIds("defects", "id", byId.Keys, new[] { "id", "name", "status" }))
            {
                DefectInfo d;
                if (!byId.TryGetValue(Get(b, "id"), out d)) continue;
                d.Found = true;
                d.Summary = Get(b, "name");
                d.Status = Get(b, "status");
            }
            var found = defects.Where(d => d.Found).ToList();

            var links = ReadLinks(client, found, log);

            // defect -> test instance ids, directly or through runs / steps
            var instOf = new Dictionary<string, List<string>>();
            var runOf = new Dictionary<string, List<string>>();
            var stepOf = new Dictionary<string, List<string>>();
            Action<Dictionary<string, List<string>>, string, string> add = (map, defect, id) =>
            {
                List<string> l;
                if (!map.TryGetValue(defect, out l)) map[defect] = l = new List<string>();
                if (!l.Contains(id)) l.Add(id);
            };
            foreach (var l in links)
            {
                DefectInfo d;
                if (!byId.TryGetValue(Get(l, "first-endpoint-id"), out d)) continue;
                var other = Get(l, "second-endpoint-id");
                var kind = Kind(Get(l, "second-endpoint-type"));
                if (kind == "instance") add(instOf, d.Id, other);
                else if (kind == "run") add(runOf, d.Id, other);
                else if (kind == "step") add(stepOf, d.Id, other);
                else if (kind != "defect" && kind != "bug") d.LinkedTo.Add(kind + " " + other);
            }
            if (stepOf.Count > 0)
            {
                var runOfStep = client.GetByIds("run-steps", "id", stepOf.Values.SelectMany(x => x), new[] { "id", "parent-id" })
                                      .ToDictionary(s => Get(s, "id"), s => Get(s, "parent-id"));
                foreach (var kv in stepOf)
                    foreach (var s in kv.Value)
                    {
                        string run;
                        if (runOfStep.TryGetValue(s, out run) && run != "") add(runOf, kv.Key, run);
                    }
            }
            if (runOf.Count > 0)
            {
                var instOfRun = client.GetByIds("runs", "id", runOf.Values.SelectMany(x => x), new[] { "id", "testcycl-id" })
                                      .ToDictionary(r => Get(r, "id"), r => Get(r, "testcycl-id"));
                foreach (var kv in runOf)
                    foreach (var r in kv.Value)
                    {
                        string inst;
                        if (instOfRun.TryGetValue(r, out inst) && inst != "") add(instOf, kv.Key, inst);
                    }
            }

            // the test cases, their test sets and Test Lab folders
            var want = new[] { "id", "test-id", "cycle-id", "name" }.Concat(fields).Distinct().ToArray();
            var insts = client.GetByIds("test-instances", "id", instOf.Values.SelectMany(x => x), want).ToDictionary(i => Get(i, "id"));
            var tests = client.GetByIds("tests", "id", insts.Values.Select(i => Get(i, "test-id")), new[] { "id", "name" })
                              .ToDictionary(t => Get(t, "id"), t => Get(t, "name"));
            var sets = client.GetByIds("test-sets", "id", insts.Values.Select(i => Get(i, "cycle-id")), new[] { "id", "name", "parent-id" })
                             .ToDictionary(s => Get(s, "id"));
            var folders = new Dictionary<string, Dictionary<string, string>>();
            var need = sets.Values.Select(s => Get(s, "parent-id")).Where(p => p != "" && p != "0").Distinct().ToList();
            for (int depth = 0; need.Count > 0 && depth < 40; depth++)
            {
                foreach (var f in client.GetByIds("test-set-folders", "id", need, new[] { "id", "name", "parent-id" }))
                    folders[Get(f, "id")] = f;
                need = need.Where(folders.ContainsKey).Select(id => Get(folders[id], "parent-id"))
                           .Where(p => p != "" && p != "0" && !folders.ContainsKey(p)).Distinct().ToList();
            }
            Func<string, string> pathOf = setId =>
            {
                Dictionary<string, string> s;
                if (!sets.TryGetValue(setId, out s)) return "(test set " + setId + ")";
                var parts = new List<string> { Get(s, "name") };
                var p = Get(s, "parent-id");
                for (int depth = 0; p != "" && p != "0" && folders.ContainsKey(p) && depth < 40; depth++)
                {
                    parts.Insert(0, Get(folders[p], "name"));
                    p = Get(folders[p], "parent-id");
                }
                return string.Join("\\", parts);
            };

            var cases = new Dictionary<string, DefectCase>();
            var fieldList = fields.ToList();
            for (int k = 0; k < defects.Count; k++)
            {
                var d = defects[k];
                List<string> ids;
                if (!instOf.TryGetValue(d.Id, out ids)) continue;
                foreach (var id in ids)
                {
                    Dictionary<string, string> i;
                    if (!insts.TryGetValue(id, out i)) continue;
                    DefectCase c;
                    if (!cases.TryGetValue(id, out c))
                    {
                        c = new DefectCase { Order = k, SetPath = pathOf(Get(i, "cycle-id")) };
                        c.Inst = lab == null ? null : lab.FindInstance(id);
                        if (c.Inst == null)
                        {
                            string tn;
                            tests.TryGetValue(Get(i, "test-id"), out tn);
                            var name = Get(i, "name");
                            var m = Regex.Match(name, @"^\[(\d+)\]");
                            c.Inst = new TestInstance
                            {
                                Id = id, TestId = Get(i, "test-id"), SetId = Get(i, "cycle-id"), Name = name, TestName = tn ?? "",
                                Label = m.Success && m.Groups[1].Value != "1" ? name : (tn ?? name),
                            };
                        }
                        foreach (var f in fieldList) c.Current[f] = Get(i, f);
                        cases[id] = c;
                    }
                    if (!c.Defects.Contains(d)) c.Defects.Add(d);
                }
            }
            foreach (var d in defects)
            {
                if (!d.Found) d.Problem = "defect not found in ALM";
                else if (d.LinkError != null) d.Problem = "could not read its links: " + d.LinkError;
                else if (!cases.Values.Any(c => c.Defects.Contains(d)))
                    d.Problem = "no linked test case in Test Lab" + (d.LinkedTo.Count > 0 ? " (linked to " + string.Join(", ", d.LinkedTo.Take(5)) + ")" : "");
            }
            return cases.Values.OrderBy(c => c.Order).ThenBy(c => c.SetPath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(c => c.Inst.Label, Comparer<string>.Create(Util.NaturalCompare)).ToList();
        }

        /// Works out the new values of every test case: a value typed for all, unless the list gives
        /// the defect its own value. Values of selection-list fields must be in the list.
        public static void Plan(List<DefectCase> cases, List<KeyValuePair<AlmClient.FieldDef, string>> forAll,
                                Dictionary<string, AlmClient.FieldDef> defs, Dictionary<string, List<string>> listValues)
        {
            foreach (var c in cases)
            {
                c.New.Clear();
                c.Error = null;
                var want = new Dictionary<string, string>();
                foreach (var kv in forAll) want[kv.Key.Name] = kv.Value;
                foreach (var f in c.Defects.SelectMany(d => d.ListValues.Keys).Distinct().ToList())
                {
                    var vals = c.Defects.Select(d => { string v; return d.ListValues.TryGetValue(f, out v) ? v : ""; })
                                .Where(v => v != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (vals.Count > 1)
                    {
                        c.Error = string.Format("the list gives different {0} for defects {1}: {2}",
                                                defs[f].Label, c.DefectIds, string.Join(" / ", vals));
                        break;
                    }
                    if (vals.Count == 1) want[f] = vals[0];
                }
                if (c.Error != null) continue;
                foreach (var kv in want)
                {
                    var def = defs[kv.Key];
                    var value = kv.Value;
                    List<string> allowed;
                    if (value != "" && def.ListId != "" && listValues.TryGetValue(def.ListId, out allowed) && allowed.Count > 0)
                    {
                        var known = allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
                        if (known == null)
                        {
                            c.Error = string.Format("'{0}' is not in the {1} list ({2})", value, def.Label, string.Join(", ", allowed.Take(8)));
                            break;
                        }
                        value = known;
                    }
                    if (value != c.Now(kv.Key)) c.New[kv.Key] = value;
                }
                if (c.Error != null) c.New.Clear();
            }
        }

        /// Writes the new values of one test case. The status is set directly, or through a run
        /// when ALM refuses that; the other fields are changed together first.
        public static string Apply(AlmClient client, DefectCase c, Dictionary<string, AlmClient.FieldDef> defs)
        {
            var done = new List<string>();
            var others = c.New.Where(kv => kv.Key != "status").ToDictionary(kv => kv.Key, kv => kv.Value);
            if (others.Count > 0)
            {
                client.UpdateEntity("test-instances", c.Inst.Id, others);
                foreach (var kv in others) { c.Current[kv.Key] = kv.Value; done.Add(defs[kv.Key].Label); }
            }
            string status;
            if (c.New.TryGetValue("status", out status))
            {
                var label = defs.ContainsKey("status") ? defs["status"].Label : "Status";
                try
                {
                    client.UpdateEntity("test-instances", c.Inst.Id, new Dictionary<string, string> { { "status", status } });
                    done.Add(label);
                }
                catch (AlmException)
                {
                    client.CreateRun(c.Inst, status);
                    done.Add(label + " (as a run)");
                }
                c.Current["status"] = status;
            }
            c.New.Clear();
            return done.Count == 0 ? "no change" : "updated " + string.Join(" + ", done);
        }
    }

    // ====================================================================== dialog
    public class DefectStatusDialog : WorkDialog
    {
        readonly AlmClient client;
        readonly TestLabFolder lab;
        protected readonly TextBox txtIds;
        protected readonly ComboBox cboField, cboValue, cboShow;
        protected readonly ListBox lstChanges;
        protected readonly ListView lv;
        protected readonly Button btnFind, btnApply, btnStop, btnAdd, btnRemove, btnImportList, btnTickAll, btnUntickAll;
        readonly Label lblSummary, lblList;
        bool filling;
        int listSeq;

        protected Dictionary<string, AlmClient.FieldDef> defs;
        protected readonly List<KeyValuePair<AlmClient.FieldDef, string>> forAll = new List<KeyValuePair<AlmClient.FieldDef, string>>();
        protected readonly Dictionary<string, List<string>> listValues = new Dictionary<string, List<string>>();
        protected DefectListFile listFile;
        protected List<DefectInfo> defects = new List<DefectInfo>();
        protected List<DefectCase> cases = new List<DefectCase>();

        public readonly List<FieldChange> Changes = new List<FieldChange>();

        public DefectStatusDialog(AlmClient client, TestLabFolder lab, string folder)
        {
            this.client = client;
            this.lab = lab;
            Text = "Change test cases by defect";
            ClientSize = new Size(1040, 700);
            MaximizeBox = true;

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var head = new Label
            {
                AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8),
                Text = "Change the test cases linked to defects (anywhere in this project's Test Lab)",
            };
            t.Controls.Add(head, 0, 0);
            t.SetColumnSpan(head, 3);

            // 1. defect IDs
            t.Controls.Add(new Label { Text = "1.  Defect IDs", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 1);
            txtIds = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 58 };
            txtIds.TextChanged += (s, e) => UpdateButtons();
            t.Controls.Add(txtIds, 1, 1);
            btnImportList = Btn("Import list...");
            var btnClear = Btn("Clear");
            btnImportList.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Defect lists (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt|All files (*.*)|*.*", InitialDirectory = folder })
                    if (d.ShowDialog(this) == DialogResult.OK) ImportList(d.FileName);
            };
            btnClear.Click += (s, e) => { if (Busy) return; txtIds.Text = ""; listFile = null; lblList.Text = ""; ClearResults(); };
            var side = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            side.Controls.AddRange(new Control[] { btnImportList, btnClear });
            t.Controls.Add(side, 2, 1);
            lblList = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6), MaximumSize = new Size(820, 0),
                                  Text = "Type or paste defect IDs (e.g. 1203, 1207 1210 or one per line), or import an Excel / CSV / text list. "
                                       + "List columns named after a field (e.g. Status, Comments) give each defect its own value." };
            t.Controls.Add(lblList, 1, 2);
            t.SetColumnSpan(lblList, 2);

            // 2. the changes
            t.Controls.Add(new Label { Text = "2.  Change", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 3);
            var editor = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            cboField = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            cboValue = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            btnAdd = Btn("Add  >");
            lstChanges = new ListBox { Dock = DockStyle.Fill, Height = 76, IntegralHeight = false };
            btnRemove = Btn("Remove");
            cboField.SelectedIndexChanged += (s, e) => OnFieldChanged();
            cboValue.TextChanged += (s, e) => UpdateButtons();
            cboValue.SelectedIndexChanged += (s, e) => UpdateButtons();
            btnAdd.Click += (s, e) => AddChange();
            btnRemove.Click += (s, e) => RemoveChange();
            lstChanges.SelectedIndexChanged += (s, e) => UpdateButtons();
            editor.Controls.Add(new Label { Text = "Field", AutoSize = true }, 0, 0);
            editor.Controls.Add(new Label { Text = "New value (for every test case)", AutoSize = true }, 1, 0);
            editor.Controls.Add(new Label { Text = "Changes to make", AutoSize = true }, 3, 0);
            editor.Controls.Add(cboField, 0, 1);
            editor.Controls.Add(cboValue, 1, 1);
            editor.Controls.Add(btnAdd, 2, 1);
            editor.Controls.Add(lstChanges, 3, 1);
            editor.SetRowSpan(lstChanges, 2);
            editor.Controls.Add(btnRemove, 2, 2);
            t.Controls.Add(editor, 1, 3);
            t.SetColumnSpan(editor, 2);

            // 3. find and check
            t.Controls.Add(new Label { Text = "3.  Check", AutoSize = true, Margin = new Padding(3, 9, 3, 3) }, 0, 4);
            btnFind = Btn("Find test cases", true);
            btnFind.Click += (s, e) => Find();
            cboShow = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
            cboShow.Items.AddRange(new object[] { "All rows", "Changes only", "Problems only" });
            cboShow.SelectedIndex = 0;
            cboShow.SelectedIndexChanged += (s, e) => Fill();
            btnTickAll = Btn("Tick all");
            btnUntickAll = Btn("Untick all");
            btnTickAll.Click += (s, e) => { foreach (var c in cases) c.Ticked = true; Fill(); };
            btnUntickAll.Click += (s, e) => { foreach (var c in cases) c.Ticked = false; Fill(); };
            var findRow = new FlowLayoutPanel { AutoSize = true };
            findRow.Controls.AddRange(new Control[] { btnFind, new Label { Text = "Show:", AutoSize = true, Margin = new Padding(16, 8, 0, 3) }, cboShow, btnTickAll, btnUntickAll });
            t.Controls.Add(findRow, 1, 4);
            t.SetColumnSpan(findRow, 2);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, CheckBoxes = true };
            lv.Columns.Add("Defect", 80);
            lv.Columns.Add("Defect summary", 190);
            lv.Columns.Add("Defect status", 90);
            lv.Columns.Add("Test set", 210);
            lv.Columns.Add("Test case", 170);
            lv.Columns.Add("Changes", 330);
            lv.Columns.Add("Result", 200);
            lv.ItemChecked += (s, e) =>
            {
                if (filling) return;
                var c = e.Item.Tag as DefectCase;
                if (c == null || c.Error != null || c.New.Count == 0) { filling = true; e.Item.Checked = false; filling = false; return; }
                c.Ticked = e.Item.Checked;
                Summary();
            };
            t.Controls.Add(lv, 0, 5);
            t.SetColumnSpan(lv, 3);
            lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3), Text = "Loading the fields of the test cases..." };
            t.Controls.Add(lblSummary, 0, 6);
            t.SetColumnSpan(lblSummary, 3);

            var row = new FlowLayoutPanel { AutoSize = true };
            btnApply = Btn("Apply changes to ALM", true);
            btnStop = Btn("Stop");
            btnStop.Enabled = false;
            var close = Btn("Close");
            close.DialogResult = DialogResult.Cancel;
            btnApply.Click += (s, e) => DoApply();
            btnStop.Click += (s, e) => stop = true;
            row.Controls.AddRange(new Control[] { btnApply, btnStop, close });
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
            Controls.Add(t);
            CancelButton = close;
            UpdateButtons();
            Shown += (s, e) => LoadFields();
        }

        // ------------------------------------------------------------ fields
        void LoadFields()
        {
            List<AlmClient.FieldDef> all = null;
            Run(() => all = client.GetFieldDefs("test-instance"), () =>
            {
                if (all == null) { lblSummary.Text = "Could not read the fields of the test cases."; return; }
                defs = new Dictionary<string, AlmClient.FieldDef>();
                foreach (var f in all.Where(f => f.Active && !defs.ContainsKey(f.Name))) defs[f.Name] = f;
                if (!defs.ContainsKey("status"))
                    defs["status"] = new AlmClient.FieldDef { Name = "status", Label = "Status", ListId = "", Type = "LookupList", Editable = true, Active = true };
                var usable = defs.Values.Where(f => f.Editable && !SetFieldDialog.Hidden.Contains(f.Name))
                                 .OrderBy(f => f.Name != "status").ThenBy(f => f.System).ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToList();
                cboField.Items.Clear();
                foreach (var f in usable) cboField.Items.Add(f);
                if (usable.Count > 0) cboField.SelectedIndex = 0;
                lblSummary.Text = "Enter the defect IDs and the changes, then press Find test cases.";
                UpdateButtons();
            });
        }

        IEnumerable<AlmClient.FieldDef> UsableFields() { return cboField.Items.Cast<AlmClient.FieldDef>(); }

        void OnFieldChanged()
        {
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            cboValue.Items.Clear();
            cboValue.Text = "";
            if (f == null) return;
            cboValue.DropDownStyle = f.ListId != "" ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
            if (f.ListId == "") { UpdateButtons(); return; }
            int seq = ++listSeq;
            List<string> cached;
            if (listValues.TryGetValue(f.ListId, out cached)) { FillValues(cached); return; }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<string> vals;
                try { vals = client.GetListValues(f.ListId); }
                catch (Exception e) { Log("Could not read the " + f.Label + " list: " + e.Message); vals = new List<string>(); }
                if (f.Name == "status" && vals.Count == 0) vals = ResultFields.DefaultStatuses.ToList();
                Ui(() =>
                {
                    lock (listValues) listValues[f.ListId] = vals;
                    if (seq == listSeq) FillValues(vals);
                    Replan();
                });
            });
        }

        void FillValues(List<string> vals)
        {
            cboValue.Items.Clear();
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            if (f != null && f.Name != "status") cboValue.Items.Add("(empty)");
            foreach (var v in vals) cboValue.Items.Add(v);
            UpdateButtons();
        }

        static string Disp(string v) { return v == "" ? "(empty)" : v; }

        void AddChange()
        {
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            if (f == null || Busy) return;
            if (f.ListId != "" && cboValue.SelectedItem == null) return;
            var value = cboValue.Text == "(empty)" ? "" : cboValue.Text;
            forAll.RemoveAll(kv => kv.Key.Name == f.Name);
            forAll.Add(new KeyValuePair<AlmClient.FieldDef, string>(f, value));
            ShowChanges();
            Replan();
        }

        void RemoveChange()
        {
            if (Busy || lstChanges.SelectedIndex < 0) return;
            forAll.RemoveAt(lstChanges.SelectedIndex);
            ShowChanges();
            Replan();
        }

        void ShowChanges()
        {
            lstChanges.Items.Clear();
            foreach (var kv in forAll) lstChanges.Items.Add(kv.Key.Label + "  =  " + Disp(kv.Value));
            UpdateButtons();
        }

        // ------------------------------------------------------------ list
        public void ImportList(string path)
        {
            if (Busy) return;
            DefectListFile file;
            try { file = DefectLinks.ReadList(path); }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is System.Xml.XmlException)) throw;
                Log("Could not read " + Path.GetFileName(path) + ": " + e.Message);
                return;
            }
            listFile = file;
            var have = DefectLinks.ParseIds(txtIds.Text);
            var added = file.Ids.Where(i => !have.Contains(i)).ToList();
            txtIds.Text = string.Join(Environment.NewLine, have.Concat(added));
            var used = file.Columns.Where(c => FieldFor(c) != null).ToList();
            var ignored = file.Columns.Except(used).ToList();
            Log(string.Format("Read {0} defect ID(s) from {1}{2}{3}", file.Ids.Count, Path.GetFileName(path),
                              used.Count > 0 ? ";  values per defect from the column(s): " + string.Join(", ", used) : "",
                              ignored.Count > 0 ? ";  ignored column(s): " + string.Join(", ", ignored) : ""));
            lblList.Text = string.Format("List: {0}  -  {1} defect ID(s){2}", Path.GetFileName(path), file.Ids.Count,
                                         used.Count > 0 ? ",  own values from: " + string.Join(", ", used.Select(c => FieldFor(c).Label)) : "");
            ClearResults();
        }

        AlmClient.FieldDef FieldFor(string column)
        {
            var n = Util.Norm(column);
            return UsableFields().FirstOrDefault(f => Util.Norm(f.Label) == n) ?? UsableFields().FirstOrDefault(f => Util.Norm(f.Name) == n);
        }

        // ------------------------------------------------------------ find
        void ClearResults()
        {
            cases = new List<DefectCase>();
            defects = new List<DefectInfo>();
            lv.Items.Clear();
            UpdateButtons();
        }

        protected void Find()
        {
            if (Busy || defs == null) return;
            var ids = DefectLinks.ParseIds(txtIds.Text);
            if (ids.Count == 0) return;
            var wanted = ids.Select(id => new DefectInfo { Id = id }).ToList();
            var listFields = new List<AlmClient.FieldDef>();
            if (listFile != null)
                foreach (var d in wanted)
                {
                    Dictionary<string, string> vals;
                    if (!listFile.Values.TryGetValue(d.Id, out vals)) continue;
                    foreach (var kv in vals)
                    {
                        var f = FieldFor(kv.Key);
                        if (f == null) continue;
                        d.ListValues[f.Name] = kv.Value;
                        if (!listFields.Contains(f)) listFields.Add(f);
                    }
                }
            var fields = UsableFields().Select(f => f.Name).ToList();
            var lists = forAll.Select(kv => kv.Key).Concat(listFields).Where(f => f.ListId != "").Select(f => f.ListId).Distinct().ToList();
            List<DefectCase> found = null;
            lblSummary.Text = string.Format("Reading {0} defect(s) and their links from ALM...", ids.Count);
            btnFind.Enabled = btnApply.Enabled = false;
            Run(() =>
            {
                foreach (var id in lists)
                {
                    bool have;
                    lock (listValues) have = listValues.ContainsKey(id);
                    if (have) continue;
                    var vals = client.GetListValues(id);
                    lock (listValues) listValues[id] = vals;
                }
                found = DefectLinks.Find(client, wanted, fields, lab, Log);
                Log(string.Format("{0} defect(s): {1} test case(s) linked, {2} defect(s) with a problem.",
                                  wanted.Count, found.Count, wanted.Count(d => d.Problem != null)));
            }, () =>
            {
                if (found != null)
                {
                    defects = wanted;
                    cases = found;
                    Replan();
                }
                else lblSummary.Text = "Could not read the defects.";
                UpdateButtons();
            });
        }

        void Replan()
        {
            if (defs == null) return;
            Dictionary<string, List<string>> lists;
            lock (listValues) lists = new Dictionary<string, List<string>>(listValues);
            DefectLinks.Plan(cases, forAll, defs, lists);
            Fill();
        }

        string ChangeText(DefectCase c)
        {
            return string.Join(";   ", c.New.Select(kv => defs[kv.Key].Label + ":  " + Disp(c.Now(kv.Key)) + "  ->  " + Disp(kv.Value)));
        }

        bool Wanted(DefectCase c) { return c.Ticked && c.Error == null && c.New.Count > 0; }

        protected void Fill()
        {
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            int show = cboShow.SelectedIndex;
            foreach (var d in defects.Where(d => d.Problem != null))
            {
                if (show == 1) continue;
                var it = new ListViewItem(new[] { d.Id, d.Summary, d.Status, "", "", "", d.Problem }) { Tag = d, ForeColor = Color.Firebrick };
                lv.Items.Add(it);
            }
            foreach (var c in cases)
            {
                bool problem = c.Error != null || c.Result.StartsWith("FAILED");
                bool updated = c.Result.StartsWith("updated");
                if (show == 1 && c.New.Count == 0 && !updated) continue;
                if (show == 2 && !problem) continue;
                var d0 = c.Defects[0];
                var it = new ListViewItem(new[]
                {
                    c.DefectIds, d0.Summary + (c.Defects.Count > 1 ? "  (+" + (c.Defects.Count - 1) + ")" : ""), d0.Status,
                    c.SetPath, c.Inst.Label,
                    c.New.Count > 0 ? ChangeText(c) : "",
                    c.Error ?? (c.Result.StartsWith("FAILED") || (c.Result != "" && c.New.Count == 0) ? c.Result
                                : c.New.Count > 0 ? (c.Ticked ? "will change" : "not ticked") : "no change"),
                }) { Tag = c, Checked = Wanted(c) };
                it.ForeColor = problem ? Color.Firebrick : updated && c.New.Count == 0 ? Color.ForestGreen
                             : c.New.Count > 0 && c.Ticked ? SystemColors.WindowText : SystemColors.GrayText;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            filling = false;
            Summary();
        }

        void Summary()
        {
            if (defects.Count == 0) { UpdateButtons(); return; }
            int change = cases.Count(Wanted), same = cases.Count(c => c.Error == null && c.New.Count == 0),
                unticked = cases.Count(c => c.Error == null && c.New.Count > 0 && !c.Ticked),
                bad = cases.Count(c => c.Error != null) + defects.Count(d => d.Problem != null);
            lblSummary.Text = string.Format("{0} defect(s), {1} linked test case(s):   {2} to change,   {3} already the same,   {4} not ticked,   {5} problem(s) (red).",
                                            defects.Count, cases.Count, change, same, unticked, bad)
                              + (forAll.Count == 0 && !defects.Any(d => d.ListValues.Count > 0) ? "   Add a change in step 2." : "");
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool busy = Busy, ready = defs != null;
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            btnFind.Enabled = !busy && ready && DefectLinks.ParseIds(txtIds.Text).Count > 0;
            btnAdd.Enabled = !busy && f != null && (f.ListId == "" || cboValue.SelectedItem != null);
            btnRemove.Enabled = !busy && lstChanges.SelectedIndex >= 0;
            btnImportList.Enabled = !busy;
            btnApply.Enabled = !busy && cases.Any(Wanted);
            btnTickAll.Enabled = btnUntickAll.Enabled = !busy && cases.Count > 0;
        }

        // ------------------------------------------------------------ apply
        protected void DoApply()
        {
            if (Busy || defs == null) return;
            var todo = cases.Where(Wanted).ToList();
            if (todo.Count == 0) return;
            int n = todo.Sum(c => c.New.Count);
            var sb = new StringBuilder();
            sb.AppendFormat("Write {0} change(s) to {1} test case(s) in ALM?", n, todo.Count).AppendLine().AppendLine();
            foreach (var c in todo.Take(15))
                sb.AppendLine("Defect " + c.DefectIds + "  |  " + c.SetPath + "  >  " + c.Inst.Label + ":   " + ChangeText(c));
            if (todo.Count > 15) sb.AppendLine("... and " + (todo.Count - 15) + " more (see the list).");
            if (todo.Any(c => c.New.ContainsKey("status")))
                sb.AppendLine().Append("If ALM does not allow changing the status directly, a run with that status is added instead.");
            if (!Confirm(sb.ToString())) return;
            btnApply.Enabled = btnFind.Enabled = false;
            btnStop.Enabled = true;
            int ok = 0, failed = 0;
            Run(() =>
            {
                foreach (var c in todo)
                {
                    if (stop) { Log("Stopped."); break; }
                    var before = new Dictionary<string, string>(c.New);
                    try
                    {
                        c.Result = DefectLinks.Apply(client, c, defs);
                        ok++;
                        Log("[ OK ] defect " + c.DefectIds + "  " + c.SetPath + " > " + c.Inst.Label + ": " + c.Result);
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException)) throw;
                        c.Result = "FAILED: " + e.Message;
                        failed++;
                        Log("[FAIL] defect " + c.DefectIds + "  " + c.SetPath + " > " + c.Inst.Label + ": " + e.Message);
                    }
                    // what is in ALM now (also the fields written before a failure)
                    var inLab = lab == null ? null : lab.FindInstance(c.Inst.Id);
                    if (inLab != null)
                        lock (Changes)
                            foreach (var kv in before.Where(kv => c.Now(kv.Key) == kv.Value))
                                Changes.Add(new FieldChange { Instance = inLab, Field = kv.Key, Value = kv.Value });
                    int done = ok + failed;
                    Ui(() => lblSummary.Text = string.Format("Writing to ALM...  {0} / {1}", done, todo.Count));
                }
                Log(string.Format("Done: {0} test case(s) updated, {1} failed.", ok, failed));
            }, () =>
            {
                btnStop.Enabled = false;
                Replan();
                foreach (var c in todo.Where(c => c.Result.StartsWith("FAILED"))) c.Ticked = true;
                Fill();
            });
        }
    }
}
