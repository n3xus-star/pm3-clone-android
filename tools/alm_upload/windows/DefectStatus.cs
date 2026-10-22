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
        /// The defect's Comments now (HTML), what was done to it, and files for it alone
        /// (from a "<defect folder>\<defect id>\" sub folder).
        public string CommentNow = "", Result = "";
        /// Assigned To now, and the value from the imported list (null: none).
        public string OwnerNow = "", ListOwner;
        public readonly List<string> FolderFiles = new List<string>();
        public readonly HashSet<string> UploadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// Attachment names on the defect in ALM (null until read).
        public HashSet<string> AttachNames;
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
        /// The test case's own attachments in ALM (null until read), to copy to its defects.
        public List<AlmClient.Attachment> Atts;
        public readonly HashSet<string> UploadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    /// A link to one attachment of a test case, for a defect's Comments.
    public class LinkItem
    {
        public string Url, Name, CaseText, CaseId;
        public AlmClient.Attachment Att;
        public bool IsImage { get { return Util.ImageExts.Contains(Path.GetExtension(Name ?? "").ToLowerInvariant()); } }
        /// Shown as a picture (pictures on and an image); else a link.
        public bool Picture, Embed;
        public int Width, Height;   // 0: unknown
        /// The small picture itself ("data:image/jpeg;base64,..."), when embedded in the comment.
        public string DataUri;
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
                                            TestLabFolder lab, Action<string> log, string commentField = null, string ownerField = null)
        {
            var byId = defects.ToDictionary(d => d.Id);
            var defectFields = new[] { "id", "name", "status", commentField, ownerField }.Where(f => f != null).Distinct().ToArray();
            foreach (var b in client.GetByIds("defects", "id", byId.Keys, defectFields))
            {
                DefectInfo d;
                if (!byId.TryGetValue(Get(b, "id"), out d)) continue;
                d.Found = true;
                d.Summary = Get(b, "name");
                d.Status = Get(b, "status");
                if (commentField != null) d.CommentNow = Get(b, commentField);
                if (ownerField != null) d.OwnerNow = Get(b, ownerField);
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

        // ------------------------------------------------------------------ comments
        /// Adds a comment under the defect's Comments the way ALM does: a line, then
        /// "user, date:" and the text. What was there stays.
        public static string AppendComment(string existing, string user, string text, DateTime when)
        {
            return AppendCommentHtml(existing, user, TextHtml(text), when);
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s ?? ""); }

        /// Plain text as HTML: escaped, new lines as line breaks.
        public static string TextHtml(string text)
        {
            return Esc((text ?? "").Trim()).Replace("\r\n", "\n").Replace("\n", "<br />");
        }

        public static string AppendCommentHtml(string existing, string user, string body, DateTime when)
        {
            const string Font = "<font face=\"Arial\"{0}><span style=\"font-size:8pt\">{1}</span></font>";
            var entry = "<div align=\"left\">" + string.Format(Font, "", "<br />")
                      + string.Format(Font, " color=\"#000080\"", "<b>________________________________________</b>") + "</div>\n"
                      + "<div align=\"left\">" + string.Format(Font, " color=\"#000080\"",
                            "<b>" + System.Security.SecurityElement.Escape(user ?? "") + ", " + when.ToString("dd/MM/yyyy") + ":</b>")
                      + string.Format(Font, "", " " + body) + "</div>\n";
            existing = existing ?? "";
            if (existing.Trim() == "") return "<html><body>\n" + entry + "</body></html>";
            int i = existing.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return existing.Substring(0, i) + entry + existing.Substring(i);
            var old = System.Security.SecurityElement.Escape(existing).Replace("\r\n", "\n").Replace("\n", "<br />");
            return "<html><body>\n<div align=\"left\">" + old + "</div>\n" + entry + "</body></html>";
        }

        /// Text of an ALM memo without its HTML, with single spaces.
        public static string PlainText(string html)
        {
            var t = Regex.Replace(html ?? "", @"<[^>]+>", " ");
            return Regex.Replace(WebUtility.HtmlDecode(t), @"\s+", " ").Trim();
        }

        /// The comment is already in the defect's Comments (e.g. added by an earlier Apply).
        public static bool HasComment(string existing, string text)
        {
            // the typed text is plain text: only its spaces are evened out
            var want = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return want != "" && PlainText(existing).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// The link is already in the defect's Comments.
        public static bool LinkThere(string existing, string url)
        {
            existing = existing ?? "";
            return existing.IndexOf(url, StringComparison.OrdinalIgnoreCase) >= 0
                   || existing.IndexOf(Esc(url), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// What to add to a defect's Comments: the text (unless it is there already) and links to the
        /// test cases' attachments that are not there yet, grouped by test case. "" when nothing is new.
        public static string CommentBody(string existing, string text, List<LinkItem> links)
        {
            var parts = new List<string>();
            text = (text ?? "").Trim();
            if (text != "" && !HasComment(existing, text)) parts.Add(TextHtml(text));
            var fresh = (links ?? new List<LinkItem>()).Where(l => !LinkThere(existing, l.Url)).ToList();
            if (fresh.Count > 0)
            {
                var sb = new StringBuilder("<b>Attachments of the test case(s):</b>");
                foreach (var g in fresh.GroupBy(l => l.CaseText))
                {
                    sb.Append("<br />" + Esc(g.Key) + ":");
                    foreach (var l in g)
                    {
                        // links ask ALM for its login form when the browser is not logged in yet
                        var href = Esc(LoginUrl(l.Url));
                        if (l.Picture && (!l.Embed || l.DataUri != null))
                        {
                            // a small picture (click: full size), its name under it
                            var size = l.Width > 0 && l.Height > 0 ? " width=\"" + l.Width + "\" height=\"" + l.Height + "\"" : " style=\"max-width:" + PictureWidth + "px; max-height:" + PictureWidth + "px\"";
                            var src = l.DataUri ?? LoginUrl(l.Url);
                            sb.Append("<br /><a href=\"" + href + "\"><img src=\"" + Esc(src) + "\" alt=\"" + Esc(l.Name) + "\" border=\"0\"" + size + " /></a>"
                                      + "<br />&nbsp;&nbsp;" + Esc(l.Name));
                        }
                        else sb.Append("<br />&nbsp;&nbsp;- <a href=\"" + href + "\">" + Esc(l.Name) + "</a>");
                    }
                }
                parts.Add(sb.ToString());
            }
            return string.Join("<br />", parts);
        }

        public const int PictureWidth = 400;
        /// Largest embedded picture: the ALM desktop client's Internet Explorer shows data pictures up to 32 KB.
        public const int EmbedBytes = 30000;

        /// The address with ALM's "show the login form when not logged in" switch.
        public static string LoginUrl(string url)
        {
            if (url.IndexOf("login-form-required=", StringComparison.OrdinalIgnoreCase) >= 0) return url;
            return url + (url.Contains("?") ? "&" : "?") + "login-form-required=y";
        }

        /// Small JPEG of a picture as a data address, at most maxBytes (lower quality, then smaller).
        public static string Thumbnail(Image img, int w, int h, int maxBytes)
        {
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            for (double scale = 1.0; scale > 0.2; scale *= 0.8)
            {
                int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
                using (var bmp = new Bitmap(tw, th))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);   // transparent PNG parts
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, tw, th);
                    }
                    foreach (long q in new long[] { 80, 65, 50 })
                        using (var ms = new MemoryStream())
                        using (var ps = new System.Drawing.Imaging.EncoderParameters(1))
                        {
                            ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                            bmp.Save(ms, codec, ps);
                            if (ms.Length <= maxBytes) return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                        }
                }
            }
            return null;
        }

        /// Reads the pictures: their size (at most PictureWidth wide and high, same proportions) and,
        /// when embedded, a small JPEG of each.
        public static void SizePictures(AlmClient client, IEnumerable<LinkItem> items)
        {
            foreach (var l in items.Where(l => l.Picture && (l.Width == 0 || (l.Embed && l.DataUri == null))))
            {
                try
                {
                    using (var ms = new MemoryStream(client.DownloadAttachment("test-instances", l.CaseId, l.Att)))
                    using (var img = Image.FromStream(ms, false, false))
                    {
                        // fits in a PictureWidth x PictureWidth box, same proportions
                        int w = img.Width, h = img.Height;
                        double k = Math.Min(1.0, Math.Min((double)PictureWidth / w, (double)PictureWidth / h));
                        w = (int)Math.Round(w * k);
                        h = (int)Math.Round(h * k);
                        l.Width = Math.Max(w, 1);
                        l.Height = Math.Max(h, 1);
                        if (l.Embed) l.DataUri = Thumbnail(img, l.Width, l.Height, EmbedBytes);
                    }
                }
                catch (Exception e)
                {
                    // not readable as a picture here (e.g. webp): the browser sizes it
                    if (!(e is AlmException || e is WebException || e is ArgumentException || e is OutOfMemoryException || e is IOException)) throw;
                }
            }
        }

        /// Uploads files to one entity; names that are already there are skipped.
        public static string UploadFiles(AlmClient client, string entity, string id, List<string> files, HashSet<string> done)
        {
            var there = client.ListAttachmentNames(entity, id);
            int up = 0, skipped = 0;
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                if (there.Contains(name)) { skipped++; done.Add(f); continue; }
                client.UploadAttachment(entity, id, f, name);
                there.Add(name);
                done.Add(f);
                up++;
            }
            return up + " file(s) uploaded" + (skipped > 0 ? ", " + skipped + " already there" : "");
        }

        /// Files of a defect folder: "<folder>\<defect id>\..." (the sub folder name may also be e.g. "Defect 3798").
        public static Dictionary<string, List<string>> ReadDefectFolder(string folder)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var dir in Directory.GetDirectories(folder))
            {
                var ids = ParseIds(Path.GetFileName(dir));
                if (ids.Count != 1) continue;
                List<string> l;
                if (!map.TryGetValue(ids[0], out l)) map[ids[0]] = l = new List<string>();
                l.AddRange(Util.ListFiles(dir));
            }
            return map;
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
        protected readonly Button btnFind, btnApply, btnStop, btnAdd, btnRemove, btnImportList, btnTickAll, btnUntickAll, btnDeleteComments;
        protected readonly TextBox txtComment;
        protected readonly ListBox lstFiles;
        protected readonly CheckBox chkFilesCases, chkFilesDefects, chkCopyFromCases, chkCopyImagesOnly, chkLinks;
        protected readonly ComboBox cboPictures;
        public const string PicturesEmbedded = "embedded pictures", PicturesFromAlm = "pictures from ALM", PicturesAsLinks = "links only";
        protected readonly Button btnAddFiles, btnFolder, btnRemoveFile;
        readonly Label lblSummary, lblList, lblFolder;
        bool filling, hadComment;
        int listSeq;
        string commentField = "dev-comments", commentLabel = "Comments", ownerField = "owner", ownerLabel = "Assigned To";
        /// The project's users (login name, full name); null when they could not be read.
        List<KeyValuePair<string, string>> users;
        protected readonly ComboBox cboOwner;
        readonly Label lblOwner;
        const string NoChange = "(no change)";

        /// Files for every ticked test case / defect, and the folder with a sub folder per defect id.
        protected readonly List<string> allFiles = new List<string>();
        protected string defectFolder;
        Dictionary<string, List<string>> folderFiles = new Dictionary<string, List<string>>();

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
            ClientSize = new Size(1060, 850);
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
            lblOwner = new Label { Text = "Defect \"Assigned To\":", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
            cboOwner = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 300, AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                                      AutoCompleteSource = AutoCompleteSource.ListItems };
            cboOwner.Items.Add(NoChange);
            cboOwner.SelectedIndex = 0;
            cboOwner.TextChanged += (s, e) => Fill();
            var ownerRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            ownerRow.Controls.AddRange(new Control[] { lblOwner, cboOwner });
            editor.Controls.Add(ownerRow, 0, 2);
            editor.SetColumnSpan(ownerRow, 2);
            t.Controls.Add(editor, 1, 3);
            t.SetColumnSpan(editor, 2);

            // 3. comment and attachments
            t.Controls.Add(new Label { Text = "3.  Comment &&\n     attachments", AutoSize = true, Margin = new Padding(3, 9, 3, 3) }, 0, 4);
            var extra = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            extra.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            extra.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            extra.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            extra.Controls.Add(new Label { Text = "Comment for the defects (optional, added with your name and date)", AutoSize = true }, 0, 0);
            txtComment = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 70, AcceptsReturn = true };
            txtComment.TextChanged += (s, e) =>
            {
                bool has = txtComment.Text.Trim() != "";
                if (has != hadComment) { hadComment = has; Fill(); }
            };
            lstFiles = new ListBox { Dock = DockStyle.Fill, Height = 70, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true };
            lstFiles.SelectedIndexChanged += (s, e) => UpdateButtons();
            btnAddFiles = Btn("Add files...");
            btnFolder = Btn("Defect folder...");
            btnRemoveFile = Btn("Remove");
            btnAddFiles.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Title = "Files for every ticked test case / defect" })
                    if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
            };
            btnFolder.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Folder with one sub folder per defect ID (e.g. ...\\3798\\), whose files go to that defect only" })
                    if (d.ShowDialog(this) == DialogResult.OK) SetDefectFolder(d.SelectedPath);
            };
            btnRemoveFile.Click += (s, e) => RemoveFiles();
            var fileButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            fileButtons.Controls.AddRange(new Control[] { btnAddFiles, btnFolder, btnRemoveFile });
            extra.Controls.Add(txtComment, 0, 1);
            extra.Controls.Add(lstFiles, 1, 1);
            extra.Controls.Add(fileButtons, 2, 1);
            // "Attachments (optional), upload to: [x] test cases [x] defects" above the file list
            chkFilesCases = new CheckBox { Text = "test cases", AutoSize = true, Checked = true, Margin = new Padding(6, 0, 0, 0) };
            chkFilesDefects = new CheckBox { Text = "defects", AutoSize = true, Checked = true, Margin = new Padding(6, 0, 0, 0) };
            chkFilesCases.CheckedChanged += (s, e) => Fill();
            chkFilesDefects.CheckedChanged += (s, e) => Fill();
            lblFolder = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(10, 2, 0, 0) };
            var fileHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            fileHead.Controls.AddRange(new Control[] { new Label { Text = "Attachments (optional) - upload to:", AutoSize = true, Margin = new Padding(3, 2, 0, 0) },
                                                       chkFilesCases, chkFilesDefects });
            extra.Controls.Add(fileHead, 1, 0);
            extra.SetColumnSpan(fileHead, 2);
            chkCopyFromCases = new CheckBox { Text = "Copy the test cases' own attachments to their defects", AutoSize = true, Margin = new Padding(3, 4, 0, 0) };
            chkCopyImagesOnly = new CheckBox { Text = "images only", AutoSize = true, Margin = new Padding(10, 4, 0, 0), Enabled = false };
            chkCopyFromCases.CheckedChanged += (s, e) =>
            {
                chkCopyImagesOnly.Enabled = chkCopyFromCases.Checked;
                if (chkCopyFromCases.Checked && cases.Any(c => c.Atts == null)) LoadCaseAttachments(); else Fill();
            };
            chkCopyImagesOnly.CheckedChanged += (s, e) => Fill();
            var copyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            copyRow.Controls.AddRange(new Control[] { chkCopyFromCases, chkCopyImagesOnly });
            extra.Controls.Add(copyRow, 1, 2);
            chkLinks = new CheckBox { Text = "Test case attachments in comment:", AutoSize = true, Margin = new Padding(3, 4, 0, 0) };
            // pictures in the comment: work without an ALM login; read from ALM: need one; links: names only
            cboPictures = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 142, Margin = new Padding(4, 1, 0, 0), Enabled = false };
            cboPictures.Items.AddRange(new object[] { PicturesEmbedded, PicturesFromAlm, PicturesAsLinks });
            cboPictures.SelectedIndex = 0;
            chkLinks.CheckedChanged += (s, e) =>
            {
                cboPictures.Enabled = chkLinks.Checked;
                probeLinks = chkLinks.Checked;
                if (chkLinks.Checked && (cases.Any(c => c.Atts == null) || client.AttachmentLinkFormat == null)) LoadCaseAttachments(); else Fill();
            };
            cboPictures.SelectedIndexChanged += (s, e) => Fill();
            var linkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            linkRow.Controls.AddRange(new Control[] { chkLinks, cboPictures });
            extra.Controls.Add(linkRow, 0, 2);
            extra.SetColumnSpan(copyRow, 2);
            extra.RowCount = 3;
            extra.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            extra.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            extra.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(extra, 1, 4);
            t.SetColumnSpan(extra, 2);

            // 4. find and check
            t.Controls.Add(new Label { Text = "4.  Check", AutoSize = true, Margin = new Padding(3, 9, 3, 3) }, 0, 5);
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
            btnDeleteComments = Btn("Delete defect comments...");
            btnDeleteComments.Click += (s, e) => OpenDeleteComments();
            findRow.Controls.AddRange(new Control[] { btnFind, new Label { Text = "Show:", AutoSize = true, Margin = new Padding(16, 8, 0, 3) }, cboShow, btnTickAll, btnUntickAll,
                                                      new Label { AutoSize = true, Width = 24, Text = "" }, btnDeleteComments });
            t.Controls.Add(findRow, 1, 5);
            t.SetColumnSpan(findRow, 2);

            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, CheckBoxes = true };
            lv.Columns.Add("Defect", 80);
            lv.Columns.Add("Defect summary", 190);
            lv.Columns.Add("Defect status", 90);
            lv.Columns.Add("Test set", 210);
            lv.Columns.Add("Test case", 170);
            lv.Columns.Add("Changes", 300);
            lv.Columns.Add("Defect update", 170);
            lv.Columns.Add("Result", 220);
            lv.ItemChecked += (s, e) =>
            {
                if (filling) return;
                var c = e.Item.Tag as DefectCase;
                if (c == null || c.Error != null) { filling = true; e.Item.Checked = false; filling = false; return; }
                c.Ticked = e.Item.Checked;
                // the defect columns of the other rows depend on what is ticked
                BeginInvoke(new Action(Fill));
            };
            t.Controls.Add(lv, 0, 6);
            t.SetColumnSpan(lv, 3);
            lblSummary = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3), Text = "Loading the fields of the test cases..." };
            t.Controls.Add(lblSummary, 0, 7);
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
            t.Controls.Add(row, 0, 8);
            t.SetColumnSpan(row, 3);
            txtLog = LogBox();
            t.Controls.Add(txtLog, 0, 9);
            t.SetColumnSpan(txtLog, 3);
            t.RowCount = 10;
            for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles[4] = new RowStyle(SizeType.Absolute, 168);   // comment / attachments: room for the three buttons
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
            Run(() =>
            {
                all = client.GetFieldDefs("test-instance");
                try
                {
                    var dd = client.GetFieldDefs("defect").Where(f => f.Active).ToList();
                    var cf = dd.FirstOrDefault(f => f.Name == "dev-comments")
                             ?? dd.FirstOrDefault(f => f.Label.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0
                                                       && f.Type.IndexOf("memo", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (cf != null) { commentField = cf.Name; commentLabel = cf.Label; }
                    var of = dd.FirstOrDefault(f => f.Name == "owner")
                             ?? dd.FirstOrDefault(f => f.Label.IndexOf("assigned", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (of != null) { ownerField = of.Name; ownerLabel = of.Label; }
                }
                catch (AlmException) { }   // keep "dev-comments" / "owner"
                try { users = client.GetUsers(); }
                catch (Exception e)
                {
                    if (!(e is AlmException || e is System.Xml.XmlException)) throw;
                    users = null;
                    Log("Could not read the users of the project (" + e.Message + "); type the Assigned To login name.");
                }
            }, () =>
            {
                if (all == null) { lblSummary.Text = "Could not read the fields of the test cases."; return; }
                lblOwner.Text = "Defect \"" + ownerLabel + "\":";
                if (users != null)
                    foreach (var u in users) cboOwner.Items.Add(UserText(u));
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
            var used = file.Columns.Where(c => IsOwnerColumn(c) || FieldFor(c) != null).ToList();
            var ignored = file.Columns.Except(used).ToList();
            Log(string.Format("Read {0} defect ID(s) from {1}{2}{3}", file.Ids.Count, Path.GetFileName(path),
                              used.Count > 0 ? ";  values per defect from the column(s): " + string.Join(", ", used) : "",
                              ignored.Count > 0 ? ";  ignored column(s): " + string.Join(", ", ignored) : ""));
            lblList.Text = string.Format("List: {0}  -  {1} defect ID(s){2}", Path.GetFileName(path), file.Ids.Count,
                                         used.Count > 0 ? ",  own values from: " + string.Join(", ", used.Select(c => IsOwnerColumn(c) ? "defect " + ownerLabel : FieldFor(c).Label)) : "");
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
                        if (IsOwnerColumn(kv.Key)) { if (kv.Value != "") d.ListOwner = kv.Value; continue; }
                        var f = FieldFor(kv.Key);
                        if (f == null) continue;
                        d.ListValues[f.Name] = kv.Value;
                        if (!listFields.Contains(f)) listFields.Add(f);
                    }
                }
            var fields = UsableFields().Select(f => f.Name).ToList();
            var lists = forAll.Select(kv => kv.Key).Concat(listFields).Where(f => f.ListId != "").Select(f => f.ListId).Distinct().ToList();
            List<DefectCase> found = null;
            bool copyAtts = chkCopyFromCases.Checked || chkLinks.Checked;
            probeLinks = chkLinks.Checked;
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
                found = DefectLinks.Find(client, wanted, fields, lab, Log, commentField, ownerField);
                if (copyAtts) ReadAttachments(found, wanted);
                Log(string.Format("{0} defect(s): {1} test case(s) linked, {2} defect(s) with a problem.",
                                  wanted.Count, found.Count, wanted.Count(d => d.Problem != null)));
            }, () =>
            {
                if (found != null)
                {
                    defects = wanted;
                    cases = found;
                    ApplyFolder(true);
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

        // ------------------------------------------------------------ attachments
        void AddFiles(IEnumerable<string> paths)
        {
            if (Busy) return;
            foreach (var p in paths)
                if (!allFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) allFiles.Add(p);
            ShowFiles();
            Fill();
        }

        void RemoveFiles()
        {
            if (Busy) return;
            var sel = lstFiles.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
            foreach (var i in sel)
            {
                if (i < allFiles.Count) allFiles.RemoveAt(i);
                else defectFolder = null;   // the folder line
            }
            if (defectFolder == null) folderFiles = new Dictionary<string, List<string>>();
            ApplyFolder(false);
            ShowFiles();
            Fill();
        }

        public void SetDefectFolder(string folder)
        {
            if (Busy) return;
            try { folderFiles = DefectLinks.ReadDefectFolder(folder); }
            catch (Exception e)
            {
                if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                Log("Could not read " + folder + ": " + e.Message);
                return;
            }
            defectFolder = folder;
            Log(string.Format("Defect folder {0}: {1} defect sub folder(s) with {2} file(s){3}", folder, folderFiles.Count,
                              folderFiles.Values.Sum(l => l.Count),
                              folderFiles.Count == 0 ? " - name the sub folders after the defect IDs, e.g. 3798" : ""));
            ApplyFolder(true);
            ShowFiles();
            Fill();
        }

        /// Gives every defect the files of its own sub folder.
        void ApplyFolder(bool report)
        {
            foreach (var d in defects)
            {
                d.FolderFiles.Clear();
                List<string> l;
                if (folderFiles.TryGetValue(d.Id, out l)) d.FolderFiles.AddRange(l);
            }
            if (report && defects.Count > 0 && folderFiles.Count > 0)
            {
                var unused = folderFiles.Keys.Where(id => !defects.Any(d => d.Id == id)).ToList();
                if (unused.Count > 0) Log("Sub folders for defects that are not in the list (not used): " + string.Join(", ", unused.Take(20)));
            }
        }

        void ShowFiles()
        {
            lstFiles.Items.Clear();
            foreach (var f in allFiles) lstFiles.Items.Add(Path.GetFileName(f) + "      (" + Path.GetDirectoryName(f) + ")");
            if (defectFolder != null)
                lstFiles.Items.Add(string.Format("[defect folder]  {0}  -  {1} defect(s), {2} file(s)", defectFolder, folderFiles.Count, folderFiles.Values.Sum(l => l.Count)));
            lblFolder.Text = allFiles.Count == 0 && defectFolder == null ? ""
                : string.Format("{0} file(s) for all{1}", allFiles.Count, defectFolder != null ? " + a defect folder" : "");
            UpdateButtons();
        }

        /// Files still to upload: the same files for all plus the defect's own, one per file name.
        static List<string> Pending(IEnumerable<string> files, HashSet<string> done)
        {
            return files.Where(f => !done.Contains(f))
                        .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        List<string> CaseFiles(DefectCase c)
        {
            if (!chkFilesCases.Checked) return new List<string>();
            return Pending(allFiles.Concat(c.Defects.SelectMany(d => d.FolderFiles)), c.UploadedFiles);
        }

        List<string> DefectFiles(DefectInfo d)
        {
            if (!chkFilesDefects.Checked) return new List<string>();
            return Pending(allFiles.Concat(d.FolderFiles), d.UploadedFiles);
        }

        // ------------------------------------------------------------ copy from test cases
        /// Reads the attachments of the test cases and the attachment names of their defects.
        void ReadAttachments(List<DefectCase> list, List<DefectInfo> defectList)
        {
            foreach (var c in list.Where(c => c.Atts == null))
            {
                try { c.Atts = client.GetAttachments("test-instances", c.Inst.Id); }
                catch (AlmException e) { c.Atts = new List<AlmClient.Attachment>(); Log("Could not read the attachments of " + c.Inst.Label + ": " + e.Message); }
            }
            foreach (var d in defectList.Where(d => d.Found && d.AttachNames == null))
            {
                try { d.AttachNames = client.ListAttachmentNames("defects", d.Id); }
                catch (AlmException e) { Log("Could not read the attachments of defect " + d.Id + ": " + e.Message); }
            }
            Log(string.Format("Read the attachments of {0} test case(s): {1} file(s).", list.Count, list.Sum(c => c.Atts.Count)));
            if (probeLinks) ProbeLinks(list);
        }

        volatile bool probeLinks;

        /// Finds once which link format opens an attachment's file in this ALM.
        void ProbeLinks(List<DefectCase> list)
        {
            if (client.AttachmentLinkFormat != null) return;
            var c = list.FirstOrDefault(x => x.Atts != null && x.Atts.Count > 0);
            if (c == null) return;
            // a small file is quicker to try
            var att = c.Atts.OrderBy(a => a.Size < 0 ? long.MaxValue : a.Size).First();
            try { client.FindAttachmentLink("test-instances", c.Inst.Id, att, Log); }
            catch (Exception e)
            {
                if (!(e is AlmException || e is WebException)) throw;
                Log("Attachment links: could not try the link formats: " + e.Message);
            }
        }

        void LoadCaseAttachments()
        {
            if (Busy || cases.Count == 0) return;
            probeLinks = chkLinks.Checked;
            var list = cases;
            var defectList = defects;
            lblSummary.Text = "Reading the attachments of the test cases...";
            btnApply.Enabled = btnFind.Enabled = false;
            Run(() => ReadAttachments(list, defectList), () => Fill());
        }

        public class CopyItem
        {
            public DefectCase Case;
            public AlmClient.Attachment Att;
            public string Target;
        }

        /// Attachments of the ticked test cases of a defect that the defect does not have yet. When two
        /// test cases have a file with the same name, the test case name goes in front of it.
        protected List<CopyItem> CopyPlan(DefectInfo d)
        {
            var none = new List<CopyItem>();
            if (!chkCopyFromCases.Checked || d.AttachNames == null) return none;
            bool images = chkCopyImagesOnly.Checked;
            var src = cases.Where(c => c.Ticked && c.Error == null && c.Atts != null && c.Defects.Contains(d))
                           .SelectMany(c => c.Atts.Where(a => !images || Util.ImageExts.Contains(Path.GetExtension(a.Name).ToLowerInvariant()))
                                                  .Select(a => new CopyItem { Case = c, Att = a }))
                           .ToList();
            var plan = new List<CopyItem>();
            foreach (var g in src.GroupBy(x => x.Att.Name, StringComparer.OrdinalIgnoreCase))
            {
                var perCase = g.GroupBy(x => x.Case).Select(cg => cg.First()).ToList();
                foreach (var x in perCase)
                    x.Target = perCase.Count == 1 ? x.Att.Name : Util.SafeFolderName(x.Case.Inst.Label) + " - " + x.Att.Name;
                plan.AddRange(perCase);
            }
            // test cases with the same name (e.g. another department's copy): number them
            foreach (var g in plan.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            {
                int k = 1;
                foreach (var x in g.Skip(1))
                    x.Target = Path.GetFileNameWithoutExtension(x.Target) + " (" + ++k + ")" + Path.GetExtension(x.Target);
            }
            return plan.Where(x => !d.AttachNames.Contains(x.Target)).ToList();
        }

        // ------------------------------------------------------------ assigned to
        static string UserText(KeyValuePair<string, string> u) { return u.Value != "" && u.Value != u.Key ? u.Value + "  (" + u.Key + ")" : u.Key; }

        bool IsOwnerColumn(string column)
        {
            var n = Util.Norm(column);
            return n == Util.Norm(ownerLabel) || n == Util.Norm(ownerField) || n == "assigned to" || n == "defect assigned to";
        }

        /// The login name for what was typed or chosen: a login name, a full name or "Full Name (login)".
        /// Null when the project's users are known and it is none of them.
        string ResolveUser(string text)
        {
            text = (text ?? "").Trim();
            if (users == null) return text;
            foreach (var u in users)
                if (string.Equals(text, u.Key, StringComparison.OrdinalIgnoreCase) || string.Equals(text, u.Value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, UserText(u), StringComparison.OrdinalIgnoreCase))
                    return u.Key;
            return null;
        }

        string OwnerChoice { get { var t = cboOwner.Text.Trim(); return t == NoChange ? "" : t; } }

        /// What the defect's Assigned To becomes: the list's value for the defect, else the one chosen
        /// for all. "" = no change; Error set when the user is unknown.
        string OwnerTarget(DefectInfo d, out string error)
        {
            error = null;
            var raw = !string.IsNullOrEmpty(d.ListOwner) ? d.ListOwner : OwnerChoice;
            if (raw == "") return "";
            var name = ResolveUser(raw);
            if (name == null) { error = "'" + raw + "' is not a user of this project"; return ""; }
            return string.Equals(name, d.OwnerNow, StringComparison.OrdinalIgnoreCase) ? "" : name;
        }

        bool OwnerPending(DefectInfo d) { string e; return OwnerTarget(d, out e) != ""; }

        bool OwnerError(DefectInfo d) { string e; OwnerTarget(d, out e); return e != null; }

        string CommentText { get { return txtComment.Text.Trim(); } }

        /// Links to the attachments of the defect's ticked test cases.
        protected List<LinkItem> LinksFor(DefectInfo d)
        {
            if (!chkLinks.Checked) return new List<LinkItem>();
            return cases.Where(c => c.Ticked && c.Error == null && c.Atts != null && c.Defects.Contains(d))
                        .SelectMany(c => c.Atts.Select(a => new LinkItem
                        {
                            Url = client.AttachmentUrl("test-instances", c.Inst.Id, a), Name = a.Name, CaseId = c.Inst.Id, Att = a,
                            CaseText = c.SetPath + " > " + c.Inst.Label + " (test case " + c.Inst.Id + ")",
                        }))
                        .Select(l =>
                        {
                            var mode = (string)cboPictures.SelectedItem;
                            l.Picture = mode != PicturesAsLinks && l.IsImage;
                            l.Embed = mode == PicturesEmbedded;
                            return l;
                        }).ToList();
        }

        int NewLinks(DefectInfo d) { return LinksFor(d).Count(l => !DefectLinks.LinkThere(d.CommentNow, l.Url)); }

        int NewPictures(DefectInfo d) { return LinksFor(d).Count(l => l.Picture && !DefectLinks.LinkThere(d.CommentNow, l.Url)); }

        /// "3 picture(s) + 1 link(s)" for the new attachments of a defect's comment.
        string AttachText(DefectInfo d)
        {
            int all = NewLinks(d), pics = NewPictures(d);
            var parts = new List<string>();
            if (pics > 0) parts.Add(pics + " picture(s)");
            if (all - pics > 0) parts.Add((all - pics) + " link(s)");
            return string.Join(" + ", parts);
        }

        bool CommentPending(DefectInfo d) { return DefectLinks.CommentBody(d.CommentNow, CommentText, LinksFor(d)) != ""; }

        /// A defect is updated when at least one of its test cases is ticked.
        bool DefectActive(DefectInfo d)
        {
            return d.Found && d.Problem == null && cases.Any(c => c.Ticked && c.Error == null && c.Defects.Contains(d));
        }

        bool DefectWanted(DefectInfo d) { return DefectActive(d) && (OwnerPending(d) || CommentPending(d) || DefectFiles(d).Count > 0 || CopyPlan(d).Count > 0); }

        string DefectText(DefectInfo d)
        {
            var parts = new List<string>();
            string ownerError;
            var owner = OwnerTarget(d, out ownerError);
            if (ownerError != null) parts.Add("unknown user " + ownerError.Substring(0, ownerError.IndexOf(" is not")));
            else if (owner != "") parts.Add("assign: " + (d.OwnerNow == "" ? "(nobody)" : d.OwnerNow) + " -> " + owner);
            if (CommentPending(d))
            {
                var att = AttachText(d);
                bool text = CommentText != "" && !DefectLinks.HasComment(d.CommentNow, CommentText);
                parts.Add(text ? (att != "" ? "comment + " + att : "comment") : att + " in comment");
            }
            int n = DefectFiles(d).Count;
            if (n > 0) parts.Add(n + " file(s)");
            int k = CopyPlan(d).Count;
            if (k > 0) parts.Add(k + " from test cases");
            return parts.Count > 0 ? string.Join(" + ", parts) : d.Result;
        }

        // ------------------------------------------------------------ list
        string ChangeText(DefectCase c)
        {
            var parts = c.New.Select(kv => defs[kv.Key].Label + ":  " + Disp(c.Now(kv.Key)) + "  ->  " + Disp(kv.Value)).ToList();
            int n = CaseFiles(c).Count;
            if (n > 0) parts.Add("+ " + n + " file(s)");
            return string.Join(";   ", parts);
        }

        bool Wanted(DefectCase c) { return c.Ticked && c.Error == null && (c.New.Count > 0 || CaseFiles(c).Count > 0); }

        protected void Fill()
        {
            if (defs == null) return;
            filling = true;
            lv.BeginUpdate();
            lv.Items.Clear();
            int show = cboShow.SelectedIndex;
            foreach (var d in defects.Where(d => d.Problem != null))
            {
                if (show == 1) continue;
                var it = new ListViewItem(new[] { d.Id, d.Summary, d.Status, "", "", "", "", d.Problem }) { Tag = d, ForeColor = Color.Firebrick };
                lv.Items.Add(it);
            }
            foreach (var c in cases)
            {
                bool problem = c.Error != null || c.Result.Contains("FAILED");
                bool updated = c.Result != "" && !c.Result.StartsWith("FAILED");
                bool wanted = Wanted(c);
                var defectText = string.Join(";  ", c.Defects.Where(DefectActive).Select(d => new { d.Id, T = DefectText(d) })
                                                     .Where(x => x.T != "").Select(x => x.Id + ": " + x.T));
                if (show == 1 && !wanted && !updated && defectText == "") continue;
                if (show == 2 && !problem) continue;
                var d0 = c.Defects[0];
                var it = new ListViewItem(new[]
                {
                    c.DefectIds, d0.Summary + (c.Defects.Count > 1 ? "  (+" + (c.Defects.Count - 1) + ")" : ""), d0.Status,
                    c.SetPath, c.Inst.Label, ChangeText(c), defectText,
                    c.Error ?? (problem || (updated && !wanted) ? c.Result
                                : wanted ? "will change" : c.Ticked ? "no change" : "not ticked"),
                }) { Tag = c, Checked = c.Ticked && c.Error == null };
                it.ForeColor = problem || (c.Ticked && c.Defects.Any(x => DefectActive(x) && OwnerError(x))) ? Color.Firebrick : updated && !wanted ? Color.ForestGreen
                             : wanted || (c.Ticked && c.Defects.Any(DefectWanted)) ? SystemColors.WindowText : SystemColors.GrayText;
                lv.Items.Add(it);
            }
            lv.EndUpdate();
            filling = false;
            Summary();
        }

        void Summary()
        {
            if (defects.Count == 0) { UpdateButtons(); return; }
            int change = cases.Count(Wanted), same = cases.Count(c => c.Ticked && c.Error == null && !Wanted(c)),
                unticked = cases.Count(c => c.Error == null && !c.Ticked),
                bad = cases.Count(c => c.Error != null) + defects.Count(d => d.Problem != null) + defects.Count(d => DefectActive(d) && OwnerError(d)),
                defectUpdates = defects.Count(DefectWanted);
            lblSummary.Text = string.Format("{0} defect(s), {1} linked test case(s):   {2} to change,   {6} defect(s) to update,   {3} with nothing to do,   {4} not ticked,   {5} problem(s) (red).",
                                            defects.Count, cases.Count, change, same, unticked, bad, defectUpdates)
                              + (forAll.Count == 0 && !defects.Any(d => d.ListValues.Count > 0) && CommentText == "" && allFiles.Count == 0 && defectFolder == null && !chkCopyFromCases.Checked && !chkLinks.Checked && OwnerChoice == "" && !defects.Any(d => !string.IsNullOrEmpty(d.ListOwner))
                                 ? "   Add a change in step 2 or a comment / files in step 3." : "");
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool busy = Busy, ready = defs != null;
            var f = cboField.SelectedItem as AlmClient.FieldDef;
            btnFind.Enabled = !busy && ready && DefectLinks.ParseIds(txtIds.Text).Count > 0;
            btnAdd.Enabled = !busy && f != null && (f.ListId == "" || cboValue.SelectedItem != null);
            btnRemove.Enabled = !busy && lstChanges.SelectedIndex >= 0;
            btnImportList.Enabled = btnAddFiles.Enabled = btnFolder.Enabled = !busy;
            btnRemoveFile.Enabled = !busy && lstFiles.SelectedIndices.Count > 0;
            btnApply.Enabled = !busy && ready && (cases.Any(Wanted) || defects.Any(DefectWanted));
            btnTickAll.Enabled = btnUntickAll.Enabled = !busy && cases.Count > 0;
            btnDeleteComments.Enabled = !busy && ready && DefectLinks.ParseIds(txtIds.Text).Count > 0;
        }

        /// Deletes chosen comments of the defects in the box; the list here then shows their new Comments.
        protected void OpenDeleteComments()
        {
            if (Busy || defs == null) return;
            var ids = DefectLinks.ParseIds(txtIds.Text);
            if (ids.Count == 0) return;
            using (var dlg = CreateDeleteDialog(ids))
            {
                dlg.ShowDialog(this);
                foreach (var kv in dlg.Changed)
                    foreach (var d in defects.Where(x => x.Id == kv.Key)) d.CommentNow = kv.Value;
                if (dlg.Changed.Count > 0) Fill();
            }
        }

        protected virtual DeleteCommentsDialog CreateDeleteDialog(List<string> ids)
        {
            return new DeleteCommentsDialog(client, ids, commentField, commentLabel);
        }

        // ------------------------------------------------------------ apply
        protected void DoApply()
        {
            if (Busy || defs == null) return;
            var todo = cases.Where(Wanted).ToList();
            var caseFiles = todo.ToDictionary(c => c, CaseFiles);
            var defTodo = defects.Where(DefectWanted).ToList();
            var comment = CommentText;
            var defComment = defTodo.ToDictionary(d => d, CommentPending);
            var defOwner = defTodo.ToDictionary(d => d, d => { string e; return OwnerTarget(d, out e); });
            var defLinks = defTodo.ToDictionary(d => d, LinksFor);
            var defAttach = defTodo.ToDictionary(d => d, AttachText);
            var defFiles = defTodo.ToDictionary(d => d, DefectFiles);
            var defCopy = defTodo.ToDictionary(d => d, CopyPlan);
            if (todo.Count == 0 && defTodo.Count == 0) return;

            int n = todo.Sum(c => c.New.Count), files = caseFiles.Values.Sum(l => l.Count);
            var sb = new StringBuilder();
            var what = new List<string>();
            if (n > 0) what.Add(string.Format("write {0} field change(s) to {1} test case(s)", n, todo.Count(c => c.New.Count > 0)));
            if (files > 0) what.Add(string.Format("upload {0} file(s) to {1} test case(s)", files, caseFiles.Count(kv => kv.Value.Count > 0)));
            if (defTodo.Count > 0) what.Add(string.Format("update {0} defect(s)", defTodo.Count));
            sb.Append("In ALM: " + string.Join(", ", what) + "?").AppendLine();
            if (todo.Count > 0) sb.AppendLine().AppendLine("Test cases:");
            foreach (var c in todo.Take(15))
                sb.AppendLine("   Defect " + c.DefectIds + "  |  " + c.SetPath + "  >  " + c.Inst.Label + ":   " + ChangeText(c));
            if (todo.Count > 15) sb.AppendLine("   ... and " + (todo.Count - 15) + " more (see the list).");
            if (defTodo.Count > 0)
            {
                sb.AppendLine().AppendLine("Defects:");
                foreach (var d in defTodo.Take(15))
                    sb.AppendLine("   Defect " + d.Id + " (" + d.Summary + "):   "
                                  + string.Join(",  ", new[] { defOwner[d] != "" ? "set " + ownerLabel + " to " + defOwner[d] : null,
                                                               defComment[d] ? "add a comment to " + commentLabel + (defAttach[d] != "" ? " (with " + defAttach[d] + ")" : "") : null,
                                                               defFiles[d].Count > 0 ? "upload " + defFiles[d].Count + " file(s)" : null,
                                                               defCopy[d].Count > 0 ? "copy " + defCopy[d].Count + " attachment(s) from its test cases ("
                                                                   + string.Join(", ", defCopy[d].Select(x => x.Target).Take(5)) + (defCopy[d].Count > 5 ? ", ..." : "") + ")" : null }
                                                    .Where(x => x != null)));
                if (defTodo.Count > 15) sb.AppendLine("   ... and " + (defTodo.Count - 15) + " more.");
            }
            if (comment != "" && defTodo.Any(d => defComment[d] && !DefectLinks.HasComment(d.CommentNow, comment)))
                sb.AppendLine().AppendLine("Comment:  " + (comment.Length > 300 ? comment.Substring(0, 300) + "..." : comment).Replace("\r\n", " / ").Replace("\n", " / "));
            var names = caseFiles.Values.Concat(defFiles.Values).SelectMany(l => l).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count > 0)
                sb.AppendLine().AppendLine("Files:  " + string.Join(", ", names.Take(10)) + (names.Count > 10 ? " ... (" + names.Count + ")" : ""));
            if (todo.Any(c => c.New.ContainsKey("status")))
                sb.AppendLine().Append("If ALM does not allow changing the status directly, a run with that status is added instead.");
            if (!Confirm(sb.ToString().TrimEnd())) return;

            btnApply.Enabled = btnFind.Enabled = false;
            btnStop.Enabled = true;
            int ok = 0, failed = 0, dok = 0, dfailed = 0;
            var user = client.User;
            Run(() =>
            {
                foreach (var c in todo)
                {
                    if (stop) { Log("Stopped."); break; }
                    var before = new Dictionary<string, string>(c.New);
                    var parts = new List<string>();
                    bool bad = false;
                    try
                    {
                        if (c.New.Count > 0) parts.Add(DefectLinks.Apply(client, c, defs));
                        if (caseFiles[c].Count > 0) parts.Add(DefectLinks.UploadFiles(client, "test-instances", c.Inst.Id, caseFiles[c], c.UploadedFiles));
                    }
                    catch (Exception e)
                    {
                        if (!(e is AlmException || e is WebException || e is IOException)) throw;
                        parts.Add("FAILED: " + e.Message);
                        bad = true;
                    }
                    c.Result = string.Join(";  ", parts);
                    if (bad) failed++; else ok++;
                    Log((bad ? "[FAIL] defect " : "[ OK ] defect ") + c.DefectIds + "  " + c.SetPath + " > " + c.Inst.Label + ": " + c.Result);
                    // what is in ALM now (also the fields written before a failure)
                    var inLab = lab == null ? null : lab.FindInstance(c.Inst.Id);
                    if (inLab != null)
                        lock (Changes)
                            foreach (var kv in before.Where(kv => c.Now(kv.Key) == kv.Value))
                                Changes.Add(new FieldChange { Instance = inLab, Field = kv.Key, Value = kv.Value });
                    int done = ok + failed;
                    Ui(() => lblSummary.Text = string.Format("Writing to ALM...  test case {0} / {1}", done, todo.Count));
                }
                foreach (var d in defTodo)
                {
                    if (stop) break;
                    var parts = new List<string>();
                    bool bad = false;
                    if (defOwner[d] != "")
                    {
                        try
                        {
                            client.UpdateEntity("defects", d.Id, new Dictionary<string, string> { { ownerField, defOwner[d] } });
                            d.OwnerNow = defOwner[d];
                            parts.Add(ownerLabel + " = " + defOwner[d]);
                        }
                        catch (Exception e)
                        {
                            if (!(e is AlmException || e is WebException)) throw;
                            parts.Add(ownerLabel + " FAILED: " + e.Message);
                            bad = true;
                        }
                    }
                    if (defComment[d])
                    {
                        try
                        {
                            // the latest Comments, in case someone added one meanwhile
                            var now = client.GetByIds("defects", "id", new[] { d.Id }, new[] { "id", commentField }).FirstOrDefault();
                            string cur;
                            if (now != null && now.TryGetValue(commentField, out cur)) d.CommentNow = cur ?? "";
                            DefectLinks.SizePictures(client, defLinks[d].Where(l => !DefectLinks.LinkThere(d.CommentNow, l.Url)));
                            var body = DefectLinks.CommentBody(d.CommentNow, comment, defLinks[d]);
                            if (body == "") parts.Add("comment already there");
                            else
                            {
                                var value = DefectLinks.AppendCommentHtml(d.CommentNow, user, body, DateTime.Now);
                                client.UpdateEntity("defects", d.Id, new Dictionary<string, string> { { commentField, value } });
                                d.CommentNow = value;
                                int embedded = Regex.Matches(body, "src=\"data:image/").Count;
                                if (embedded > 0)
                                {
                                    // does ALM keep pictures inside the text?
                                    var back = client.GetByIds("defects", "id", new[] { d.Id }, new[] { "id", commentField }).FirstOrDefault();
                                    string saved;
                                    if (back != null && back.TryGetValue(commentField, out saved))
                                    {
                                        d.CommentNow = saved ?? "";
                                        int kept = Regex.Matches(d.CommentNow, "data:image/").Count - Regex.Matches(value, "data:image/").Count + embedded;
                                        if (kept < embedded)
                                        {
                                            parts.Add("comment added, but ALM removed " + (embedded - Math.Max(kept, 0)) + " of its " + embedded + " picture(s)");
                                            Log("ALM did not keep the pictures inside the comment of defect " + d.Id
                                                + ": choose 'pictures from ALM' or 'links only', or copy the attachments to the defects.");
                                        }
                                        else parts.Add("comment added (" + embedded + " picture(s))");
                                    }
                                    else parts.Add("comment added");
                                }
                                else parts.Add("comment added");
                            }
                        }
                        catch (Exception e)
                        {
                            if (!(e is AlmException || e is WebException)) throw;
                            parts.Add("comment FAILED: " + e.Message);
                            bad = true;
                        }
                    }
                    if (defFiles[d].Count > 0)
                    {
                        try { parts.Add(DefectLinks.UploadFiles(client, "defects", d.Id, defFiles[d], d.UploadedFiles)); }
                        catch (Exception e)
                        {
                            if (!(e is AlmException || e is WebException || e is IOException)) throw;
                            parts.Add("files FAILED: " + e.Message);
                            bad = true;
                        }
                    }
                    if (defCopy[d].Count > 0)
                    {
                        int copied = 0, skipped = 0;
                        try
                        {
                            // names on the defect now (the files above are there too)
                            var there = client.ListAttachmentNames("defects", d.Id);
                            foreach (var x in defCopy[d])
                            {
                                if (stop) break;
                                if (there.Contains(x.Target)) { skipped++; continue; }
                                client.UploadAttachment("defects", d.Id, client.DownloadAttachment("test-instances", x.Case.Inst.Id, x.Att), x.Target);
                                there.Add(x.Target);
                                copied++;
                            }
                            d.AttachNames = there;
                            parts.Add(copied + " copied from test cases" + (skipped > 0 ? ", " + skipped + " already there" : ""));
                        }
                        catch (Exception e)
                        {
                            if (!(e is AlmException || e is WebException || e is IOException)) throw;
                            parts.Add((copied > 0 ? copied + " copied, then " : "") + "copy FAILED: " + e.Message);
                            bad = true;
                        }
                    }
                    d.Result = string.Join(";  ", parts);
                    if (bad) dfailed++; else dok++;
                    Log((bad ? "[FAIL] defect " : "[ OK ] defect ") + d.Id + ": " + d.Result);
                    int done = dok + dfailed;
                    Ui(() => lblSummary.Text = string.Format("Writing to ALM...  defect {0} / {1}", done, defTodo.Count));
                }
                Log(string.Format("Done: {0} test case(s) updated, {1} failed;  {2} defect(s) updated, {3} failed.", ok, failed, dok, dfailed));
            }, () =>
            {
                btnStop.Enabled = false;
                Replan();
            });
        }
    }
}
