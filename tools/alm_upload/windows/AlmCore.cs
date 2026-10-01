// ALM Image Uploader - core logic (ALM REST client, Test Lab folder matching, uploader).
// C# port of alm_upload.py, for a single native Windows .exe (.NET Framework 4.x,
// which ships with Windows 10/11). Build: see build.sh.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace AlmImageUploader
{
    public class AlmException : Exception
    {
        public AlmException(string message) : base(message) { }
    }

    public class AlmClient
    {
        public readonly string Base;
        public string Domain, Project;   // chosen after login
        readonly CookieContainer cookies = new CookieContainer();
        string user, password;

        public int Timeout = 120000;
        public string User { get { return user; } }

        public AlmClient(string baseUrl, bool ignoreSsl)
        {
            baseUrl = baseUrl.Trim().TrimEnd('/');
            if (!baseUrl.EndsWith("/qcbin", StringComparison.OrdinalIgnoreCase))
                baseUrl += "/qcbin";
            Base = baseUrl;

            // TLS 1.2 is not on by default in older .NET Framework versions
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.Expect100Continue = false;
            if (ignoreSsl)
                ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            if (WebRequest.DefaultWebProxy != null)
                WebRequest.DefaultWebProxy.Credentials = CredentialCache.DefaultNetworkCredentials;
        }

        // ---------------------------------------------------------------- auth
        public void Login(string user, string password)
        {
            this.user = user;
            this.password = password;
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

            // ALM 12.60+ and OpenText ALM
            var r = Send("POST", Base + "/api/authentication/sign-in", null, null,
                         new Dictionary<string, string> { { "Authorization", "Basic " + basic } }, false);
            if (r.Status == 404)
            {
                // older ALM (11.x - 12.5x)
                string xml = "<alm-authentication><user>" + SecurityElementEscape(user) + "</user><password>" +
                             SecurityElementEscape(password) + "</password></alm-authentication>";
                r = Send("POST", Base + "/authentication-point/alm-authenticate",
                         Encoding.UTF8.GetBytes(xml), "application/xml", null, false);
                CheckLogin(r);
                r = Send("POST", Base + "/rest/site-session", new byte[0], "application/xml", null, false);
                if (r.Status >= 400 && r.Status != 404)
                    Check(r, "site-session");
            }
            else
            {
                CheckLogin(r);
            }
        }

        static void CheckLogin(Response r)
        {
            if (r.Status == 401 || r.Status == 403)
                throw new AlmException("wrong username or password (HTTP " + r.Status + ")");
            Check(r, "login");
        }

        public void Logout()
        {
            foreach (var path in new[] { "/api/authentication/sign-out", "/authentication-point/logout" })
            {
                try
                {
                    if (Send("GET", Base + path, null, null, null, false).Status < 400)
                        return;
                }
                catch (WebException) { }
            }
        }

        static string SecurityElementEscape(string s)
        {
            return System.Security.SecurityElement.Escape(s ?? "");
        }

        // ------------------------------------------------------------ requests
        public class Response
        {
            public int Status;
            public string Body;
            public byte[] Bytes;
        }

        Response Send(string method, string url, byte[] body, string contentType,
                      Dictionary<string, string> headers, bool retryOn401, string accept = "application/xml")
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.CookieContainer = cookies;
            req.Accept = accept;
            req.Timeout = Timeout;
            req.ReadWriteTimeout = Timeout;
            req.AllowAutoRedirect = true;
            foreach (Cookie c in cookies.GetCookies(new Uri(Base)))
                if (c.Name == "XSRF-TOKEN")
                    req.Headers["X-XSRF-TOKEN"] = c.Value;
            if (headers != null)
                foreach (var kv in headers)
                    req.Headers[kv.Key] = kv.Value;
            if (body != null)
            {
                req.ContentType = contentType ?? "application/octet-stream";
                req.ContentLength = body.Length;
                using (var s = req.GetRequestStream())
                    s.Write(body, 0, body.Length);
            }

            var resp = new Response();
            HttpWebResponse wr;
            try
            {
                wr = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException e)
            {
                wr = e.Response as HttpWebResponse;
                if (wr == null)
                    throw;
            }
            using (wr)
            {
                resp.Status = (int)wr.StatusCode;
                using (var ms = new MemoryStream())
                {
                    wr.GetResponseStream().CopyTo(ms);
                    resp.Bytes = ms.ToArray();
                }
                resp.Body = Encoding.UTF8.GetString(resp.Bytes);
            }

            if (resp.Status == 401 && retryOn401 && user != null)
            {
                // session expired (common while watching a folder): log in again, retry once
                Login(user, password);
                return Send(method, url, body, contentType, headers, false, accept);
            }
            return resp;
        }

        static void Check(Response r, string what)
        {
            if (r.Status >= 400)
            {
                string body = r.Body ?? "";
                var m = Regex.Match(body, "<Title>(.*?)</Title>", RegexOptions.Singleline);
                if (m.Success) body = m.Groups[1].Value;
                if (body.Length > 300) body = body.Substring(0, 300);
                throw new AlmException(string.Format("{0} failed: HTTP {1} {2}", what, r.Status, body.Trim()));
            }
        }

        string Rest(string collection)
        {
            if (string.IsNullOrEmpty(Domain) || string.IsNullOrEmpty(Project))
                throw new AlmException("choose a domain and project first");
            return string.Format("{0}/rest/domains/{1}/projects/{2}/{3}", Base,
                Uri.EscapeDataString(Domain), Uri.EscapeDataString(Project), collection);
        }

        // ------------------------------------------------- domains / projects
        public List<string> GetDomains()
        {
            var r = Send("GET", Base + "/rest/domains", null, null, null, true);
            Check(r, "list domains");
            return NamesOf(r.Body, "Domain");
        }

        public List<string> GetProjects(string domain)
        {
            var r = Send("GET", Base + "/rest/domains/" + Uri.EscapeDataString(domain) + "/projects",
                         null, null, null, true);
            Check(r, "list projects of " + domain);
            return NamesOf(r.Body, "Project");
        }

        static List<string> NamesOf(string xml, string tag)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return doc.GetElementsByTagName(tag).Cast<XmlElement>()
                      .Select(e => e.GetAttribute("Name")).Where(n => n != "")
                      .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ------------------------------------------------ Test Lab folder tree
        static readonly string[] TreeFields = { "id", "name", "parent-id" };

        /// Id of the Test Lab "Root" folder (0 on standard ALM installs).
        public string GetTestLabRootId()
        {
            var zero = GetEntities("test-set-folders", "{id[0]}", TreeFields);
            if (zero.Count > 0) return "0";
            var roots = GetEntities("test-set-folders", "{name['Root']}", TreeFields);
            return roots.Count > 0 ? roots[0]["id"] : "0";
        }

        public List<Dictionary<string, string>> GetChildFolders(string folderId)
        {
            return GetEntities("test-set-folders", "{parent-id[" + folderId + "]}", TreeFields)
                   .OrderBy(f => f.ContainsKey("name") ? f["name"] : "", StringComparer.OrdinalIgnoreCase).ToList();
        }

        public List<Dictionary<string, string>> GetTestSetsIn(string folderId)
        {
            return GetEntities("test-sets", "{parent-id[" + folderId + "]}", TreeFields)
                   .OrderBy(f => f.ContainsKey("name") ? f["name"] : "", StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ------------------------------------------------------------ entities
        // fields an ALM version does not have, per collection (e.g. test-instance 'test-order' on ALM 25.x)
        readonly Dictionary<string, HashSet<string>> missingFields = new Dictionary<string, HashSet<string>>();

        public List<Dictionary<string, string>> GetEntities(string collection, string query, string[] fields)
        {
            HashSet<string> missing;
            if (!missingFields.TryGetValue(collection, out missing))
                missingFields[collection] = missing = new HashSet<string>();
            var result = new List<Dictionary<string, string>>();
            int start = 1;
            while (true)
            {
                var use = fields == null ? null : fields.Where(f => !missing.Contains(f)).ToArray();
                var url = Rest(collection) + "?page-size=1000&start-index=" + start;
                if (query != null) url += "&query=" + Uri.EscapeDataString(query);
                if (use != null) url += "&fields=" + Uri.EscapeDataString(string.Join(",", use));
                var r = Send("GET", url, null, null, null, true);
                if (r.Status == 400 && use != null)
                {
                    // "Entity: test-instance doesn't have a field named: 'test-order'" -> drop it, start again
                    var m = Regex.Match(r.Body ?? "", @"field named:?\s*'([^']+)'", RegexOptions.IgnoreCase);
                    if (m.Success && use.Contains(m.Groups[1].Value) && m.Groups[1].Value != "id")
                    {
                        missing.Add(m.Groups[1].Value);
                        result.Clear();
                        start = 1;
                        continue;
                    }
                }
                Check(r, "read " + collection);
                int total;
                var batch = ParseEntities(r.Body, out total);
                result.AddRange(batch);
                if (batch.Count == 0 || result.Count >= total)
                    return result;
                start += batch.Count;
            }
        }

        public List<Dictionary<string, string>> GetByIds(string collection, string field,
                                                          IEnumerable<string> ids, string[] fields)
        {
            var list = ids.Where(i => !string.IsNullOrEmpty(i)).Distinct().OrderBy(i => i).ToList();
            var result = new List<Dictionary<string, string>>();
            for (int n = 0; n < list.Count; n += 50)
            {
                var chunk = list.Skip(n).Take(50);
                var q = "{" + field + "[" + string.Join(" OR ", chunk) + "]}";
                result.AddRange(GetEntities(collection, q, fields));
            }
            return result;
        }

        static List<Dictionary<string, string>> ParseEntities(string xml, out int total)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var list = new List<Dictionary<string, string>>();
            var root = doc.DocumentElement;
            if (!int.TryParse(root.GetAttribute("TotalResults"), out total))
                total = -1;
            foreach (XmlElement e in root.GetElementsByTagName("Entity"))
            {
                var d = new Dictionary<string, string>();
                foreach (XmlElement f in e.GetElementsByTagName("Field"))
                {
                    var v = f.GetElementsByTagName("Value");
                    d[f.GetAttribute("Name")] = v.Count > 0 ? v[0].InnerText : "";
                }
                list.Add(d);
            }
            if (total < 0) total = list.Count;
            return list;
        }

        // --------------------------------------------------------- attachments
        public class Attachment
        {
            public string Id, Name;
            public long Size;
        }

        /// Attachments of one entity, in the order ALM lists them.
        public List<Attachment> GetAttachments(string entity, string id)
        {
            var r = Send("GET", Rest(entity) + "/" + id + "/attachments", null, null, null, true);
            Check(r, "list attachments of " + entity + " " + id);
            int total;
            return ParseEntities(r.Body, out total).Select(e =>
            {
                string v;
                long size;
                return new Attachment
                {
                    Id = e.TryGetValue("id", out v) ? v : "",
                    Name = e.TryGetValue("name", out v) ? v : "",
                    Size = e.TryGetValue("file-size", out v) && long.TryParse(v, out size) ? size : -1,
                };
            }).Where(a => a.Name != "").ToList();
        }

        /// Download one attachment's file content.
        public byte[] DownloadAttachment(string entity, string id, Attachment att)
        {
            var r = Send("GET", Rest(entity) + "/" + id + "/attachments/" + Uri.EscapeDataString(att.Name),
                         null, null, null, true, "application/octet-stream");
            if ((r.Status == 404 || r.Status == 400) && att.Id != "")
                r = Send("GET", Rest("attachments") + "/" + att.Id + "?alt=application/octet-stream",
                         null, null, null, true, "application/octet-stream");
            Check(r, "download " + att.Name);
            return r.Bytes;
        }

        public HashSet<string> ListAttachmentNames(string entity, string id)
        {
            var r = Send("GET", Rest(entity) + "/" + id + "/attachments", null, null, null, true);
            Check(r, "list attachments of " + entity + " " + id);
            int total;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in ParseEntities(r.Body, out total))
            {
                string name;
                if (e.TryGetValue("name", out name) && name != "")
                    names.Add(name);
            }
            return names;
        }

        public void UploadAttachment(string entity, string id, string path, string name)
        {
            var r = Send("POST", Rest(entity) + "/" + id + "/attachments", File.ReadAllBytes(path),
                         "application/octet-stream",
                         new Dictionary<string, string> { { "Slug", HeaderSafe(name) } }, true);
            Check(r, "upload " + name + " to " + entity + " " + id);
        }

        static string HeaderSafe(string s)
        {
            // HTTP headers are ASCII only
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c < 32 || c > 126 ? '_' : c);
            return sb.ToString();
        }

        // ------------------------------------------------------ field values
        public class FieldDef
        {
            public string Name, Label, Type, ListId;
            public bool Editable, Active, System, Required;
            public override string ToString() { return Label + "   (" + Name + ")"; }
        }

        /// Field definitions of an entity, e.g. "test-instance" (Label is what ALM shows as the column name).
        public List<FieldDef> GetFieldDefs(string entity)
        {
            var r = Send("GET", Rest("customization/entities/" + entity + "/fields"), null, null, null, true);
            Check(r, "read the fields of " + entity);
            var doc = new XmlDocument();
            doc.LoadXml(r.Body);
            Func<XmlElement, string, string> child = (e, tag) =>
            {
                foreach (XmlNode n in e.ChildNodes)
                    if (n is XmlElement && string.Equals(n.Name, tag, StringComparison.OrdinalIgnoreCase)) return n.InnerText.Trim();
                return "";
            };
            return doc.GetElementsByTagName("Field").Cast<XmlElement>().Select(e => new FieldDef
            {
                Name = e.GetAttribute("Name"),
                Label = e.GetAttribute("Label") != "" ? e.GetAttribute("Label") : e.GetAttribute("Name"),
                Type = child(e, "Type"),
                ListId = child(e, "List-Id"),
                Editable = child(e, "Editable") != "false",
                Active = child(e, "Active") != "false",
                System = child(e, "System") == "true",
                Required = child(e, "Required") == "true",
            }).Where(f => f.Name != "").ToList();
        }

        /// The values of an ALM selection list (all levels, in list order).
        public List<string> GetListValues(string listId)
        {
            var r = Send("GET", Rest("customization/used-lists") + "?id=" + Uri.EscapeDataString(listId), null, null, null, true);
            if (r.Status == 404)
                r = Send("GET", Rest("customization/lists") + "?id=" + Uri.EscapeDataString(listId), null, null, null, true);
            Check(r, "read selection list " + listId);
            var doc = new XmlDocument();
            doc.LoadXml(r.Body);
            return doc.GetElementsByTagName("Item").Cast<XmlElement>()
                      .Select(e => e.GetAttribute("value") != "" ? e.GetAttribute("value") : e.GetAttribute("Value"))
                      .Where(v => v != "").Distinct().ToList();
        }

        /// Change field values of one entity, e.g. UpdateEntity("test-instances", "885", {"user-01": "NA"}).
        public void UpdateEntity(string collection, string id, Dictionary<string, string> values)
        {
            var type = collection.EndsWith("s") ? collection.Substring(0, collection.Length - 1) : collection;
            var r = Send("PUT", Rest(collection) + "/" + id, EntityXml(type, values), "application/xml", null, true);
            Check(r, "update " + type + " " + id);
        }

        static byte[] EntityXml(string type, Dictionary<string, string> values)
        {
            var xml = new StringBuilder("<Entity Type=\"" + type + "\"><Fields>");
            foreach (var kv in values)
                xml.Append("<Field Name=\"" + SecurityElementEscape(kv.Key) + "\"><Value>" + SecurityElementEscape(kv.Value) + "</Value></Field>");
            xml.Append("</Fields></Entity>");
            return Encoding.UTF8.GetBytes(xml.ToString());
        }

        /// Records a result the way ALM's own "fast run" does: a manual run with that status,
        /// which also sets the test case's status. Used when the status cannot be set directly.
        public void CreateRun(TestInstance inst, string status)
        {
            var values = new Dictionary<string, string>
            {
                { "name", "Fast_Run_" + DateTime.Now.ToString("M-d_H-m-s") },
                { "test-id", inst.TestId }, { "testcycl-id", inst.Id }, { "cycle-id", inst.SetId },
                { "owner", user ?? "" }, { "subtype-id", "hp.qc.run.MANUAL" }, { "status", status },
            };
            var r = Send("POST", Rest("runs"), EntityXml("run", values), "application/xml", null, true);
            Check(r, "create a run for test case " + inst.Id);
        }

        public static string QueryValue(string name)
        {
            // ALM queries cannot escape quotes: use a wildcard and filter exactly afterwards
            return name.Replace("'", "*");
        }
    }

    // -------------------------------------------------------------------- helpers
    public static class Util
    {
        public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

        public static bool IsImage(string path)
        {
            return File.Exists(path) && ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        public static List<string> ListImages(string folder)
        {
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                            .Where(IsImage).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool FileIsStable(string path)
        {
            // avoid uploading a screenshot that is still being written
            try
            {
                long a = new FileInfo(path).Length;
                Thread.Sleep(1000);
                long b = new FileInfo(path).Length;
                return a == b && a > 0;
            }
            catch (IOException) { return false; }
        }

        // Lower-case and collapse separators so 'Login_Test' matches 'Login Test'. Characters
        // Windows does not allow in folder names count as separators too.
        public static string Norm(string s)
        {
            return Regex.Replace((s ?? "").ToLowerInvariant(), @"[\s_\-.\\/:*?""<>|]+", " ").Trim();
        }

        public static string SafeFolderName(string name)
        {
            name = Regex.Replace(name ?? "", @"[\\/:*?""<>|]", "_").Trim().TrimEnd('.', ' ');
            return name == "" ? "_" : name;
        }

        public static string RelPath(string root, string path)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length)
                                                                              : Path.GetFileName(path);
        }

        /// Compares like people read: "1.9 Login" before "1.10 Login", "Step 2" before "Step 10".
        public static int NaturalCompare(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        public static int ToInt(string s)
        {
            int n;
            return int.TryParse(s, out n) ? n : 0;
        }
    }

    // ------------------------------------------------------------------ Test Lab
    public class TestInstance
    {
        public string Id, TestId, SetId, Name, TestName, Label;
        /// Extra field values read on load, e.g. { "user-01": "NA" } for the Comments column.
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public int Order;
        public bool Repeated;
    }

    public class TestSet
    {
        public string Id, Name;
        public List<string> Path = new List<string>();   // relative to the chosen folder, ends with Name
        public List<TestInstance> Instances = new List<TestInstance>();
        public string PathText { get { return string.Join("\\", Path); } }
    }

    public class PlanItem
    {
        public string File;
        public TestSet Set;
        public TestInstance Instance;
        public string Error;
    }

    /// Every test set and test case under one Test Lab folder (and its sub folders).
    /// Local images: <image folder>\<test set>\<test case>\*.png. When two test sets share a
    /// name, the sub folder path is used instead; a test that is in a set more than once
    /// uses its instance name, e.g. "[2]Login".
    public class TestLabFolder
    {
        readonly AlmClient client;
        public readonly string FolderId;
        public readonly List<TestSet> Sets = new List<TestSet>();
        readonly Dictionary<string, TestSet> pathIndex = new Dictionary<string, TestSet>();
        readonly Dictionary<string, List<TestSet>> nameIndex = new Dictionary<string, List<TestSet>>();
        static readonly string[] FolderFields = { "id", "name", "parent-id" };

        public TestLabFolder(AlmClient client, string folder, IEnumerable<string> extraFields = null)
        {
            this.client = client;
            FolderId = ResolveFolder(folder.Trim());

            var paths = new Dictionary<string, List<string>> { { FolderId, new List<string>() } };
            var frontier = new List<string> { FolderId };
            while (frontier.Count > 0)
            {
                var children = client.GetByIds("test-set-folders", "parent-id", frontier, FolderFields);
                frontier = new List<string>();
                foreach (var c in children)
                {
                    if (paths.ContainsKey(c["id"]) || !paths.ContainsKey(Get(c, "parent-id"))) continue;
                    paths[c["id"]] = new List<string>(paths[c["parent-id"]]) { Get(c, "name") };
                    frontier.Add(c["id"]);
                }
            }

            var byId = new Dictionary<string, TestSet>();
            foreach (var s in client.GetByIds("test-sets", "parent-id", paths.Keys, FolderFields))
            {
                var parent = Get(s, "parent-id");
                if (!paths.ContainsKey(parent)) continue;
                var ts = new TestSet { Id = s["id"], Name = Get(s, "name") };
                ts.Path.AddRange(paths[parent]);
                ts.Path.Add(ts.Name);
                Sets.Add(ts);
                byId[ts.Id] = ts;
            }
            Sets.Sort((a, b) => string.Compare(a.PathText, b.PathText, StringComparison.OrdinalIgnoreCase));

            var insts = client.GetByIds("test-instances", "cycle-id", byId.Keys,
                                        new[] { "id", "test-id", "cycle-id", "test-order", "name" }
                                            .Concat(extraFields ?? new string[0]).Distinct().ToArray());
            var names = client.GetByIds("tests", "id", insts.Select(i => Get(i, "test-id")), new[] { "id", "name" })
                              .ToDictionary(t => t["id"], t => Get(t, "name"));
            foreach (var i in insts)
            {
                TestSet ts;
                if (!byId.TryGetValue(Get(i, "cycle-id"), out ts)) continue;
                string tn;
                names.TryGetValue(Get(i, "test-id"), out tn);
                var inst = new TestInstance
                {
                    Id = i["id"], TestId = Get(i, "test-id"), SetId = ts.Id, Name = Get(i, "name"),
                    TestName = tn ?? "", Order = Util.ToInt(Get(i, "test-order")),
                };
                foreach (var f in extraFields ?? new string[0])
                    inst.Values[f] = Get(i, f);
                ts.Instances.Add(inst);
            }
            foreach (var ts in Sets)
            {
                // 'test-order' / 'name' are missing on some ALM versions: fall back to the id order
                ts.Instances.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order)
                                                               : Util.ToInt(a.Id).CompareTo(Util.ToInt(b.Id)));
                foreach (var g in ts.Instances.GroupBy(i => i.TestId))
                {
                    int k = 0;
                    foreach (var i in g)
                    {
                        k++;
                        i.Repeated = g.Count() > 1;
                        if (i.Name == "") i.Name = "[" + k + "]" + i.TestName;   // ALM's own instance naming
                        i.Label = i.Repeated ? i.Name : i.TestName;
                    }
                }
                pathIndex[Key(ts.Path)] = ts;
                var nk = Util.Norm(ts.Name);
                if (!nameIndex.ContainsKey(nk)) nameIndex[nk] = new List<TestSet>();
                nameIndex[nk].Add(ts);
            }
        }

        static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? v ?? "" : "";
        }

        static string Key(IEnumerable<string> parts)
        {
            return string.Join("\u0001", parts.Select(Util.Norm));
        }

        public int InstanceCount { get { return Sets.Sum(s => s.Instances.Count); } }

        public TestSet SetOf(TestInstance inst)
        {
            return Sets.First(s => s.Id == inst.SetId);
        }

        public TestInstance FindInstance(string id)
        {
            return Sets.SelectMany(s => s.Instances).FirstOrDefault(i => i.Id == id);
        }

        // ------------------------------------------------------------ folders
        List<Dictionary<string, string>> FoldersNamed(string name, HashSet<string> parents)
        {
            var q = "{name['" + AlmClient.QueryValue(name) + "']}";
            return client.GetEntities("test-set-folders", q, FolderFields)
                         .Where(f => string.Equals(Get(f, "name"), name, StringComparison.OrdinalIgnoreCase)
                                     && (parents == null || parents.Contains(Get(f, "parent-id"))))
                         .ToList();
        }

        string ResolveFolder(string folder)
        {
            if (Regex.IsMatch(folder, @"^\d+$"))
                return folder;
            var parts = Regex.Split(folder, @"[\\/]+").Select(p => p.Trim()).Where(p => p != "").ToList();
            if (parts.Count > 0 && parts[0].Equals("root", StringComparison.OrdinalIgnoreCase))
                parts.RemoveAt(0);
            if (parts.Count == 0)
                throw new AlmException("Choose a Test Lab folder below Root, e.g. Root\\Release 1");

            var cands = FoldersNamed(parts[0], null);
            if (cands.Count > 1)
            {
                // prefer the one directly under Root
                var parents = client.GetByIds("test-set-folders", "id", cands.Select(c => Get(c, "parent-id")),
                                              new[] { "id", "name" }).ToDictionary(f => f["id"], f => Get(f, "name"));
                var top = cands.Where(c =>
                {
                    string pn;
                    return parents.TryGetValue(Get(c, "parent-id"), out pn) &&
                           pn.Equals("root", StringComparison.OrdinalIgnoreCase);
                }).ToList();
                if (top.Count > 0) cands = top;
            }
            foreach (var part in parts.Skip(1))
                cands = FoldersNamed(part, new HashSet<string>(cands.Select(c => c["id"])));
            if (cands.Count == 0)
                throw new AlmException("Test Lab folder not found: " + folder);
            if (cands.Count > 1)
                throw new AlmException(string.Format("Test Lab folder '{0}' is ambiguous (ids: {1}); enter the folder ID",
                                                     folder, string.Join(", ", cands.Select(c => c["id"]))));
            return cands[0]["id"];
        }

        /// Relative local folder parts for a test set (name alone when unique).
        public List<string> LocalSetPath(TestSet ts)
        {
            return nameIndex[Util.Norm(ts.Name)].Count == 1 ? new List<string> { ts.Name } : ts.Path;
        }

        public int MakeFolders(string imageRoot)
        {
            int made = 0;
            foreach (var ts in Sets)
            {
                var baseDir = LocalSetPath(ts).Aggregate(imageRoot, (acc, p) => Path.Combine(acc, Util.SafeFolderName(p)));
                foreach (var i in ts.Instances)
                {
                    var dir = Path.Combine(baseDir, Util.SafeFolderName(i.Label));
                    if (!Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                        made++;
                    }
                }
            }
            return made;
        }

        // ------------------------------------------------------------ matching
        public PlanItem Resolve(string imageRoot, string file)
        {
            var item = new PlanItem { File = file };
            var parts = Util.RelPath(imageRoot, file).Split(Path.DirectorySeparatorChar);
            var dirs = parts.Take(parts.Length - 1).ToList();
            if (dirs.Count == 0)
            {
                item.Error = "image must be inside a <test set>\\<test case> folder";
                return item;
            }
            for (int k = dirs.Count; k > 0; k--)
            {
                TestSet exact;
                List<TestSet> sets;
                if (pathIndex.TryGetValue(Key(dirs.Take(k)), out exact))
                    sets = new List<TestSet> { exact };
                else if (k == 1 && nameIndex.TryGetValue(Util.Norm(dirs[0]), out sets)) { }
                else continue;

                if (sets.Count > 1)
                {
                    item.Error = string.Format("{0} test sets are named '{1}'; put the images under {2}",
                        sets.Count, dirs[k - 1], string.Join(" or ", sets.Select(s => s.PathText)));
                    return item;
                }
                item.Set = sets[0];
                string err;
                item.Instance = k < dirs.Count ? FindInstance(item.Set, dirs[k], out err)
                                               : FindByFileName(item.Set, parts[parts.Length - 1], out err);
                item.Error = err;
                return item;
            }
            item.Error = string.Format("no test set named '{0}' in this Test Lab folder", dirs[0]);
            return item;
        }

        static TestInstance FindInstance(TestSet ts, string folderName, out string error)
        {
            var key = Util.Norm(folderName);
            var tries = new[]
            {
                ts.Instances.Where(i => i.Id == key).ToList(),
                ts.Instances.Where(i => i.Name != "" && Util.Norm(i.Name) == key).ToList(),
                ts.Instances.Where(i => Util.Norm(i.TestName) == key).ToList(),
            };
            foreach (var m in tries)
            {
                if (m.Count == 1) { error = null; return m[0]; }
                if (m.Count > 1)
                {
                    error = string.Format("'{0}' is in test set '{1}' {2} times; name the folder like '{3}'",
                        folderName, ts.Name, m.Count, m[0].Name != "" ? m[0].Name : "[1]" + folderName);
                    return null;
                }
            }
            error = string.Format("no test case '{0}' in test set '{1}'", folderName, ts.Name);
            return null;
        }

        static TestInstance FindByFileName(TestSet ts, string fileName, out string error)
        {
            var stem = Util.Norm(Path.GetFileNameWithoutExtension(fileName));
            TestInstance best = null;
            foreach (var i in ts.Instances)
            {
                var key = Util.Norm(i.Label);
                if (key != "" && (stem == key || stem.StartsWith(key + " ")) &&
                    (best == null || key.Length > Util.Norm(best.Label).Length))
                    best = i;
            }
            if (best == null)
            {
                error = string.Format("put it in a test case folder inside '{0}'", ts.Name);
                return null;
            }
            if (best.Repeated && !(best.Name != "" && stem.StartsWith(Util.Norm(best.Name))))
            {
                error = string.Format("test '{0}' is in the set more than once; use a '{1}' folder",
                                      best.TestName, best.Name != "" ? best.Name : "[1]" + best.TestName);
                return null;
            }
            error = null;
            return best;
        }

        public List<PlanItem> Plan(string imageRoot)
        {
            return Util.ListImages(imageRoot).Select(f => Resolve(imageRoot, f)).ToList();
        }
    }

    /// Suggests a test case for an image from its file name, by word / number overlap with
    /// "<test set> <test case>": exact words count most, then word starts (land ~ landing),
    /// small typos and common abbreviations (pw ~ password). A clear winner is a strong
    /// suggestion; a close call is a weak one ("maybe"). The user always confirms.
    public class Suggester
    {
        readonly List<KeyValuePair<TestInstance, List<string>>> candidates = new List<KeyValuePair<TestInstance, List<string>>>();

        static readonly Dictionary<string, string> Abbrev = new Dictionary<string, string>
        {
            { "pw", "password" }, { "pwd", "password" }, { "pass", "password" }, { "msg", "message" },
            { "acc", "account" }, { "acct", "account" }, { "btn", "button" }, { "err", "error" },
            { "num", "number" }, { "no", "number" }, { "info", "information" }, { "txn", "transaction" },
            { "trx", "transaction" }, { "auth", "authentication" }, { "addr", "address" }, { "amt", "amount" },
            { "img", "image" }, { "pic", "picture" }, { "ver", "version" }, { "upd", "update" },
            { "dl", "download" }, { "maint", "maintenance" }, { "reg", "register" }, { "noti", "notification" },
            { "notif", "notification" }, { "pwd2", "password" }, { "sec", "security" }, { "qn", "question" },
        };

        static readonly HashSet<string> Ignore = new HashSet<string>
            { "the", "and", "of", "to", "an", "in", "on", "for", "with", "copy", "png", "jpg" };

        public Suggester(TestLabFolder lab)
        {
            foreach (var ts in lab.Sets)
                foreach (var i in ts.Instances)
                    candidates.Add(new KeyValuePair<TestInstance, List<string>>(i, Tokens(ts.Name + " " + i.TestName)));
        }

        public static List<string> Tokens(string s)
        {
            s = Regex.Replace(s ?? "", "([a-z])([A-Z])", "$1 $2");          // NotFound -> Not Found
            return Regex.Matches(s.ToLowerInvariant(), @"\d+(?:\.\d+)*|[a-z]+")
                        .Cast<Match>().Select(m => m.Value).Where(t => !Ignore.Contains(t)).Distinct().ToList();
        }

        static int Distance(string a, string b)
        {
            var d = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) d[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                int prev = d[0];
                d[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int tmp = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = tmp;
                }
            }
            return d[b.Length];
        }

        static bool IsSubsequence(string small, string big)
        {
            int k = 0;
            foreach (char ch in big) if (k < small.Length && ch == small[k]) k++;
            return k == small.Length;
        }

        /// How well one file-name token matches one test token (0 = not at all).
        static double Match(string t, string c)
        {
            if (t == c) return char.IsDigit(t[0]) ? 1 : 2;
            if (char.IsDigit(t[0]) || char.IsDigit(c[0])) return 0;
            string full;
            if (Abbrev.TryGetValue(t, out full) && (c == full || c.StartsWith(full))) return 1.8;
            if (t.Length >= 3 && c.Length >= 3 && (t.StartsWith(c) || c.StartsWith(t))) return 1.5;   // land ~ landing
            int max = Math.Max(t.Length, c.Length);
            if (max >= 5 && Distance(t, c) <= (max >= 8 ? 2 : 1)) return 1.2;                     // small typo
            if (t.Length >= 2 && t.Length <= 3 && c.Length > t.Length && c[0] == t[0] && IsSubsequence(t, c))
                return 1;                                                                            // pw ~ password
            return 0;
        }

        public TestInstance Suggest(string fileName, out bool weak)
        {
            weak = false;
            var img = Tokens(Path.GetFileNameWithoutExtension(fileName));
            // single letters written apart ("1.1 D,X") may be one code ("StatusDX")
            var letters = new List<string>();
            foreach (var t in img.Concat(new[] { "0" }))
            {
                if (t.Length == 1 && char.IsLetter(t[0])) { letters.Add(t); continue; }
                if (letters.Count > 1 && !img.Contains(string.Concat(letters))) img.Add(string.Concat(letters));
                letters.Clear();
            }
            if (!img.Any(t => !char.IsDigit(t[0]))) return null;
            var scored = new List<KeyValuePair<TestInstance, double>>();
            foreach (var c in candidates)
            {
                double score = 0;
                bool word = false;
                foreach (var t in img)
                {
                    double m = c.Value.Select(x => Match(t, x)).DefaultIfEmpty(0).Max();
                    score += m;
                    if (m > 0 && !char.IsDigit(t[0])) word = true;
                }
                if (!word) continue;
                scored.Add(new KeyValuePair<TestInstance, double>(c.Key, score - 0.01 * c.Value.Count));   // prefer shorter names
            }
            if (scored.Count == 0) return null;
            scored.Sort((x, y) => y.Value != x.Value ? y.Value.CompareTo(x.Value)
                                                     : Util.NaturalCompare(x.Key.Label, y.Key.Label));
            double best = scored[0].Value, second = scored.Count > 1 ? scored[1].Value : 0;
            if (best < 0.95) return null;
            weak = best < 1.9 || best - second < 0.3;
            return scored[0].Key;
        }
    }

    // ----------------------------------------------------------- manual assigning
    public class ImageItem
    {
        public string File, Rel;
        public readonly List<TestInstance> Assigned = new List<TestInstance>();
        public readonly HashSet<string> Uploaded = new HashSet<string>();   // test instance ids

        /// Suggested test cases (not assigned yet) and why.
        public readonly List<TestInstance> Suggested = new List<TestInstance>();
        public bool SuggestWeak;
        public string SuggestLike;           // "1.3 Login-4.png" when it comes from a similar image

        // suggestion from the file name alone, worked out once on load
        public TestInstance NameSuggestion;
        public bool NameSuggestionWeak;

        public TestInstance Suggestion { get { return Suggested.Count > 0 ? Suggested[0] : null; } }
        public bool FullyUploaded { get { return Assigned.Count > 0 && Assigned.All(i => Uploaded.Contains(i.Id)); } }

        /// "1.3 Login-5.png" -> "1.3 login": images that differ only by a trailing number.
        public static string SiblingKey(string rel)
        {
            var dir = Path.GetDirectoryName(rel) ?? "";
            var stem = Path.GetFileNameWithoutExtension(rel);
            var core = Regex.Replace(stem, @"(?:[\s_\-]*\(?\d{1,3}\)?)+$", "");
            core = Util.Norm(core);
            if (!Regex.IsMatch(core, "[a-z]")) return null;   // "1.1.png" / "1.2.png" are not siblings
            return Util.Norm(dir) + "|" + core;
        }

        /// Recomputes suggestions: an image whose "sibling" (same name apart from a trailing
        /// number) is assigned gets the same test cases; otherwise the file-name suggestion.
        public static void RefreshSuggestions(List<ImageItem> images)
        {
            var bySibling = new Dictionary<string, ImageItem>();
            foreach (var img in images.Where(i => i.Assigned.Count > 0))
            {
                var key = SiblingKey(img.Rel);
                if (key != null && !bySibling.ContainsKey(key)) bySibling[key] = img;
            }
            foreach (var img in images)
            {
                img.Suggested.Clear();
                img.SuggestLike = null;
                img.SuggestWeak = false;
                if (img.Assigned.Count > 0) continue;
                var key = SiblingKey(img.Rel);
                ImageItem sib;
                if (key != null && bySibling.TryGetValue(key, out sib))
                {
                    img.Suggested.AddRange(sib.Assigned);
                    img.SuggestLike = Path.GetFileName(sib.Rel);
                }
                else if (img.NameSuggestion != null)
                {
                    img.Suggested.Add(img.NameSuggestion);
                    img.SuggestWeak = img.NameSuggestionWeak;
                }
            }
        }
    }

    /// Remembers which image goes to which test case (and what is uploaded) in a text file
    /// next to the images, so the work can continue another day.
    /// Line format: domain TAB project TAB relative image path TAB test instance id TAB uploaded(0/1)
    public static class AssignmentStore
    {
        public const string FileName = "ALM-assignments.txt";

        static string PathIn(string root) { return Path.Combine(root, FileName); }

        static IEnumerable<string[]> Read(string root)
        {
            var path = PathIn(root);
            if (!File.Exists(path)) yield break;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var f = line.Split('\t');
                if (f.Length >= 5 && !line.StartsWith("#")) yield return f;
            }
        }

        public static void Load(string root, string domain, string project, TestLabFolder lab, List<ImageItem> images)
        {
            var byRel = images.ToDictionary(i => i.Rel, StringComparer.OrdinalIgnoreCase);
            foreach (var f in Read(root))
            {
                ImageItem img;
                if (f[0] != domain || f[1] != project || !byRel.TryGetValue(f[2], out img)) continue;
                var inst = lab.FindInstance(f[3]);
                if (inst == null) continue;
                if (!img.Assigned.Contains(inst)) img.Assigned.Add(inst);
                if (f[4] == "1") img.Uploaded.Add(inst.Id);
            }
        }

        public static void Save(string root, string domain, string project, TestLabFolder lab, List<ImageItem> images)
        {
            // keep lines of other projects / other Test Lab folders
            var mine = new HashSet<string>(lab.Sets.SelectMany(s => s.Instances).Select(i => i.Id));
            var lines = new List<string> { "# ALM Image Uploader - which image goes to which test case. Safe to delete." };
            lines.AddRange(Read(root).Where(f => f[0] != domain || f[1] != project || !mine.Contains(f[3]))
                                     .Select(f => string.Join("\t", f)));
            foreach (var img in images)
                foreach (var inst in img.Assigned)
                    lines.Add(string.Join("\t", domain, project, img.Rel, inst.Id, img.Uploaded.Contains(inst.Id) ? "1" : "0"));
            File.WriteAllLines(PathIn(root), lines.ToArray(), Encoding.UTF8);
        }
    }

    // ------------------------------------------------------------------ uploader
    public class Uploader
    {
        readonly AlmClient client;
        readonly string imageRoot, moveTo;
        readonly Action<string> log;
        readonly Dictionary<string, HashSet<string>> existing = new Dictionary<string, HashSet<string>>();
        public readonly HashSet<string> Done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Ok, Skipped, Failed;

        public Uploader(AlmClient client, string imageRoot, string moveTo, Action<string> log)
        {
            this.client = client;
            this.imageRoot = imageRoot;
            this.moveTo = string.IsNullOrWhiteSpace(moveTo) ? null : moveTo;
            this.log = log;
        }

        public void Handle(PlanItem item)
        {
            Done.Add(item.File);
            if (item.Instance == null)
            {
                log("[SKIP] " + Util.RelPath(imageRoot, item.File) + " - " + item.Error);
                Skipped++;
                return;
            }
            if (UploadTo(item.File, item.Set, item.Instance))
                AfterUpload(item.File);
        }

        /// Attach one image to one test case. True when it is in ALM afterwards
        /// (uploaded now, or an attachment with the same name was already there).
        public bool UploadTo(string file, TestSet set, TestInstance inst)
        {
            var name = Path.GetFileName(file);
            var target = set.PathText + " > " + inst.Label;
            try
            {
                HashSet<string> names;
                if (!existing.TryGetValue(inst.Id, out names))
                    existing[inst.Id] = names = client.ListAttachmentNames("test-instances", inst.Id);
                if (names.Contains(name))
                {
                    log("[SKIP] " + name + " - already attached to " + target);
                    Skipped++;
                }
                else
                {
                    client.UploadAttachment("test-instances", inst.Id, file, name);
                    names.Add(name);
                    log("[ OK ] " + name + " -> " + target);
                    Ok++;
                }
                return true;
            }
            catch (Exception e)
            {
                if (!(e is AlmException || e is WebException || e is IOException || e is UnauthorizedAccessException))
                    throw;
                log("[FAIL] " + name + " -> " + target + ": " + e.Message);
                Failed++;
                return false;
            }
        }

        void AfterUpload(string file)
        {
            if (moveTo == null) return;
            // keep the <test set>\<test case> structure below the image folder
            var dest = Path.Combine(moveTo, Util.RelPath(imageRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(dest))
                dest = Path.Combine(Path.GetDirectoryName(dest),
                    Path.GetFileNameWithoutExtension(dest) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") +
                    Path.GetExtension(dest));
            File.Move(file, dest);
        }
    }
}
