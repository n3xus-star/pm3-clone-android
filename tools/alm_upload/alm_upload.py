#!/usr/bin/env python3
"""
alm_upload.py - Auto upload images as attachments to HP ALM / Micro Focus ALM /
OpenText ALM (Quality Center) through the ALM REST API.

Main use: attach screenshots to every test case (test instance) in a
Test Lab test set.

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
        out, start = [], 1
        while True:
            params = {"page-size": page_size, "start-index": start}
            if query:
                params["query"] = query
            if fields:
                params["fields"] = ",".join(fields)
            r = self._request("GET", url, params=params)
            self._check(r, "read " + collection)
            data = r.json()
            batch = [_fields(e) for e in data.get("entities", [])]
            out += batch
            total = int(data.get("TotalResults", len(out)))
            if not batch or len(out) >= total:
                return out
            start += len(batch)

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
    """Lower-case and collapse separators so 'Login_Test' matches 'Login Test'."""
    return re.sub(r"[\s_\-.]+", " ", (s or "").lower()).strip()


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
            "test-sets", query="{{name['{}']}}".format(name), fields=["id", "name", "parent-id"]
        )
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


# -------------------------------------------------------------- uploader
class Uploader:
    def __init__(self, client, args, resolver):
        self.client = client
        self.args = args
        self.resolver = resolver  # path -> list of entity ids
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
        ids = self.resolver(path)
        if not ids:
            print("[SKIP] {} - no matching {} found".format(path, self.args.entity))
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
                print("[SKIP] {} - already attached to {}".format(name, target))
                self.skipped += 1
                return True
            if args.dry_run:
                print("[DRY ] {} -> {}".format(path, target))
                self.ok += 1
                return True
            self.client.upload_attachment(args.entity, entity_id, path, name)
            self._existing_names(entity_id).add(name)
            print("[ OK ] {} -> {}".format(name, target))
            self.ok += 1
            return True
        except (AlmError, requests.RequestException, OSError) as e:
            print("[FAIL] {} -> {}: {}".format(name, target, e))
            self.failed += 1
            return False

    def _after_upload(self, path):
        if self.args.dry_run:
            return
        if self.args.move_to:
            os.makedirs(self.args.move_to, exist_ok=True)
            dest = os.path.join(self.args.move_to, os.path.basename(path))
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

    tl = p.add_argument_group("Test Lab (attach to test cases in a test set)")
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
                    help="Only list the test cases in the test set and exit")

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
        args.entity = "test-instances" if args.testlab else "defects"

    missing = [n for n in ("url", "domain", "project") if not getattr(args, n)]
    if missing:
        p.error("missing: " + ", ".join("--" + m for m in missing))
    if not (args.client_id and args.secret) and not args.user:
        p.error("give --user (and password) or --client-id/--secret")
    if args.testlab:
        if args.entity != "test-instances":
            p.error("--test-set/--test-set-id only works with --entity test-instances")
        if args.id or args.id_from_filename:
            p.error("use --match instead of --id/--id-from-filename with a test set")
    elif args.list:
        p.error("--list needs --test-set or --test-set-id")
    elif not args.id and not args.id_from_filename:
        p.error("give --test-set/--test-set-id, --id or --id-from-filename")
    if not args.list:
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


def build_resolver(client, args):
    id_regex = re.compile(args.id_regex)
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
            _lab, resolver = build_resolver(client, args)
        except (AlmError, requests.RequestException) as e:
            print("Error: {}".format(e))
            return 1
        if args.list:
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
