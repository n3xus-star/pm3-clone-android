# ALM Image Uploader - changelog

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
