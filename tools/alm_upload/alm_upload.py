#!/usr/bin/env python3
"""
alm_upload.py - Auto upload images as attachments to HP ALM / Micro Focus ALM /
OpenText ALM (Quality Center) through the ALM REST API.

Main use: attach screenshots to every test case (test instance) of every
test set under one Test Lab folder. Images are laid out as
<image folder>/<test set>/<test case>/*.png:

  # list test sets and test cases, then create the matching image folders
  alm_upload.py --lab-folder "Root\\Release 1\\Sprint 12" --list
  alm_upload.py --lab-folder "Root\\Release 1\\Sprint 12" --folder shots --make-folders

  # preview, then upload (and keep watching for new screenshots)
  alm_upload.py --lab-folder "Root\\Release 1\\Sprint 12" --folder shots --dry-run
  alm_upload.py --lab-folder "Root\\Release 1\\Sprint 12" --folder shots --watch

Single test set mode:

  # list the test cases in a test set
  alm_upload.py --test-set "Sprint 12 Regression" --list

  # one sub folder per test case, named like the test:
  #   shots/Login Test/step1.png, shots/Logout Test/step1.png ...
  alm_upload.py --test-set "Sprint 12 Regression" --folder shots --recursive

  # or file names starting with the test name / test id / instance id
  alm_upload.py --test-set-id 101 --folder shots                   # Login Test_1.png
  alm_upload.py --test-set-id 101 --folder shots --match test-id   # 2345_1.png
  alm_upload.py --test-set-id 101 --folder shots --match instance-id

  # same image(s) on every test case in the set
  alm_upload.py --test-set-id 101 --match all evidence.png

  # keep running and upload new screenshots as they appear
  alm_upload.py --test-set-id 101 --folder shots --recursive --watch

Other entities also work (defects, runs, tests, ...):
  alm_upload.py --entity defects --id 123 a.png b.jpg
  alm_upload.py --entity defects --folder shots --id-from-filename   # 123_x.png

Credentials come from CLI flags or environment variables:
  ALM_URL, ALM_DOMAIN, ALM_PROJECT, ALM_USER, ALM_PASSWORD
  ALM_CLIENT_ID, ALM_SECRET   (API key login, used instead of user/password)
If no password is given, it is prompted for interactively.
"""

import argparse
import getpass
import os
import re
import shutil
import sys
import time

try:
    import requests
except ImportError:
    sys.exit("This script needs 'requests'. Install it with: pip install requests")

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"}

# REST collection names accepted by ALM for attachments
ENTITIES = [
    "defects", "tests", "test-instances", "runs", "run-steps",
    "design-steps", "requirements", "test-sets", "test-folders",
    "releases", "release-cycles",
]


class AlmError(Exception):
    pass


class AlmClient:
    def __init__(self, base_url, domain, project, verify=True, timeout=60):
        base_url = base_url.rstrip("/")
        if not base_url.endswith("/qcbin"):
            base_url += "/qcbin"
        self.base = base_url
        self.domain = domain
        self.project = project
        self.timeout = timeout
        self.session = requests.Session()
        self.session.verify = verify
        self.session.headers["Accept"] = "application/json"
        self._login_args = None
        self._missing_fields = {}  # fields this ALM version does not have, per collection

    # ---------------------------------------------------------------- auth
    def login(self, user=None, password=None, client_id=None, secret=None):
        self._login_args = (user, password, client_id, secret)
        if client_id and secret:
            r = self.session.post(
                self.base + "/rest/oauth2/login",
                json={"clientId": client_id, "secret": secret},
                timeout=self.timeout,
            )
            self._check(r, "API key login")
            return

        # ALM 12.60+ (and OpenText ALM 15+/16+/24+)
        r = self.session.post(
            self.base + "/api/authentication/sign-in",
            auth=(user, password),
            timeout=self.timeout,
        )
        if r.status_code == 404:
            # Older ALM (11.x / 12.0x - 12.5x)
            r = self.session.post(
                self.base + "/authentication-point/alm-authenticate",
                data=(
                    "<alm-authentication><user>{}</user><password>{}</password>"
                    "</alm-authentication>"
                ).format(_xml_escape(user), _xml_escape(password)),
                headers={"Content-Type": "application/xml"},
                timeout=self.timeout,
            )
            self._check(r, "login")
            r = self.session.post(self.base + "/rest/site-session", timeout=self.timeout)
            if r.status_code not in (200, 201, 404):
                self._check(r, "site-session")
        else:
            self._check(r, "login")

    def logout(self):
        for path in ("/api/authentication/sign-out", "/authentication-point/logout"):
            try:
                r = self.session.get(self.base + path, timeout=self.timeout)
                if r.status_code < 400:
                    return
            except requests.RequestException:
                pass

    # ------------------------------------------------------------ requests
    def _headers(self, extra=None):
        h = {}
        xsrf = self.session.cookies.get("XSRF-TOKEN")
        if xsrf:
            h["X-XSRF-TOKEN"] = xsrf
        if extra:
            h.update(extra)
        return h

    def _request(self, method, url, **kw):
        kw.setdefault("timeout", self.timeout)
        kw["headers"] = self._headers(kw.get("headers"))
        r = self.session.request(method, url, **kw)
        if r.status_code == 401 and self._login_args:
            # Session expired (common in --watch mode): log in again and retry once
            self.login(*self._login_args)
            kw["headers"] = self._headers(kw.get("headers"))
            r = self.session.request(method, url, **kw)
        return r

    @staticmethod
    def _check(r, what):
        if r.status_code >= 400:
            raise AlmError("{} failed: HTTP {} {}".format(what, r.status_code, r.text[:500]))

    def _entity_url(self, entity, entity_id):
        return "{}/rest/domains/{}/projects/{}/{}/{}".format(
            self.base, self.domain, self.project, entity, entity_id
        )

    def get_entities(self, collection, query=None, fields=None, page_size=1000):
        """Return all entities of a collection as a list of {field: value} dicts."""
        url = "{}/rest/domains/{}/projects/{}/{}".format(
            self.base, self.domain, self.project, collection)
        missing = self._missing_fields.setdefault(collection, set())
        out, start = [], 1
        while True:
            use = [f for f in fields if f not in missing] if fields else None
            params = {"page-size": page_size, "start-index": start}
            if query:
                params["query"] = query
            if use:
                params["fields"] = ",".join(use)
            r = self._request("GET", url, params=params)
            m = re.search(r"field named:?\s*'([^']+)'", r.text or "", re.I) if r.status_code == 400 else None
            if m and use and m.group(1) in use and m.group(1) != "id":
                # e.g. ALM 25.x: "test-instance doesn't have a field named: 'test-order'" -> drop it, restart
                missing.add(m.group(1))
                out, start = [], 1
                continue
            self._check(r, "read " + collection)
            data = r.json()
            batch = [_fields(e) for e in data.get("entities", [])]
            out += batch
            total = int(data.get("TotalResults", len(out)))
            if not batch or len(out) >= total:
                return out
            start += len(batch)

    def get_by_ids(self, collection, field, ids, fields=None, chunk=50):
        """get_entities() for `field` in `ids`, split in chunks to keep URLs short."""
        ids = sorted({str(i) for i in ids if i not in (None, "")})
        out = []
        for n in range(0, len(ids), chunk):
            query = "{{{}[{}]}}".format(field, " OR ".join(ids[n:n + chunk]))
            out += self.get_entities(collection, query=query, fields=fields)
        return out

    # ------------------------------------------------------- attachments
    def list_attachment_names(self, entity, entity_id):
        r = self._request("GET", self._entity_url(entity, entity_id) + "/attachments")
        self._check(r, "list attachments of {} {}".format(entity, entity_id))
        names = set()
        try:
            data = r.json()
        except ValueError:
            return names
        for ent in data.get("entities", []):
            name = _fields(ent).get("name")
            if name:
                names.add(name)
        return names

    def upload_attachment(self, entity, entity_id, path, name=None):
        name = name or os.path.basename(path)
        with open(path, "rb") as fh:
            r = self._request(
                "POST",
                self._entity_url(entity, entity_id) + "/attachments",
                data=fh,
                headers={"Content-Type": "application/octet-stream", "Slug": name},
            )
        self._check(r, "upload {} to {} {}".format(name, entity, entity_id))
        return r


def _fields(entity):
    d = {}
    for field in entity.get("Fields", []):
        vals = field.get("values") or [{}]
        d[field.get("Name")] = vals[0].get("value")
    return d


def _xml_escape(s):
    return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


# ------------------------------------------------------------------ helpers
def is_image(path):
    return os.path.isfile(path) and os.path.splitext(path)[1].lower() in IMAGE_EXTS


def list_images(folder, recursive):
    found = []
    if recursive:
        for root, _dirs, files in os.walk(folder):
            found += [os.path.join(root, f) for f in files]
    else:
        found = [os.path.join(folder, f) for f in os.listdir(folder)]
    return sorted(p for p in found if is_image(p))


def file_is_stable(path, wait=1.0):
    """Avoid uploading a screenshot that is still being written."""
    try:
        size1 = os.path.getsize(path)
        time.sleep(wait)
        size2 = os.path.getsize(path)
    except OSError:
        return False
    return size1 == size2 and size1 > 0


def _norm(s):
    """Lower-case and collapse separators so 'Login_Test' matches 'Login Test'.

    Characters Windows does not allow in folder names count as separators too,
    so a test called 'Login: admin/user' matches the folder 'Login_ admin_user'.
    """
    return re.sub(r'[\s_\-.\\/:*?"<>|]+', " ", (s or "").lower()).strip()


def safe_folder_name(name):
    """Folder name Windows accepts, that still matches `name` via _norm()."""
    name = re.sub(r'[\\/:*?"<>|]', "_", name or "").strip().rstrip(". ")
    return name or "_"


def _query_value(name):
    # ALM query strings cannot escape quotes; use a wildcard and filter exactly later
    return name.replace("'", "*")


# ------------------------------------------------------------- Test Lab
class TestLab:
    """Test cases (test instances) inside one Test Lab test set."""

    def __init__(self, client, test_set_id):
        self.test_set_id = str(test_set_id)
        self.instances = client.get_entities(
            "test-instances",
            query="{{cycle-id[{}]}}".format(self.test_set_id),
            fields=["id", "test-id", "test-order", "name"],
        )
        if not self.instances:
            raise AlmError("test set {} has no test cases".format(self.test_set_id))

        # Test names come from the Test Plan entity (instance "name" is like "[1]Login")
        test_ids = sorted({i["test-id"] for i in self.instances if i.get("test-id")})
        test_names = {}
        for n in range(0, len(test_ids), 100):
            chunk = test_ids[n:n + 100]
            for t in client.get_entities(
                "tests", query="{{id[{}]}}".format(" OR ".join(chunk)), fields=["id", "name"]
            ):
                test_names[t["id"]] = t.get("name") or ""
        for inst in self.instances:
            inst["test-name"] = test_names.get(inst.get("test-id"), "")

        self.by_id = {i["id"]: i for i in self.instances}
        self.by_test_id = {}
        for i in self.instances:
            self.by_test_id.setdefault(i.get("test-id"), []).append(i["id"])
        # longest names first so "Login Admin" wins over "Login"
        self.by_name = sorted(
            ((_norm(i["test-name"]), i["id"]) for i in self.instances if i["test-name"]),
            key=lambda x: -len(x[0]),
        )

    @staticmethod
    def find_test_set(client, name):
        sets = client.get_entities(
            "test-sets", query="{{name['{}']}}".format(_query_value(name)),
            fields=["id", "name", "parent-id"],
        )
        sets = [x for x in sets if (x.get("name") or "").lower() == name.lower()]
        if not sets:
            raise AlmError("test set '{}' not found".format(name))
        if len(sets) > 1:
            raise AlmError("several test sets named '{}' (ids: {}); use --test-set-id".format(
                name, ", ".join(s["id"] for s in sets)))
        return sets[0]["id"]

    def describe(self):
        lines = ["Test set {} has {} test case(s):".format(self.test_set_id, len(self.instances))]
        for i in sorted(self.instances, key=lambda x: int(x.get("test-order") or 0)):
            lines.append("  instance {:>6}  test-id {:>6}  {}".format(
                i["id"], i.get("test-id") or "-", i["test-name"]))
        return "\n".join(lines)

    def match(self, path, mode, id_regex):
        """Return the test-instance ids this image belongs to."""
        if mode == "all":
            return [i["id"] for i in self.instances]
        if mode == "name":
            # sub folder named after the test (shots/Login Test/1.png) or
            # file name starting with the test name (Login Test_step1.png)
            candidates = [_norm(os.path.basename(os.path.dirname(path))),
                          _norm(os.path.splitext(os.path.basename(path))[0])]
            for cand in candidates:
                for name, inst_id in self.by_name:
                    if cand == name or cand.startswith(name + " "):
                        return [inst_id]
            return []
        m = id_regex.search(os.path.basename(path))
        if not m:
            return []
        if mode == "test-id":
            return self.by_test_id.get(m.group(1), [])
        return [m.group(1)] if m.group(1) in self.by_id else []  # mode == "instance-id"


class TestLabFolder:
    """Every test set and test case under one Test Lab folder (and its sub folders).

    Local images are expected as <image root>/<test set>/<test case>/<image>.
    When two test sets share a name, the sub folder path from the chosen Test Lab
    folder is used instead: <image root>/<sub folder>/<test set>/<test case>/.
    A test that is in a set more than once uses its instance name, e.g. "[2]Login".
    """

    def __init__(self, client, folder):
        self.client = client
        self.folder_id = self._resolve_folder(str(folder).strip())

        # all sub folders: id -> path (tuple of names) relative to the chosen folder
        paths = {self.folder_id: ()}
        frontier = [self.folder_id]
        while frontier:
            children = client.get_by_ids("test-set-folders", "parent-id", frontier,
                                         fields=["id", "name", "parent-id"])
            frontier = []
            for c in children:
                if c["id"] not in paths and c.get("parent-id") in paths:
                    paths[c["id"]] = paths[c["parent-id"]] + (c.get("name") or "",)
                    frontier.append(c["id"])

        self.sets = []
        for ts in client.get_by_ids("test-sets", "parent-id", list(paths),
                                    fields=["id", "name", "parent-id"]):
            if ts.get("parent-id") in paths:
                ts["path"] = paths[ts["parent-id"]] + (ts.get("name") or "",)
                ts["instances"] = []
                self.sets.append(ts)
        self.sets.sort(key=lambda x: [p.lower() for p in x["path"]])
        by_set = {ts["id"]: ts for ts in self.sets}

        instances = client.get_by_ids(
            "test-instances", "cycle-id", list(by_set),
            fields=["id", "test-id", "cycle-id", "test-order", "name"])
        names = {t["id"]: t.get("name") or "" for t in client.get_by_ids(
            "tests", "id", [i.get("test-id") for i in instances], fields=["id", "name"])}
        for inst in instances:
            inst["test-name"] = names.get(inst.get("test-id"), "")
            if inst.get("cycle-id") in by_set:
                by_set[inst["cycle-id"]]["instances"].append(inst)
        for ts in self.sets:
            # 'test-order' / 'name' are missing on some ALM versions: fall back to the id order
            ts["instances"].sort(key=lambda i: (int(i.get("test-order") or 0), int(i["id"])))
            counts, seen = {}, {}
            for i in ts["instances"]:
                counts[i.get("test-id")] = counts.get(i.get("test-id"), 0) + 1
            for i in ts["instances"]:
                seen[i.get("test-id")] = seen.get(i.get("test-id"), 0) + 1
                if not i.get("name"):
                    i["name"] = "[{}]{}".format(seen[i.get("test-id")], i["test-name"])  # ALM's own naming
                i["repeated"] = counts[i.get("test-id")] > 1
                i["label"] = i["name"] if i["repeated"] else i["test-name"]

        # local folder keys -> test sets: full sub folder path (exact) or set name alone
        self.path_index = {tuple(_norm(p) for p in ts["path"]): ts for ts in self.sets}
        self.name_index = {}
        for ts in self.sets:
            self.name_index.setdefault(_norm(ts["name"]), []).append(ts)

    # ------------------------------------------------------------ folders
    def _resolve_folder(self, folder):
        if folder.isdigit():
            return folder
        parts = [p for p in re.split(r"[\\/]+", folder) if p.strip()]
        if parts and parts[0].strip().lower() == "root":
            parts = parts[1:]
        if not parts:
            raise AlmError("choose a Test Lab folder below Root, e.g. Root\\Release 1")

        def named(name, parent_ids=None):
            q = "{{name['{}']}}".format(_query_value(name))
            found = self.client.get_entities("test-set-folders", query=q,
                                             fields=["id", "name", "parent-id"])
            return [f for f in found if (f.get("name") or "").lower() == name.lower()
                    and (parent_ids is None or f.get("parent-id") in parent_ids)]

        candidates = named(parts[0].strip())
        if len(candidates) > 1:
            # prefer the one directly under Root
            parents = {f["id"]: f for f in self.client.get_by_ids(
                "test-set-folders", "id", [c.get("parent-id") for c in candidates],
                fields=["id", "name"])}
            top = [c for c in candidates
                   if (parents.get(c.get("parent-id"), {}).get("name") or "").lower() == "root"]
            candidates = top or candidates
        for part in parts[1:]:
            candidates = named(part.strip(), {c["id"] for c in candidates})
        if not candidates:
            raise AlmError("Test Lab folder not found: " + folder)
        if len(candidates) > 1:
            raise AlmError("Test Lab folder '{}' is ambiguous (ids: {}); enter the folder id".format(
                folder, ", ".join(c["id"] for c in candidates)))
        return candidates[0]["id"]

    @property
    def instance_count(self):
        return sum(len(ts["instances"]) for ts in self.sets)

    def local_set_path(self, ts):
        """Relative local folder parts for a test set (name alone when unique)."""
        if len(self.name_index[_norm(ts["name"])]) == 1:
            return (ts["name"],)
        return ts["path"]

    def make_folders(self, image_root):
        """Create <image root>/<test set>/<test case> folders. Returns number created."""
        made = 0
        for ts in self.sets:
            base = os.path.join(image_root, *[safe_folder_name(p) for p in self.local_set_path(ts)])
            for inst in ts["instances"]:
                path = os.path.join(base, safe_folder_name(inst["label"]))
                if not os.path.isdir(path):
                    os.makedirs(path)
                    made += 1
        return made

    # ------------------------------------------------------------ matching
    def resolve(self, image_root, path):
        """Return (test set, test instance, error) for one local image."""
        rel = os.path.relpath(path, image_root)
        parts = rel.split(os.sep)
        dirs = [_norm(d) for d in parts[:-1]]
        if not dirs:
            return None, None, "image must be inside a <test set>\\<test case> folder"
        for k in range(len(dirs), 0, -1):
            exact = self.path_index.get(tuple(dirs[:k]))
            sets = [exact] if exact else (self.name_index.get(dirs[0], []) if k == 1 else [])
            if not sets:
                continue
            if len(sets) > 1:
                return None, None, "{} test sets are named '{}'; put the images under {}".format(
                    len(sets), parts[k - 1], " or ".join(
                        "\\".join(ts["path"]) for ts in sets))
            ts = sets[0]
            if k < len(dirs):
                inst, err = self._find_instance(ts, dirs[k], parts[k])
            else:
                inst, err = self._find_by_file_name(ts, parts[-1])
            return ts, inst, err
        return None, None, "no test set named '{}' in this Test Lab folder".format(parts[0])

    @staticmethod
    def _find_instance(ts, key, shown):
        insts = ts["instances"]
        for match in (
            [i for i in insts if key.isdigit() and i["id"] == key],
            [i for i in insts if i.get("name") and _norm(i["name"]) == key],
            [i for i in insts if _norm(i["test-name"]) == key],
        ):
            if len(match) == 1:
                return match[0], None
            if len(match) > 1:
                return None, "'{}' is in test set '{}' {} times; name the folder like '{}'".format(
                    shown, ts["name"], len(match), match[0].get("name") or "[1]" + shown)
        return None, "no test case '{}' in test set '{}'".format(shown, ts["name"])

    @staticmethod
    def _find_by_file_name(ts, file_name):
        stem = _norm(os.path.splitext(file_name)[0])
        best = None
        for i in ts["instances"]:
            key = _norm(i["label"])
            if key and (stem == key or stem.startswith(key + " ")):
                if best is None or len(key) > len(_norm(best["label"])):
                    best = i
        if best is None:
            return None, "put it in a test case folder inside '{}'".format(ts["name"])
        if best["repeated"] and not (best.get("name") and stem.startswith(_norm(best["name"]))):
            return None, "test '{}' is in the set more than once; use a '{}' folder".format(
                best["test-name"], best.get("name") or "[1]" + best["test-name"])
        return best, None

    def plan(self, image_root, recursive=True):
        """Resolve every image under image_root: list of dicts path/set/instance/error."""
        return [dict(zip(("set", "instance", "error"), self.resolve(image_root, p)), path=p)
                for p in list_images(image_root, recursive)]


# -------------------------------------------------------------- uploader
class Uploader:
    def __init__(self, client, args, resolver, log=print):
        self.client = client
        self.args = args
        self.resolver = resolver  # path -> list of entity ids
        self.log = log
        self.existing = {}  # entity_id -> set of attachment names already in ALM
        self.done = set()   # local paths handled in this run
        self.ok = 0
        self.failed = 0
        self.skipped = 0

    def _existing_names(self, entity_id):
        if entity_id not in self.existing:
            self.existing[entity_id] = self.client.list_attachment_names(
                self.args.entity, entity_id
            )
        return self.existing[entity_id]

    def handle(self, path):
        self.done.add(path)
        ids = self.resolver(path)  # list of ids, or an error message
        if not ids or isinstance(ids, str):
            self.log("[SKIP] {} - {}".format(
                path, ids or "no matching {} found".format(self.args.entity)))
            self.skipped += 1
            return
        name = self.args.prefix + os.path.basename(path)
        all_ok = True
        for entity_id in ids:
            all_ok &= self._upload_one(path, name, entity_id)
        if all_ok:
            self._after_upload(path)

    def _upload_one(self, path, name, entity_id):
        args = self.args
        target = "{} {}".format(args.entity, entity_id)
        try:
            if not args.allow_duplicates and name in self._existing_names(entity_id):
                self.log("[SKIP] {} - already attached to {}".format(name, target))
                self.skipped += 1
                return True
            if args.dry_run:
                self.log("[DRY ] {} -> {}".format(path, target))
                self.ok += 1
                return True
            self.client.upload_attachment(args.entity, entity_id, path, name)
            self._existing_names(entity_id).add(name)
            self.log("[ OK ] {} -> {}".format(name, target))
            self.ok += 1
            return True
        except (AlmError, requests.RequestException, OSError) as e:
            self.log("[FAIL] {} -> {}: {}".format(name, target, e))
            self.failed += 1
            return False

    def _after_upload(self, path):
        if self.args.dry_run:
            return
        if self.args.move_to:
            # keep the <test set>/<test case> structure below the image folder
            root = getattr(self.args, "folder", None)
            rel = os.path.relpath(path, root) if root else os.path.basename(path)
            if rel.startswith(os.pardir):
                rel = os.path.basename(path)
            dest = os.path.join(self.args.move_to, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            if os.path.exists(dest):
                base, ext = os.path.splitext(dest)
                dest = "{}_{}{}".format(base, int(time.time()), ext)
            shutil.move(path, dest)
        elif self.args.delete_after:
            os.remove(path)


def parse_args(argv=None):
    p = argparse.ArgumentParser(
        description="Auto upload images as attachments to HP / Micro Focus / OpenText ALM.",
    )
    p.add_argument("files", nargs="*", help="Image files to upload")
    p.add_argument("--url", default=os.environ.get("ALM_URL"),
                   help="ALM server, e.g. https://alm.company.com/qcbin (env ALM_URL)")
    p.add_argument("--domain", default=os.environ.get("ALM_DOMAIN"), help="env ALM_DOMAIN")
    p.add_argument("--project", default=os.environ.get("ALM_PROJECT"), help="env ALM_PROJECT")
    p.add_argument("--user", default=os.environ.get("ALM_USER"), help="env ALM_USER")
    p.add_argument("--password", default=os.environ.get("ALM_PASSWORD"),
                   help="env ALM_PASSWORD (prompted if missing)")
    p.add_argument("--client-id", default=os.environ.get("ALM_CLIENT_ID"),
                   help="API key client id (env ALM_CLIENT_ID)")
    p.add_argument("--secret", default=os.environ.get("ALM_SECRET"),
                   help="API key secret (env ALM_SECRET)")

    lf = p.add_argument_group(
        "Test Lab folder (attach to test cases of every test set in a folder)",
        "Images go in <--folder>/<test set>/<test case>/*.png")
    lf.add_argument("--lab-folder",
                    help=r"Test Lab folder path (e.g. 'Root\Release 1\Sprint 12') or folder id")
    lf.add_argument("--make-folders", action="store_true",
                    help="Create the <test set>/<test case> folders inside --folder and exit")

    tl = p.add_argument_group("Single test set (attach to test cases in one test set)")
    tl.add_argument("--test-set-id", help="Test Lab test set id (cycle id)")
    tl.add_argument("--test-set", help="Test Lab test set name (must be unique)")
    tl.add_argument("--match", default="name",
                    choices=["name", "test-id", "instance-id", "all"],
                    help="How an image is matched to a test case: "
                         "name = sub folder or file name starts with the test name (default); "
                         "test-id = file name starts with the Test Plan test id; "
                         "instance-id = file name starts with the test instance id; "
                         "all = attach every image to every test case in the set")
    tl.add_argument("--list", action="store_true",
                    help="Only list the test sets / test cases and exit")

    ot = p.add_argument_group("Other entities")
    ot.add_argument("--entity", choices=ENTITIES,
                    help="Entity type (default: test-instances with a test set, else defects)")
    ot.add_argument("--id", help="Attach every image to this one entity id")
    ot.add_argument("--id-from-filename", action="store_true",
                    help="Take the entity id from each file name (see --id-regex)")

    p.add_argument("--id-regex", default=r"^(\d+)",
                   help=r"Regex whose group 1 is the id (default: ^(\d+), e.g. 1234_step1.png)")
    p.add_argument("--folder", help="Upload every image in this folder")
    p.add_argument("--recursive", action="store_true",
                   help="Include sub folders (needed for one-folder-per-test layout)")
    p.add_argument("--watch", action="store_true",
                   help="Keep watching --folder and upload new images automatically")
    p.add_argument("--interval", type=float, default=5.0,
                   help="Watch polling interval in seconds (default: 5)")
    p.add_argument("--move-to", help="Move each uploaded file into this folder")
    p.add_argument("--delete-after", action="store_true", help="Delete local file after upload")
    p.add_argument("--prefix", default="", help="Prefix added to the attachment name in ALM")
    p.add_argument("--allow-duplicates", action="store_true",
                   help="Upload even if an attachment with the same name exists")
    p.add_argument("--dry-run", action="store_true", help="Show what would be uploaded")

    p.add_argument("--insecure", action="store_true", help="Skip SSL certificate check")
    p.add_argument("--ca-bundle", help="Path to a CA bundle for the ALM server certificate")
    args = p.parse_args(argv)

    args.testlab = bool(args.test_set_id or args.test_set)
    if args.entity is None:
        args.entity = "test-instances" if (args.testlab or args.lab_folder) else "defects"

    missing = [n for n in ("url", "domain", "project") if not getattr(args, n)]
    if missing:
        p.error("missing: " + ", ".join("--" + m for m in missing))
    if not (args.client_id and args.secret) and not args.user:
        p.error("give --user (and password) or --client-id/--secret")
    if args.lab_folder:
        if args.entity != "test-instances" or args.testlab or args.id or args.id_from_filename:
            p.error("--lab-folder cannot be combined with --entity/--test-set/--id options")
        if args.files:
            p.error("with --lab-folder give the image root as --folder, not single files")
        if not args.folder and not args.list:
            p.error("--lab-folder needs --folder (the local image root)")
        args.recursive = True
    elif args.make_folders:
        p.error("--make-folders needs --lab-folder")
    elif args.testlab:
        if args.entity != "test-instances":
            p.error("--test-set/--test-set-id only works with --entity test-instances")
        if args.id or args.id_from_filename:
            p.error("use --match instead of --id/--id-from-filename with a test set")
    elif args.list:
        p.error("--list needs --lab-folder, --test-set or --test-set-id")
    elif not args.id and not args.id_from_filename:
        p.error("give --lab-folder, --test-set/--test-set-id, --id or --id-from-filename")
    if args.make_folders and not args.folder:
        p.error("--make-folders needs --folder")
    if not args.list and not args.make_folders:
        if not args.files and not args.folder:
            p.error("give image files or --folder")
        if args.watch and not args.folder:
            p.error("--watch needs --folder")
        if args.folder and not os.path.isdir(args.folder):
            p.error("folder not found: " + args.folder)
        if args.move_to and args.folder and args.recursive and \
                os.path.abspath(args.move_to).startswith(os.path.abspath(args.folder) + os.sep):
            p.error("--move-to must not be inside --folder when using --recursive")
    return args


def describe_lab_folder(lab, image_root=None):
    counts = {}
    if image_root and os.path.isdir(image_root):
        for item in lab.plan(image_root):
            if item["instance"]:
                counts[item["instance"]["id"]] = counts.get(item["instance"]["id"], 0) + 1
    lines = ["{} test set(s), {} test case(s):".format(len(lab.sets), lab.instance_count)]
    for ts in lab.sets:
        lines.append("  [{}] {}".format(ts["id"], "\\".join(ts["path"])))
        for i in ts["instances"]:
            extra = "  ({} image(s))".format(counts.get(i["id"], 0)) if image_root else ""
            lines.append("      {:>6}  {}{}".format(i["id"], i["label"], extra))
    return "\n".join(lines)


def build_resolver(client, args):
    id_regex = re.compile(args.id_regex)
    if args.lab_folder:
        lab = TestLabFolder(client, args.lab_folder)
        print(describe_lab_folder(lab, args.folder))

        def by_folder(path):
            _ts, inst, err = lab.resolve(args.folder, path)
            return [inst["id"]] if inst else err
        return lab, by_folder
    if args.testlab:
        set_id = args.test_set_id or TestLab.find_test_set(client, args.test_set)
        lab = TestLab(client, set_id)
        print(lab.describe())
        return lab, lambda path: lab.match(path, args.match, id_regex)
    if args.id_from_filename:
        def by_name(path):
            m = id_regex.search(os.path.basename(path))
            return [m.group(1)] if m else []
        return None, by_name
    return None, lambda path: [args.id]


def main(argv=None):
    args = parse_args(argv)
    if not (args.client_id and args.secret) and not args.password:
        args.password = getpass.getpass("ALM password for {}: ".format(args.user))

    verify = False if args.insecure else (args.ca_bundle or True)
    if args.insecure:
        requests.packages.urllib3.disable_warnings()

    client = AlmClient(args.url, args.domain, args.project, verify=verify)
    try:
        client.login(args.user, args.password, args.client_id, args.secret)
    except (AlmError, requests.RequestException) as e:
        sys.exit("Login failed: {}".format(e))
    print("Logged in to {} ({}/{})".format(client.base, args.domain, args.project))

    up = None
    try:
        try:
            lab, resolver = build_resolver(client, args)
            if args.make_folders:
                made = lab.make_folders(args.folder)
                print("Created {} test case folder(s) in {}".format(made, args.folder))
        except (AlmError, requests.RequestException, OSError) as e:
            print("Error: {}".format(e))
            return 1
        if args.list or args.make_folders:
            return 0

        up = Uploader(client, args, resolver)
        for f in args.files:
            if is_image(f):
                up.handle(f)
            else:
                print("[SKIP] {} - not an image file".format(f))
                up.skipped += 1

        if args.folder:
            for f in list_images(args.folder, args.recursive):
                up.handle(f)

        if args.watch:
            print("Watching {} every {}s (Ctrl+C to stop)...".format(args.folder, args.interval))
            while True:
                time.sleep(args.interval)
                for f in list_images(args.folder, args.recursive):
                    if f not in up.done and file_is_stable(f):
                        up.handle(f)
    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        client.logout()

    if up is None:
        return 1
    print("Done: {} uploaded, {} skipped, {} failed".format(up.ok, up.skipped, up.failed))
    return 1 if up.failed else 0


if __name__ == "__main__":
    sys.exit(main())
