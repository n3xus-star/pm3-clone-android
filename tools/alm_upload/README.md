# ALM Image Auto Upload

A script that uploads images (screenshots) automatically as attachments to
**HP ALM / Micro Focus ALM / OpenText ALM**. Its main job is attaching images
to **every test case in a Test Lab test set** (the `test-instances` entity).

It runs on a PC or laptop that can reach the ALM server. It is not part of the
Android app.

## Install

```bash
pip install -r requirements.txt
```

## Settings (once)

```bash
# Linux / macOS
export ALM_URL=https://alm.company.com/qcbin
export ALM_DOMAIN=DEFAULT
export ALM_PROJECT=MyProject
export ALM_USER=myuser
# ALM_PASSWORD is optional. If it is not set, the script asks for it.
```

On Windows (cmd), use `set ALM_URL=...` instead of `export`.

You can also pass these as flags: `--url --domain --project --user --password`.
To log in with an API key, use `--client-id` and `--secret` (or `ALM_CLIENT_ID` and `ALM_SECRET`).

## Test Lab: attach images to each test case

1. Check the test cases in the test set:

   ```bash
   python alm_upload.py --test-set "Sprint 12 Regression" --list
   ```

   (Use `--test-set-id 101` if you know the ID, or if the name is not unique.)

2. Arrange the images in one of these ways.

   **A. One folder per test case (recommended).** Name each folder after the test:

   ```
   shots/
     Login Test/
       step1.png
       step2.png
     Logout Test/
       result.png
   ```

   ```bash
   python alm_upload.py --test-set "Sprint 12 Regression" --folder shots --recursive
   ```

   **B. File name starts with the test name:** `Login Test_step1.png`, `Logout Test_1.jpg`

   ```bash
   python alm_upload.py --test-set "Sprint 12 Regression" --folder shots
   ```

   Name matching ignores case, and treats spaces, `_`, `-` and `.` as the same
   (`login_test` = `Login Test`).

   **C. File name starts with the Test Plan test ID:** `2345_step1.png`

   ```bash
   python alm_upload.py --test-set-id 101 --folder shots --match test-id
   ```

   **D. File name starts with the test instance ID:** `--match instance-id`

   **E. Same image on every test case:**

   ```bash
   python alm_upload.py --test-set-id 101 --match all evidence.png
   ```

## Auto mode (watch folder)

The script keeps running and uploads every new screenshot as soon as it lands
in the folder:

```bash
python alm_upload.py --test-set-id 101 --folder shots --recursive --watch --move-to uploaded
```

`--move-to uploaded` moves each file into the `uploaded` folder after its upload
succeeds. Press Ctrl+C to stop.

## Other useful options

| Option | Purpose |
|---|---|
| `--dry-run` | Show which images would go to which test case, without uploading |
| `--prefix "TC_"` | Add a prefix to the attachment name in ALM |
| `--allow-duplicates` | Upload even if an attachment with the same name already exists (skipped by default) |
| `--delete-after` | Delete the local file after it uploads |
| `--insecure` / `--ca-bundle file.pem` | For ALM servers with a self-signed certificate |

## Other entities

```bash
# defect 123
python alm_upload.py --entity defects --id 123 a.png b.png
# ID from the file name (123_error.png -> defect 123)
python alm_upload.py --entity defects --folder shots --id-from-filename
```

Supported entities: `defects, tests, test-instances, runs, run-steps, design-steps,
requirements, test-sets, test-folders, releases, release-cycles`.
