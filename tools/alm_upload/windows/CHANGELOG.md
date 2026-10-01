# ALM Image Uploader - changelog

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
