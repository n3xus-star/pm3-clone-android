# ALM Image Uploader - changelog

## 2.16.0
- Change by defect: new "Delete defect comments..." for the defect IDs in the box. It splits
  each defect's Comments into its comments ("____" line, "user, date:" and the text) and lists
  them with date, author and text. Tick a date to tick all its comments, filter by author
  (changing the filter never changes what is ticked), or tick / untick single comments.
  Only the ticked comments are removed; the others stay as they are. Before deleting, a copy
  of each defect's Comments is saved in Documents\ALM Image Uploader\Comment backups, the
  Comments are read again (comments changed meanwhile are left alone), and afterwards they
  are read back to check the result.

## 2.15.0
- Change by defect, "Test case attachments in comment": pictures read from ALM only showed
  where the viewer had an ALM REST login ("Authentication failed. Browser based
  integrations ..."). New default "embedded pictures": a small JPEG of each picture (at most
  400 x 400 px and 30 KB, which the ALM desktop client shows) is stored in the comment
  itself, so it shows without logging in. "pictures from ALM" and "links only" are still
  there. After writing, the comment is read back; if ALM removed the pictures the result
  and the log say so.
- Every attachment link now ends with "login-form-required=y": opened without an ALM login,
  ALM shows its login form first and then the file.

## 2.14.0
- Change by defect: new "Defect Assigned To" in step 2. Choose a user of the project (the
  list shows "Full Name (login)"; typing finds them) to set on every defect. An imported list
  with an "Assigned To" column gives each defect its own user. Unknown users are shown in
  red and not written; defects that already have that user are left alone. The Defect update
  column shows "assign: old -> new" and the confirmation lists it.

## 2.13.1
- Change by defect: attachment links in the defect comment opened the attachment's details
  (XML) instead of the file on ALM 25.1. The program now tries six link formats the way a
  browser and the ALM desktop client open a link, and uses the first one that returns the
  file itself (checked against a real download). The log says which one, or, when none
  works, what ALM returned for each.

## 2.13.0
- Change by defect: "Test case attachments in the comment" (was: links) with "as pictures".
  The pictures of the ticked test cases are shown in the defect's comment, small (at most
  400 x 400 px, same proportions; click for the full size), with their names under them;
  other files (Excel, logs, ...) stay links. The pictures are read straight from the test
  cases' attachments in ALM, so the comment stays small.

## 2.12.0
- Change by defect: "Add links to the test cases' attachments in the comment". The comment
  added to each defect lists the attachments of its ticked test cases, grouped by test case
  ("<test set path> > <test case> (test case <id>)"), each a link to the original file in
  ALM (opens in a browser that is logged in to ALM). Works with or without comment text.
  Links already in the defect's Comments are not added again; a later Apply only adds the
  links that are new.

## 2.11.0
- Change by defect: "Copy the test cases' own attachments to their defects" (optionally
  images only). The attachments already on the ticked test cases are copied to the defects
  linked to them; names the defect already has are skipped, so it never copies twice. When
  two test cases have a file with the same name, the test case name goes in front
  ("1.1_Land_Screen - Step 2.jpg"), numbered if that is still the same.

## 2.10.0
- Change by defect, new step "Comment & attachments":
  - a comment typed once is added to the Comments of every defect (under a line, with your
    user name and the date, like ALM does; what was there stays). The same comment is not
    added twice.
  - attachments: files for all, plus a "defect folder" whose sub folders are named after the
    defect IDs (e.g. ...\3798\) with files for that defect only. They are uploaded to the
    ticked test cases and/or to the defects ("upload to: test cases / defects"); names that
    are already there are skipped.
  - the list shows the files per test case and a "Defect update" column; the confirmation
    lists the test cases, the defects, the comment and the file names.
  - a defect is updated when at least one of its test cases is ticked.

## 2.9.1
- Change by defect: fixed "HTTP 500 The source ids ... is not a comma separated list of
  numbers" on ALM 25.1. The links are now asked for as "{first-endpoint-id[1,2,3]}" (with
  "1 OR 2" and one defect at a time as fallbacks). A defect whose links cannot be read is
  shown in red with the reason instead of stopping the whole search.

## 2.9.0
- New "Change by defect..." (top of step 3): change the test cases linked to defects.
  - Type or paste many defect IDs, or import a list (Excel, CSV or text). List columns named
    after a field (e.g. Status, Comments) give each defect its own value.
  - Finds the test cases anywhere in the project's Test Lab: linked directly to the defect,
    or through a run / run step of the test case. Defects that are not found or have no
    linked test case are shown in red.
  - Change any editable test case field, several at once (e.g. Status = Passed and
    Comments = NA). The list shows every change (old -> new) with a tick box per test case,
    and Apply asks for confirmation, listing the changes.
  - The status is set directly, or through a run when ALM does not allow that.

## 2.8.0
- Download attachments: third "Folders" choice "<test set>_<test case>_1.png" puts every
  image in the chosen folder itself, with no sub folders at all. When two test sets share a
  name, the file name starts with their folder path ("Smoke_Dup_Login Test_1.png").
  Loading that folder assigns every image to its test case again.

## 2.7.0
- Download attachments: new "Folders" choice "<test set>\<test set>_<test case>_1.png"
  (images only, one folder per test set, no test case folders). Each image is named after
  its test set and test case. Loading that folder assigns every image to its test case
  again, ready to upload.

## 2.6.0
- The left side has two tabs: "Images" (as before) and "Attachments", which has two views:
  - "In the image folder": the other files of the folder (Excel, PDF, Word, logs, videos,
    ...), assigned and uploaded like images (tick many test cases, suggestions, upload);
    instead of a picture it shows the file's type, size and date, with "Open file";
  - "In ALM": the attachments already on the ticked test cases (or the selected test set /
    test case) with picture preview, Open, Download (as <test set>\<test case>\file),
    "Copy to ticked test cases" (same names skipped) and "Delete from ALM" (asks first and
    lists every file).
- The summary and the test tree count images and other files separately; the upload
  button is now "Upload assigned files".

## 2.5.0
- Export results now includes the attachments of every test case (ticked by default):
  - Excel: saved in the folder "<file name> - attachments\<test set>\<test case>\" next to
    the Excel file; new columns "Attachments" (count) and "Attachment files";
  - PDF: the pictures appear under each test case (other files are listed by name).
- Import results: "Upload attachments" is now ticked by default and uploads the files from
  that folder to the matched test cases (another department's folder or project too);
  without the folder it copies them from the exported test cases in ALM. Files with the
  same name already on the test case are skipped.
- Fix: test cases with many attachments - all pages of ALM's attachment list are read
  (before, only the first page was used when downloading, copying and checking duplicates).

## 2.4.0
- Import results can also duplicate attachments: "Copy attachments from the exported test
  cases" copies the attachments of each test case in the Excel file (its Test Case ID) to the
  matched test case in the loaded folder, e.g. from Merchant App to another department's
  copy. Optional "images only". Attachments with the same name are skipped, so importing
  twice does not duplicate anything; nothing is copied when source and target are the same.

## 2.3.0
- "Export results...": the results of all (or the ticked) test cases, read fresh from ALM,
  - to Excel: Results sheet (Test Lab folder, Test Set, Test Case, ID, Status, Comments,
    Tester, Exec Date, Time), Status / Comments drop-downs with the ALM values, editable
    columns in yellow, filter and frozen header; Summary sheet (counts per test set);
  - to PDF: A4 landscape report with a summary and one table per test set.
- "Import results...": reads such an Excel file into the loaded Test Lab folder - the same
  one or another department's. Rows are matched by test case ID (same folder), else by test
  set + test case name, else by a similar test set name ("5.5 Login" ~ "5.5 Login - Finance").
  Preview shows every change (old -> new), skipped rows and problems before anything is
  written; Status is set directly, or as a run when ALM does not allow that.
- The test tree shows each test case's status (e.g. "- Passed") and can filter by it.

## 2.2.0
- Load always starts with every image and test case visible (Search / Show are reset), and
  the image list title shows "Images (271)" or "Images (showing 12 of 271)".
- Suggestions:
  - similar images follow each other: once "1.3 Login-4.png" is assigned, "1.3 Login-5.png",
    "-6", ... suggest the same test case(s) ("suggest (like 1.3 Login-4.png)");
  - more name matches: word starts (land ~ landing), small typos, abbreviations (pw ~ password);
  - close calls are shown as "maybe" (purple) instead of being left out;
  - the Status column says "suggested" / "maybe".
- .jfif images are included.
- Clicking an image no longer moves the selection in the test tree (it could change where
  "Assign" puts the images when nothing is ticked).

## 2.1.0
- "Tick all" next to "Untick all": ticks every test case the tree shows (respects Search / Show).
- Images: "Select all" and "Deselect all" (respect Search / Show); Ctrl+A still selects all.

## 2.0.0
- Images: click a column title (Image / Test case / Status) to sort, again to reverse.
  Sorting is natural: "1.9" comes before "1.10".
- Images: "Show" filter - all, not assigned, with suggestion, assigned but not uploaded, uploaded.
- Test sets / test cases: "Sort" (name A-Z / Z-A, most / fewest images, test case ID, ALM
  test order) and "Show" filter (with / without images, ticked, waiting for upload, and by
  the Comments value, e.g. "Comments: NA").
- NA indicator: the Comments column of every test case is read on Load and shown as
  `[NA]` (grey) in the tree; each test set shows how many of its test cases are NA.
  "Set field" updates the indicator straight away.
- Version number in the window title, the login screen and the .exe properties.

## 1.5
- Download attachments of the ticked test cases into `<folder>\<test set>\<test case>\`,
  renamed after the test case, ready to Load and upload again.

## 1.4
- Tick boxes for test sets / test cases; assign the selected images to every ticked test
  case. "Untick all", "Clear all assignments".
- "Set field" module: set a field such as Comments = NA on all ticked test cases.

## 1.3
- Assign screen: images with preview and name-based suggestions on the left, test sets and
  test cases on the right; assignments saved in `ALM-assignments.txt`.

## 1.2
- Works with ALM versions without the test-instance `test-order` / `name` fields (ALM 25.x).
- Built against the official .NET Framework 4.5 reference assemblies
  (fixes "Method not found: System.String.TrimEnd(Char)").

## 1.1
- Step by step: log in, choose domain and project, browse the Test Lab folder tree.

## 1.0
- First Windows version: Test Lab folder, folder-per-test-case upload, watch mode.
