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
        }

        Response Send(string method, string url, byte[] body, string contentType,
                      Dictionary<string, string> headers, bool retryOn401)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.CookieContainer = cookies;
            req.Accept = "application/xml";
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
                using (var sr = new StreamReader(wr.GetResponseStream(), Encoding.UTF8))
                    resp.Body = sr.ReadToEnd();
            }

            if (resp.Status == 401 && retryOn401 && user != null)
            {
                // session expired (common while watching a folder): log in again, retry once
                Login(user, password);
                return Send(method, url, body, contentType, headers, false);
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

        public static string QueryValue(string name)
        {
            // ALM queries cannot escape quotes: use a wildcard and filter exactly afterwards
            return name.Replace("'", "*");
        }
    }

    // -------------------------------------------------------------------- helpers
    public static class Util
    {
        public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp" };

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

        public TestLabFolder(AlmClient client, string folder)
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
                                        new[] { "id", "test-id", "cycle-id", "test-order", "name" });
            var names = client.GetByIds("tests", "id", insts.Select(i => Get(i, "test-id")), new[] { "id", "name" })
                              .ToDictionary(t => t["id"], t => Get(t, "name"));
            foreach (var i in insts)
            {
                TestSet ts;
                if (!byId.TryGetValue(Get(i, "cycle-id"), out ts)) continue;
                string tn;
                names.TryGetValue(Get(i, "test-id"), out tn);
                ts.Instances.Add(new TestInstance
                {
                    Id = i["id"], TestId = Get(i, "test-id"), SetId = ts.Id, Name = Get(i, "name"),
                    TestName = tn ?? "", Order = Util.ToInt(Get(i, "test-order")),
                });
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
            var name = Path.GetFileName(item.File);
            var id = item.Instance.Id;
            var target = item.Set.PathText + " > " + item.Instance.Label;
            try
            {
                HashSet<string> names;
                if (!existing.TryGetValue(id, out names))
                    existing[id] = names = client.ListAttachmentNames("test-instances", id);
                if (names.Contains(name))
                {
                    log("[SKIP] " + name + " - already attached to " + target);
                    Skipped++;
                }
                else
                {
                    client.UploadAttachment("test-instances", id, item.File, name);
                    names.Add(name);
                    log("[ OK ] " + name + " -> " + target);
                    Ok++;
                }
                AfterUpload(item.File);
            }
            catch (Exception e)
            {
                if (!(e is AlmException || e is WebException || e is IOException || e is UnauthorizedAccessException))
                    throw;
                log("[FAIL] " + name + " -> " + target + ": " + e.Message);
                Failed++;
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
