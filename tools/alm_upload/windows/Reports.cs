// ALM Image Uploader - test results export (Excel / PDF) and import (Excel).
// Excel files are written and read as plain Office Open XML (zip + XML), and the PDF is
// written by hand, so nothing beyond the .NET Framework is needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace AlmImageUploader
{
    // ======================================================================== Excel
    public class XlsxSheet
    {
        public string Name;
        public readonly List<string[]> Rows = new List<string[]>();     // row 0 is the header
        public double[] Widths;
        public readonly HashSet<int> Editable = new HashSet<int>();     // columns people may change (yellow)
        public readonly Dictionary<int, List<string>> Lists = new Dictionary<int, List<string>>();  // drop-downs
        public readonly HashSet<int> Numbers = new HashSet<int>();      // columns written as numbers (counts)
        public bool Hidden, HeaderRow = true;
    }

    public static class Xlsx
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        static string Esc(string s)
        {
            // XML 1.0 does not allow most control characters
            s = Regex.Replace(s ?? "", @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
            return SecurityElement.Escape(s);
        }

        public static string Col(int i)
        {
            var name = "";
            for (i++; i > 0; i = (i - 1) / 26) name = (char)('A' + (i - 1) % 26) + name;
            return name;
        }

        static void Put(ZipArchive zip, string path, string text)
        {
            using (var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                w.Write(text);
        }

        public static void Write(string path, List<XlsxSheet> sheets)
        {
            // drop-down values live in one hidden sheet, one column per list
            var lists = new XlsxSheet { Name = "Lists", Hidden = true, HeaderRow = false };
            var listRefs = new Dictionary<List<string>, string>();
            foreach (var list in sheets.SelectMany(s => s.Lists.Values).Distinct())
            {
                int col = listRefs.Count;
                for (int r = 0; r < list.Count; r++)
                {
                    while (lists.Rows.Count <= r) lists.Rows.Add(new string[0]);
                    var row = lists.Rows[r];
                    if (row.Length <= col) { Array.Resize(ref row, col + 1); lists.Rows[r] = row; }
                    row[col] = list[r];
                }
                listRefs[list] = string.Format("Lists!${0}$1:${0}${1}", Col(col), Math.Max(1, list.Count));
            }
            var all = new List<XlsxSheet>(sheets);
            if (listRefs.Count > 0) all.Add(lists);

            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                    + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                    + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                    + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                    + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                    + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                for (int i = 0; i < all.Count; i++)
                    types.AppendFormat("<Override PartName=\"/xl/worksheets/sheet{0}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>", i + 1);
                types.Append("</Types>");
                Put(zip, "[Content_Types].xml", types.ToString());
                Put(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + PkgRel + "\">"
                    + "<Relationship Id=\"rId1\" Type=\"" + Rel + "/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");

                var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"" + Main + "\" xmlns:r=\"" + Rel + "\"><sheets>");
                var wbRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + PkgRel + "\">");
                var names = new StringBuilder();
                for (int i = 0; i < all.Count; i++)
                {
                    wb.AppendFormat("<sheet name=\"{0}\" sheetId=\"{1}\" r:id=\"rId{1}\"{2}/>", Esc(all[i].Name), i + 1, all[i].Hidden ? " state=\"hidden\"" : "");
                    wbRels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"" + Rel + "/worksheet\" Target=\"worksheets/sheet{0}.xml\"/>", i + 1);
                    if (all[i].HeaderRow && all[i].Rows.Count > 1)
                        names.AppendFormat("<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{0}\" hidden=\"1\">'{1}'!$A$1:${2}${3}</definedName>",
                                           i, Esc(all[i].Name.Replace("'", "''")), Col(Width(all[i]) - 1), all[i].Rows.Count);
                }
                wb.Append("</sheets>");
                if (names.Length > 0) wb.Append("<definedNames>").Append(names).Append("</definedNames>");
                wb.Append("</workbook>");
                wbRels.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"" + Rel + "/styles\" Target=\"styles.xml\"/></Relationships>", all.Count + 1);
                Put(zip, "xl/workbook.xml", wb.ToString());
                Put(zip, "xl/_rels/workbook.xml.rels", wbRels.ToString());
                // styles: 0 normal, 1 header, 2 editable header (yellow), 3 editable cell (light yellow)
                Put(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"" + Main + "\">"
                    + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                    + "<fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
                    + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9D9D9\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                    + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFD966\"/><bgColor indexed=\"64\"/></patternFill></fill>"
                    + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF2CC\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>"
                    + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                    + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                    + "<cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                    + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
                    + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>"
                    + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"4\" borderId=\"0\" xfId=\"0\" applyFill=\"1\"/></cellXfs>"
                    + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                for (int i = 0; i < all.Count; i++)
                    Put(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", SheetXml(all[i], listRefs));
            }
        }

        static int Width(XlsxSheet s)
        {
            return Math.Max(1, s.Rows.Count == 0 ? 1 : s.Rows.Max(r => r.Length));
        }

        static string SheetXml(XlsxSheet s, Dictionary<List<string>, string> listRefs)
        {
            var x = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"" + Main + "\" xmlns:r=\"" + Rel + "\">");
            if (s.HeaderRow)
                x.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            if (s.Widths != null)
            {
                x.Append("<cols>");
                for (int c = 0; c < s.Widths.Length; c++)
                    x.AppendFormat(CultureInfo.InvariantCulture, "<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", c + 1, s.Widths[c]);
                x.Append("</cols>");
            }
            x.Append("<sheetData>");
            for (int r = 0; r < s.Rows.Count; r++)
            {
                x.AppendFormat("<row r=\"{0}\">", r + 1);
                var row = s.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    bool header = s.HeaderRow && r == 0;
                    int style = header ? (s.Editable.Contains(c) ? 2 : 1) : (s.Editable.Contains(c) ? 3 : 0);
                    var v = row[c];
                    if (string.IsNullOrEmpty(v) && style == 0) continue;
                    x.AppendFormat("<c r=\"{0}{1}\"{2}", Col(c), r + 1, style != 0 ? " s=\"" + style + "\"" : "");
                    if (string.IsNullOrEmpty(v)) { x.Append("/>"); continue; }
                    long number;
                    if (!header && s.Numbers.Contains(c) && long.TryParse(v, out number))
                    {
                        x.Append("><v>").Append(number).Append("</v></c>");
                        continue;
                    }
                    x.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Esc(v)).Append("</t></is></c>");
                }
                x.Append("</row>");
            }
            x.Append("</sheetData>");
            int last = Math.Max(2, s.Rows.Count);
            if (s.HeaderRow && s.Rows.Count > 1)
                x.AppendFormat("<autoFilter ref=\"A1:{0}{1}\"/>", Col(Width(s) - 1), s.Rows.Count);
            if (s.Lists.Count > 0)
            {
                x.AppendFormat("<dataValidations count=\"{0}\">", s.Lists.Count);
                foreach (var kv in s.Lists)
                    x.AppendFormat("<dataValidation type=\"list\" allowBlank=\"1\" showErrorMessage=\"1\" sqref=\"{0}2:{0}{1}\"><formula1>{2}</formula1></dataValidation>",
                                   Col(kv.Key), last, Esc(listRefs[kv.Value]));
                x.Append("</dataValidations>");
            }
            x.Append("</worksheet>");
            return x.ToString();
        }

        // ------------------------------------------------------------- reading
        static IEnumerable<XmlElement> Kids(XmlNode n, string localName)
        {
            foreach (XmlNode c in n.ChildNodes)
                if (c is XmlElement && c.LocalName == localName) yield return (XmlElement)c;
        }

        static XmlDocument Load(ZipArchive zip, string path)
        {
            var e = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            var doc = new XmlDocument();
            using (var st = e.Open()) doc.Load(st);
            return doc;
        }

        static string AllText(XmlNode n)
        {
            // text of every <t> below n (rich text has several runs)
            var sb = new StringBuilder();
            foreach (XmlNode t in n.SelectNodes(".//*[local-name()='t']")) sb.Append(t.InnerText);
            return sb.ToString();
        }

        /// Rows of one sheet (the one named preferSheet, else the first) as text.
        public static List<string[]> Read(string path, string preferSheet)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var wb = Load(zip, "xl/workbook.xml");
                if (wb == null) throw new InvalidDataException("not an Excel .xlsx file");
                var rels = Load(zip, "xl/_rels/workbook.xml.rels");
                var targets = Kids(rels.DocumentElement, "Relationship").ToDictionary(r => r.GetAttribute("Id"), r => r.GetAttribute("Target"));
                var sheets = wb.DocumentElement.SelectNodes("//*[local-name()='sheet']").Cast<XmlElement>().ToList();
                var sheet = sheets.FirstOrDefault(s => string.Equals(s.GetAttribute("name"), preferSheet, StringComparison.OrdinalIgnoreCase))
                            ?? sheets.First();
                var rid = sheet.Attributes.Cast<XmlAttribute>().First(a => a.LocalName == "id").Value;
                var target = targets[rid].TrimStart('/');
                if (!target.StartsWith("xl/")) target = "xl/" + target;

                var shared = new List<string>();
                var sst = Load(zip, "xl/sharedStrings.xml");
                if (sst != null) shared.AddRange(Kids(sst.DocumentElement, "si").Select(AllText));

                var doc = Load(zip, target);
                var rows = new List<string[]>();
                foreach (XmlElement row in doc.SelectNodes("//*[local-name()='sheetData']/*[local-name()='row']"))
                {
                    int r = rows.Count;
                    int rn;
                    if (int.TryParse(row.GetAttribute("r"), out rn)) r = rn - 1;
                    while (rows.Count <= r) rows.Add(new string[0]);
                    var cells = new List<string>();
                    int next = 0;
                    foreach (var c in Kids(row, "c"))
                    {
                        int col = next;
                        var m = Regex.Match(c.GetAttribute("r"), "^([A-Z]+)");
                        if (m.Success) { col = 0; foreach (char ch in m.Groups[1].Value) col = col * 26 + (ch - 'A' + 1); col--; }
                        next = col + 1;
                        string t = c.GetAttribute("t"), v;
                        var ve = Kids(c, "v").FirstOrDefault();
                        if (t == "s") { int k; v = ve != null && int.TryParse(ve.InnerText, out k) && k < shared.Count ? shared[k] : ""; }
                        else if (t == "inlineStr") v = Kids(c, "is").Select(AllText).FirstOrDefault() ?? "";
                        else if (t == "b") v = ve != null && ve.InnerText == "1" ? "TRUE" : "FALSE";
                        else v = ve != null ? ve.InnerText : "";
                        while (cells.Count <= col) cells.Add("");
                        cells[col] = v;
                    }
                    rows[r] = cells.ToArray();
                }
                return rows;
            }
        }
    }

    // ========================================================================== PDF
    /// Tiny PDF writer: Helvetica text, filled boxes, lines; enough for a results report.
    public class PdfDoc
    {
        public const double W = 842, H = 595;   // A4 landscape, points
        readonly List<StringBuilder> pages = new List<StringBuilder>();
        StringBuilder page;

        static readonly int[] Widths =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556,
            556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778,
            722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278,
            278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
        };

        public int PageCount { get { return pages.Count; } }

        public void NewPage()
        {
            page = new StringBuilder();
            pages.Add(page);
        }

        public static double TextWidth(string s, double size, bool bold)
        {
            double w = 0;
            foreach (char ch in s ?? "") w += ch >= 32 && ch <= 126 ? Widths[ch - 32] : 556;
            return w * size / 1000 * (bold ? 1.06 : 1);
        }

        /// Shortens text with "..." so it fits in maxWidth.
        public static string Fit(string s, double size, bool bold, double maxWidth)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            if (TextWidth(s, size, bold) <= maxWidth) return s;
            while (s.Length > 0 && TextWidth(s + "...", size, bold) > maxWidth) s = s.Substring(0, s.Length - 1);
            return s + "...";
        }

        static string Rgb(double[] c) { return string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1:0.###} {2:0.###}", c[0], c[1], c[2]); }

        public void Text(double x, double y, string s, double size, bool bold = false, double[] color = null)
        {
            var esc = new StringBuilder();
            foreach (char ch in s ?? "")
            {
                char c = ch > 255 ? '?' : ch;
                if (c == '\\' || c == '(' || c == ')') esc.Append('\\');
                esc.Append(c);
            }
            page.AppendFormat(CultureInfo.InvariantCulture, "BT /{0} {1:0.##} Tf {2} rg {3:0.##} {4:0.##} Td ({5}) Tj ET\n",
                              bold ? "F2" : "F1", size, Rgb(color ?? new double[] { 0, 0, 0 }), x, y, esc);
        }

        public void Box(double x, double y, double w, double h, double[] fill)
        {
            page.AppendFormat(CultureInfo.InvariantCulture, "{0} rg {1:0.##} {2:0.##} {3:0.##} {4:0.##} re f\n", Rgb(fill), x, y, w, h);
        }

        /// Small grey text at the bottom right of an earlier page (e.g. "Page 2 of 5").
        public void Footer(int pageIndex, string text)
        {
            pages[pageIndex].AppendFormat(CultureInfo.InvariantCulture, "BT /F1 8 Tf 0.5 0.5 0.5 rg {0:0.##} 22 Td ({1}) Tj ET\n",
                                          W - 36 - TextWidth(text, 8, false), text);
        }

        public void Line(double x1, double y1, double x2, double y2, double gray = 0.75)
        {
            page.AppendFormat(CultureInfo.InvariantCulture, "{0:0.##} G 0.5 w {1:0.##} {2:0.##} m {3:0.##} {4:0.##} l S\n", gray, x1, y1, x2, y2);
        }

        // ---- images (JPEG, embedded as-is)
        class PdfImage
        {
            public byte[] Jpeg;
            public int W, H;
        }

        readonly List<PdfImage> images = new List<PdfImage>();

        /// Turns an image file's bytes into a JPEG small enough for the report. Returns the image
        /// number to pass to DrawImage, or -1 when the bytes are not a readable image.
        public int AddImage(byte[] content, int maxPixels = 1200)
        {
            try
            {
                using (var src = new MemoryStream(content))
                using (var img = System.Drawing.Image.FromStream(src))
                {
                    double scale = Math.Min(1.0, (double)maxPixels / Math.Max(img.Width, img.Height));
                    int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
                    using (var bmp = new System.Drawing.Bitmap(w, h))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    using (var outStream = new MemoryStream())
                    {
                        g.Clear(System.Drawing.Color.White);   // transparent PNGs on white
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, w, h);
                        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                        var prm = new System.Drawing.Imaging.EncoderParameters(1);
                        prm.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                        bmp.Save(outStream, codec, prm);
                        images.Add(new PdfImage { Jpeg = outStream.ToArray(), W = w, H = h });
                        return images.Count - 1;
                    }
                }
            }
            catch (Exception e)
            {
                if (e is OutOfMemoryException || e is ArgumentException || e is System.Runtime.InteropServices.ExternalException) return -1;
                throw;
            }
        }

        /// Width / height ratio of an added image.
        public double Aspect(int image) { return (double)images[image].W / images[image].H; }

        public void DrawImage(int image, double x, double y, double w, double h)
        {
            page.AppendFormat(CultureInfo.InvariantCulture, "q {0:0.##} 0 0 {1:0.##} {2:0.##} {3:0.##} cm /Im{4} Do Q\n", w, h, x, y, image);
        }

        public void Frame(double x, double y, double w, double h, double gray = 0.7)
        {
            page.AppendFormat(CultureInfo.InvariantCulture, "{0:0.##} G 0.5 w {1:0.##} {2:0.##} {3:0.##} {4:0.##} re S\n", gray, x, y, w, h);
        }

        public void Save(string path)
        {
            // objects: 1 catalog, 2 pages, 3-4 fonts, then the images, then page + content per page
            var latin1 = Encoding.GetEncoding(28591);
            int firstImage = 5, firstPage = firstImage + images.Count;
            var objs = new List<KeyValuePair<string, byte[]>>();
            Action<string> add = t => objs.Add(new KeyValuePair<string, byte[]>(t, null));
            add("<< /Type /Catalog /Pages 2 0 R >>");
            var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => (firstPage + i * 2) + " 0 R"));
            add("<< /Type /Pages /Kids [" + kids + "] /Count " + pages.Count + " >>");
            add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            foreach (var im in images)
                objs.Add(new KeyValuePair<string, byte[]>(string.Format(
                    "<< /Type /XObject /Subtype /Image /Width {0} /Height {1} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {2} >>",
                    im.W, im.H, im.Jpeg.Length), im.Jpeg));
            var xobjects = images.Count == 0 ? "" : " /XObject << " + string.Join(" ", images.Select((im, i) => "/Im" + i + " " + (firstImage + i) + " 0 R")) + " >>";
            foreach (var p in pages)
            {
                int contents = objs.Count + 2;   // the page is object Count+1, its content stream the next one
                add(string.Format(CultureInfo.InvariantCulture,
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >>{3} >> /Contents {2} 0 R >>",
                    W, H, contents, xobjects));
                var body = latin1.GetBytes(p.ToString());
                objs.Add(new KeyValuePair<string, byte[]>("<< /Length " + body.Length + " >>", body));
            }
            using (var ms = new MemoryStream())
            {
                Action<string> write = t => { var b = latin1.GetBytes(t); ms.Write(b, 0, b.Length); };
                write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
                var offsets = new List<long>();
                for (int i = 0; i < objs.Count; i++)
                {
                    offsets.Add(ms.Position);
                    write((i + 1) + " 0 obj\n" + objs[i].Key);
                    if (objs[i].Value != null)
                    {
                        write("\nstream\n");
                        ms.Write(objs[i].Value, 0, objs[i].Value.Length);
                        write("\nendstream");
                    }
                    write("\nendobj\n");
                }
                long xref = ms.Position;
                write("xref\n0 " + (objs.Count + 1) + "\n0000000000 65535 f \n");
                foreach (var o in offsets) write(o.ToString("0000000000") + " 00000 n \n");
                write("trailer\n<< /Size " + (objs.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                File.WriteAllBytes(path, ms.ToArray());
            }
        }
    }

    // ===================================================================== results
    /// The test-instance fields a results report uses, as named in this ALM project.
    public class ResultFields
    {
        public AlmClient.FieldDef Status, Comment, Tester, ExecDate, ExecTime;
        public List<string> StatusValues = new List<string>(), CommentValues = new List<string>();

        public static readonly string[] DefaultStatuses = { "Passed", "Failed", "Blocked", "Not Completed", "No Run", "N/A" };

        public static ResultFields Read(AlmClient client)
        {
            var f = new ResultFields();
            var defs = client.GetFieldDefs("test-instance").Where(d => d.Active).ToList();
            f.Status = defs.FirstOrDefault(d => d.Name == "status")
                       ?? new AlmClient.FieldDef { Name = "status", Label = "Status", ListId = "", Editable = true, Active = true };
            f.Comment = defs.FirstOrDefault(d => d.Label.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0);
            f.Tester = defs.FirstOrDefault(d => d.Name == "actual-tester");
            f.ExecDate = defs.FirstOrDefault(d => d.Name == "exec-date");
            f.ExecTime = defs.FirstOrDefault(d => d.Name == "exec-time");
            if (f.Status.ListId != "") f.StatusValues = client.GetListValues(f.Status.ListId);
            if (f.StatusValues.Count == 0) f.StatusValues = DefaultStatuses.ToList();
            if (f.Comment != null && f.Comment.ListId != "") f.CommentValues = client.GetListValues(f.Comment.ListId);
            return f;
        }

        public IEnumerable<AlmClient.FieldDef> All()
        {
            return new[] { Status, Comment, Tester, ExecDate, ExecTime }.Where(d => d != null);
        }
    }

    /// One attachment of a test case in an export: saved next to the Excel file (Rel) and/or
    /// shown in the PDF (Image, -1 when it is not a picture).
    public class ExportedFile
    {
        public string Name, Rel;
        public int Image = -1;
    }

    public class ResultRow
    {
        public TestSet Set;
        public TestInstance Inst;
        public string Folder;   // sub folder of the test set, relative to the loaded Test Lab folder
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly List<ExportedFile> Files = new List<ExportedFile>();

        public string Get(AlmClient.FieldDef f)
        {
            string v;
            return f != null && Values.TryGetValue(f.Name, out v) ? v ?? "" : "";
        }
    }

    public static class Results
    {
        /// Current values from ALM for the given test cases, in tree order.
        public static List<ResultRow> Fetch(AlmClient client, TestLabFolder lab, List<TestInstance> targets, ResultFields f)
        {
            var fields = new[] { "id" }.Concat(f.All().Select(d => d.Name)).Distinct().ToArray();
            var now = client.GetByIds("test-instances", "id", targets.Select(i => i.Id), fields)
                            .ToDictionary(d => d["id"], d => d);
            var want = new HashSet<TestInstance>(targets);
            var rows = new List<ResultRow>();
            foreach (var ts in lab.Sets.OrderBy(s => s.PathText, Comparer<string>.Create(Util.NaturalCompare)))
                foreach (var inst in ts.Instances.Where(want.Contains).OrderBy(i => i.Label, Comparer<string>.Create(Util.NaturalCompare)))
                {
                    var row = new ResultRow { Set = ts, Inst = inst, Folder = string.Join("\\", ts.Path.Take(ts.Path.Count - 1)) };
                    Dictionary<string, string> d;
                    if (now.TryGetValue(inst.Id, out d))
                        foreach (var kv in d) row.Values[kv.Key] = kv.Value;
                    rows.Add(row);
                }
            return rows;
        }

        /// "Results.xlsx" -> "Results - attachments" (next to it).
        public static string AttachmentFolder(string excelFile)
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(excelFile)),
                                Path.GetFileNameWithoutExtension(excelFile) + " - attachments");
        }

        /// Downloads every test case's attachments: into folder (Excel export) and/or into the
        /// PDF as pictures. progress(test case number, files so far); stop() ends early.
        public static void FetchAttachments(AlmClient client, List<ResultRow> rows, string folder, PdfDoc pdf,
                                            bool imagesOnly, Func<bool> stop, Action<int, int> progress, Action<string> log)
        {
            int files = 0;
            for (int k = 0; k < rows.Count && !stop(); k++)
            {
                var r = rows[k];
                progress(k + 1, files);
                List<AlmClient.Attachment> atts;
                try { atts = client.GetAttachments("test-instances", r.Inst.Id); }
                catch (AlmException e) { log("[FAIL] " + r.Set.Name + " > " + r.Inst.Label + ": " + e.Message); continue; }
                foreach (var a in atts)
                {
                    if (stop()) break;
                    bool picture = Util.ImageExts.Contains(Path.GetExtension(a.Name).ToLowerInvariant());
                    if (imagesOnly && !picture) continue;
                    var f = new ExportedFile { Name = a.Name };
                    // a PDF only needs the pictures' content; other files are listed by name
                    if (folder != null || (pdf != null && picture))
                    {
                        byte[] content;
                        try { content = client.DownloadAttachment("test-instances", r.Inst.Id, a); }
                        catch (Exception e)
                        {
                            if (!(e is AlmException || e is WebException)) throw;
                            log("[FAIL] " + r.Set.Name + " > " + r.Inst.Label + " / " + a.Name + ": " + e.Message);
                            continue;
                        }
                        if (folder != null)
                        {
                            f.Rel = Path.Combine(Path.Combine(r.Set.Path.Select(Util.SafeFolderName).ToArray()),
                                                 Util.SafeFolderName(r.Inst.Label), Util.SafeFolderName(a.Name));
                            var full = Path.Combine(folder, f.Rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(full));
                            File.WriteAllBytes(full, content);
                        }
                        if (pdf != null && picture) f.Image = pdf.AddImage(content);
                    }
                    r.Files.Add(f);
                    files++;
                }
            }
            progress(rows.Count, files);
        }

        static readonly string[] Fixed = { "Test Lab folder", "Test Set", "Test Case", "Test Case ID" };

        public static void WriteExcel(string path, List<ResultRow> rows, ResultFields f, string title, bool withAttachments)
        {
            var cols = f.All().ToList();
            var results = new XlsxSheet { Name = "Results" };
            var tail = withAttachments ? new[] { "Test Set ID", "Attachments", "Attachment files" } : new[] { "Test Set ID" };
            results.Rows.Add(Fixed.Concat(cols.Select(c => c.Label)).Concat(tail).ToArray());
            foreach (var r in rows)
            {
                var end = withAttachments
                    ? new[] { r.Set.Id, r.Files.Count.ToString(), string.Join("; ", r.Files.Where(x => x.Rel != null).Select(x => x.Rel)) }
                    : new[] { r.Set.Id };
                results.Rows.Add(new[] { r.Folder, r.Set.Name, r.Inst.Label, r.Inst.Id }.Concat(cols.Select(r.Get)).Concat(end).ToArray());
            }
            results.Widths = new double[] { 22, 34, 34, 13 }.Concat(cols.Select(c => c == f.Comment ? 26.0 : 15.0))
                                 .Concat(withAttachments ? new[] { 12.0, 13.0, 60.0 } : new[] { 12.0 }).ToArray();
            if (withAttachments) results.Numbers.Add(Fixed.Length + cols.Count + 1);
            int statusCol = Fixed.Length + cols.IndexOf(f.Status);
            results.Editable.Add(statusCol);
            results.Lists[statusCol] = f.StatusValues;
            if (f.Comment != null)
            {
                int c = Fixed.Length + cols.IndexOf(f.Comment);
                results.Editable.Add(c);
                if (f.CommentValues.Count > 0) results.Lists[c] = f.CommentValues;
            }

            var summary = new XlsxSheet { Name = "Summary" };
            var statuses = Statuses(rows, f);
            summary.Rows.Add(new[] { "Test Set", "Test Cases" }.Concat(statuses).ToArray());
            foreach (var g in rows.GroupBy(r => r.Set))
                summary.Rows.Add(new[] { g.Key.PathText, g.Count().ToString() }
                                 .Concat(statuses.Select(st => g.Count(r => Status(r, f) == st).ToString())).ToArray());
            summary.Rows.Add(new[] { "Total", rows.Count.ToString() }.Concat(statuses.Select(st => rows.Count(r => Status(r, f) == st).ToString())).ToArray());
            summary.Widths = new double[] { 40, 12 }.Concat(statuses.Select(s => 13.0)).ToArray();
            for (int c = 1; c < 2 + statuses.Count; c++) summary.Numbers.Add(c);

            var info = new XlsxSheet { Name = "How to import", HeaderRow = false, Widths = new double[] { 110 } };
            foreach (var line in new[]
            {
                title,
                "Exported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " by ALM Image Uploader " + AppInfo.Version + ".",
                "",
                "To change results: edit the yellow columns (" + f.Status.Label + (f.Comment != null ? ", " + f.Comment.Label : "") + ") on the Results sheet and save.",
                "Then in ALM Image Uploader: Load the Test Lab folder to update (this one or another department's), press 'Import results...' and choose this file.",
                "Rows are matched by Test Case ID when it is the same folder, otherwise by Test Set + Test Case name (similar test set names are matched too).",
                withAttachments ? "The attachments of every test case are in the folder '" + Path.GetFileName(AttachmentFolder(path)) + "' next to this file. "
                                  + "Keep it next to the file: Import uploads them to the matched test cases." : "",
                "You see every change before anything is written to ALM. Do not rename the column titles.",
            })
                info.Rows.Add(new[] { line });

            Xlsx.Write(path, new List<XlsxSheet> { results, summary, info });
        }

        /// The test case's pictures under its row, in rows of thumbnails; other files by name.
        static void Pictures(PdfDoc pdf, ResultRow r, double left, double right, double bottom, ref double y, Action newPage)
        {
            const double maxH = 170, maxW = 240, gap = 8;
            var others = r.Files.Where(f => f.Image < 0).Select(f => f.Name).ToList();
            if (others.Count > 0)
            {
                if (y - 12 < bottom) newPage();
                var text = "Attachments: " + string.Join(",  ", others);
                pdf.Text(left, y + 2, PdfDoc.Fit(text, 7.5, false, right - left), 7.5, false, new[] { 0.4, 0.4, 0.4 });
                y -= 12;
            }
            double x = left, rowTop = y + 6, rowH = 0;
            foreach (var file in r.Files.Where(f => f.Image >= 0))
            {
                double aspect = pdf.Aspect(file.Image);
                double h = maxH, w = h * aspect;
                if (w > maxW) { w = maxW; h = w / aspect; }
                if (x + w > right) { rowTop -= rowH + gap + 10; x = left; rowH = 0; }
                if (rowTop - h - 10 < bottom)
                {
                    newPage();
                    rowTop = y + 6;
                    x = left;
                    rowH = 0;
                }
                pdf.DrawImage(file.Image, x, rowTop - h, w, h);
                pdf.Frame(x, rowTop - h, w, h);
                pdf.Text(x, rowTop - h - 8, PdfDoc.Fit(file.Name, 6.5, false, w), 6.5, false, new[] { 0.45, 0.45, 0.45 });
                x += w + gap;
                rowH = Math.Max(rowH, h);
            }
            if (rowH > 0) y = rowTop - rowH - 10 - 14;
        }

        static string Status(ResultRow r, ResultFields f)
        {
            var s = r.Get(f.Status);
            return s == "" ? "No Run" : s;
        }

        static List<string> Statuses(List<ResultRow> rows, ResultFields f)
        {
            var used = rows.Select(r => Status(r, f)).Distinct().ToList();
            return f.StatusValues.Where(used.Contains).Concat(used.Where(u => !f.StatusValues.Contains(u))).ToList();
        }

        static double[] StatusColor(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "passed": return new[] { 0.0, 0.5, 0.1 };
                case "failed": return new[] { 0.8, 0.1, 0.1 };
                case "blocked": return new[] { 0.85, 0.45, 0.0 };
                case "not completed": return new[] { 0.75, 0.55, 0.0 };
                case "n/a": return new[] { 0.35, 0.4, 0.55 };
                default: return new[] { 0.45, 0.45, 0.45 };
            }
        }

        public static void WritePdf(string path, List<ResultRow> rows, ResultFields f, string title, string subtitle, PdfDoc pdf = null)
        {
            pdf = pdf ?? new PdfDoc();
            const double left = 36, right = PdfDoc.W - 36, top = PdfDoc.H - 40, bottom = 40, rowH = 14;
            var grey = new[] { 0.93, 0.93, 0.93 };
            double y = 0;
            Action header = () =>
            {
                pdf.NewPage();
                pdf.Text(left, top, PdfDoc.Fit(title, 14, true, right - left), 14, true);
                pdf.Text(left, top - 16, PdfDoc.Fit(subtitle, 9, false, right - left), 9, false, new[] { 0.4, 0.4, 0.4 });
                y = top - 36;
            };
            Action<double> need = h => { if (y - h < bottom) header(); };
            header();

            // overall + per test set summary
            var statuses = Statuses(rows, f);
            pdf.Text(left, y, "Summary", 11, true);
            y -= rowH + 2;
            double nameW = 300, numW = Math.Min(70, (right - left - nameW - 60) / Math.Max(1, statuses.Count));
            Action<string, string, IList<string>, bool> sumRow = (name, total, counts, bold) =>
            {
                need(rowH);
                if (bold) pdf.Box(left, y - 4, right - left, rowH, grey);
                pdf.Text(left + 4, y, PdfDoc.Fit(name, 9, bold, nameW - 8), 9, bold);
                pdf.Text(left + nameW, y, total, 9, bold);
                for (int i = 0; i < counts.Count; i++)
                    pdf.Text(left + nameW + 60 + i * numW, y, counts[i], 9, bold, bold ? null : (counts[i] == "0" ? new[] { 0.7, 0.7, 0.7 } : StatusColor(statuses[i])));
                y -= rowH;
            };
            sumRow("Test set", "Test cases", statuses, true);
            foreach (var g in rows.GroupBy(r => r.Set))
                sumRow(g.Key.PathText, g.Count().ToString(), statuses.Select(st => g.Count(r => Status(r, f) == st).ToString()).ToList(), false);
            pdf.Line(left, y + rowH - 3, right, y + rowH - 3, 0.5);
            sumRow("Total", rows.Count.ToString(), statuses.Select(st => rows.Count(r => Status(r, f) == st).ToString()).ToList(), false);

            // one table per test set
            var cols = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("Test case", 250), new KeyValuePair<string, double>(f.Status.Label, 80),
            };
            if (f.Comment != null) cols.Add(new KeyValuePair<string, double>(f.Comment.Label, 170));
            if (f.Tester != null) cols.Add(new KeyValuePair<string, double>(f.Tester.Label, 100));
            if (f.ExecDate != null) cols.Add(new KeyValuePair<string, double>(f.ExecDate.Label, 70));
            cols.Add(new KeyValuePair<string, double>("ID", 50));
            Action tableHead = () =>
            {
                pdf.Box(left, y - 4, right - left, rowH, grey);
                double x = left + 28;
                pdf.Text(left + 4, y, "#", 8, true);
                foreach (var c in cols) { pdf.Text(x, y, PdfDoc.Fit(c.Key, 8, true, c.Value - 6), 8, true); x += c.Value; }
                y -= rowH;
            };
            foreach (var g in rows.GroupBy(r => r.Set))
            {
                y -= 10;
                need(rowH * 4);
                pdf.Text(left, y, PdfDoc.Fit(g.Key.PathText, 11, true, right - left - 150), 11, true);
                var counts = string.Join("   ", statuses.Select(st => new { st, n = g.Count(r => Status(r, f) == st) }).Where(a => a.n > 0).Select(a => a.st + ": " + a.n));
                pdf.Text(right - PdfDoc.TextWidth(counts, 8, false), y + 1, counts, 8, false, new[] { 0.35, 0.35, 0.35 });
                y -= rowH + 2;
                tableHead();
                int n = 0;
                foreach (var r in g)
                {
                    // keep the row together with its first row of pictures
                    double needed = rowH + (r.Files.Any(fl => fl.Image >= 0) ? 170 + 26 : 0) + (r.Files.Any(fl => fl.Image < 0) ? 12 : 0);
                    if (y - needed < bottom) { header(); pdf.Text(left, y, PdfDoc.Fit(g.Key.PathText + "  (continued)", 10, true, right - left), 10, true); y -= rowH + 2; tableHead(); }
                    n++;
                    pdf.Text(left + 4, y, n.ToString(), 8, false, new[] { 0.5, 0.5, 0.5 });
                    double x = left + 28;
                    var values = new List<string> { r.Inst.Label, Status(r, f) };
                    if (f.Comment != null) values.Add(r.Get(f.Comment));
                    if (f.Tester != null) values.Add(r.Get(f.Tester));
                    if (f.ExecDate != null) values.Add(r.Get(f.ExecDate));
                    values.Add(r.Inst.Id);
                    for (int i = 0; i < cols.Count; i++)
                    {
                        bool isStatus = i == 1;
                        pdf.Text(x, y, PdfDoc.Fit(values[i], 8.5, isStatus, cols[i].Value - 6), 8.5, isStatus, isStatus ? StatusColor(values[i]) : null);
                        x += cols[i].Value;
                    }
                    pdf.Line(left, y - 4, right, y - 4, 0.88);
                    y -= rowH;
                    Pictures(pdf, r, left + 28, right, bottom, ref y, () => { header(); tableHead(); });
                }
            }
            // page numbers once the page count is known
            for (int i = 0; i < pdf.PageCount; i++)
                pdf.Footer(i, string.Format("Page {0} of {1}", i + 1, pdf.PageCount));
            pdf.Save(path);
        }
    }

    // ====================================================================== import
    public class ImportRow
    {
        public int Line;
        public string Folder, SetName, CaseName, Id, NewStatus, NewComment;
        public bool HasStatus, HasComment;
        public TestSet Set;
        public TestInstance Target;
        public string How, Error;
        public bool NotHere;     // its test set is not in the loaded folder: skipped, not an error
        public bool AttachmentsDone;
        public string AttachResult = "";
        public readonly List<string> AttachFiles = new List<string>();   // from the "Attachment files" column
        public string AttachRoot;                                           // "<file> - attachments" folder, when present

        /// Files saved by the export next to the Excel file.
        public bool HasFolderFiles { get { return AttachRoot != null && AttachFiles.Count > 0; } }

        /// Attachments can be put on the target: from the export folder, or copied in ALM from
        /// the exported test case when that is another one than the target.
        public bool CanCopyAttachments
        {
            get
            {
                if (Target == null || Error != null || AttachmentsDone) return false;
                // the export folder holds every attachment it found: rows without files have none
                return AttachRoot != null ? AttachFiles.Count > 0 : Id != "" && Id != Target.Id;
            }
        }
        public string OldStatus = "", OldComment = "";
        public bool StatusChanges, CommentChanges;
        public string Result = "";
    }

    public static class ResultsImport
    {
        static int Find(string[] header, params string[] names)
        {
            for (int i = 0; i < header.Length; i++)
                foreach (var n in names)
                    if (n != null && Util.Norm(header[i]) == Util.Norm(n)) return i;
            return -1;
        }

        static string Cell(string[] row, int i)
        {
            return i >= 0 && i < row.Length ? (row[i] ?? "").Trim() : "";
        }

        /// Reads the Results sheet; the columns are found by their titles.
        public static List<ImportRow> Read(string path, ResultFields f)
        {
            var rows = Xlsx.Read(path, "Results");
            int h = rows.FindIndex(r => Find(r, "Test Case") >= 0 && Find(r, "Test Set") >= 0);
            if (h < 0) throw new InvalidDataException("no 'Test Set' and 'Test Case' columns found - use a file made by 'Export results'");
            var head = rows[h];
            int cFolder = Find(head, "Test Lab folder"), cSet = Find(head, "Test Set"), cCase = Find(head, "Test Case"),
                cId = Find(head, "Test Case ID"), cStatus = Find(head, f.Status.Label, "Status"),
                cComment = f.Comment == null ? -1 : Find(head, f.Comment.Label, "Comments", "Comment"),
                cFiles = Find(head, "Attachment files");
            var root = Results.AttachmentFolder(path);
            if (!Directory.Exists(root)) root = null;
            var list = new List<ImportRow>();
            for (int r = h + 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (Cell(row, cSet) == "" && Cell(row, cCase) == "") continue;
                var item = new ImportRow
                {
                    Line = r + 1, Folder = Cell(row, cFolder), SetName = Cell(row, cSet), CaseName = Cell(row, cCase), Id = Cell(row, cId),
                    NewStatus = Cell(row, cStatus), HasStatus = cStatus >= 0, NewComment = Cell(row, cComment), HasComment = cComment >= 0,
                    AttachRoot = root,
                };
                item.AttachFiles.AddRange(Cell(row, cFiles).Split(';').Select(x => x.Trim()).Where(x => x != ""));
                list.Add(item);
            }
            return list;
        }

        static bool SameCase(TestInstance i, string name)
        {
            var n = Util.Norm(name);
            return Util.Norm(i.Label) == n || Util.Norm(i.TestName) == n || (i.Name != "" && Util.Norm(i.Name) == n);
        }

        static List<string> Words(string s) { return Suggester.Tokens(s); }

        /// Finds the ALM test case for every row: same id (same folder), else same test set and
        /// test case name, else a test set whose name contains the other one (department copies).
        public static void Match(List<ImportRow> rows, TestLabFolder lab)
        {
            var taken = new Dictionary<TestInstance, ImportRow>();
            foreach (var row in rows)
            {
                var byId = row.Id != "" ? lab.FindInstance(row.Id) : null;
                if (byId != null && SameCase(byId, row.CaseName))
                {
                    row.Target = byId;
                    row.Set = lab.SetOf(byId);
                    row.How = "same test case";
                }
                else
                {
                    var sets = lab.Sets.Where(s => Util.Norm(s.Name) == Util.Norm(row.SetName)).ToList();
                    string how = "same names";
                    if (sets.Count > 1)
                    {
                        // same test set name in several sub folders: use the sub folder from the file
                        var inFolder = sets.Where(s => Util.Norm(string.Join(" ", s.Path.Take(s.Path.Count - 1))) == Util.Norm(row.Folder)).ToList();
                        if (inFolder.Count > 0) sets = inFolder;
                    }
                    if (sets.Count == 0)
                    {
                        var want = Words(row.SetName);
                        sets = lab.Sets.Where(s =>
                        {
                            var have = Words(s.Name);
                            var small = want.Count <= have.Count ? want : have;
                            var big = small == want ? have : want;
                            return small.Count > 0 && small.All(big.Contains);
                        }).ToList();
                        how = "similar test set";
                    }
                    var cands = sets.SelectMany(s => s.Instances.Where(i => SameCase(i, row.CaseName))).ToList();
                    if (cands.Count == 1)
                    {
                        row.Target = cands[0];
                        row.Set = lab.SetOf(cands[0]);
                        row.How = how;
                    }
                    else if (cands.Count > 1)
                        row.Error = string.Format("{0} test cases match ({1}) - fix the Test Set name", cands.Count,
                                                  string.Join(", ", cands.Select(i => lab.SetOf(i).PathText).Distinct().Take(3)));
                    else if (sets.Count == 0)
                    {
                        row.NotHere = true;
                        row.Error = "skipped: no test set like '" + row.SetName + "' in this folder";
                    }
                    else
                        row.Error = "no test case '" + row.CaseName + "' in " + string.Join(", ", sets.Select(s => s.Name).Take(3));
                }
                if (row.Target != null)
                {
                    ImportRow first;
                    if (taken.TryGetValue(row.Target, out first))
                    {
                        row.Error = "same test case as row " + first.Line;
                        row.Target = null;
                        row.Set = null;
                    }
                    else taken[row.Target] = row;
                }
            }
        }

        /// Reads the current values of the matched test cases and works out what changes.
        public static void Compare(AlmClient client, List<ImportRow> rows, ResultFields f)
        {
            var matched = rows.Where(r => r.Target != null).ToList();
            var fields = new[] { "id", f.Status.Name }.Concat(f.Comment != null ? new[] { f.Comment.Name } : new string[0]).ToArray();
            var now = client.GetByIds("test-instances", "id", matched.Select(r => r.Target.Id), fields).ToDictionary(d => d["id"], d => d);
            foreach (var r in matched)
            {
                Dictionary<string, string> d;
                string v;
                if (!now.TryGetValue(r.Target.Id, out d)) continue;
                r.OldStatus = d.TryGetValue(f.Status.Name, out v) ? v ?? "" : "";
                r.OldComment = f.Comment != null && d.TryGetValue(f.Comment.Name, out v) ? v ?? "" : "";
                if (r.HasStatus && r.NewStatus != "")
                {
                    var known = f.StatusValues.FirstOrDefault(s => string.Equals(s, r.NewStatus, StringComparison.OrdinalIgnoreCase));
                    if (known == null) { r.Error = "'" + r.NewStatus + "' is not a status in ALM (" + string.Join(", ", f.StatusValues) + ")"; continue; }
                    r.NewStatus = known;
                    r.StatusChanges = !string.Equals(known, r.OldStatus, StringComparison.OrdinalIgnoreCase);
                }
                if (r.HasComment && f.Comment != null)
                {
                    if (r.NewComment != "" && f.CommentValues.Count > 0)
                    {
                        var known = f.CommentValues.FirstOrDefault(s => string.Equals(s, r.NewComment, StringComparison.OrdinalIgnoreCase));
                        if (known == null) { r.Error = "'" + r.NewComment + "' is not in the " + f.Comment.Label + " list"; continue; }
                        r.NewComment = known;
                    }
                    r.CommentChanges = r.NewComment != r.OldComment;
                }
            }
        }

        /// Copies the attachments of the exported test case (row.Id) to the matched one; names that
        /// are already there are skipped, so importing twice does not duplicate anything.
        public static string CopyAttachments(AlmClient client, ImportRow r, bool imagesOnly)
        {
            if (r.HasFolderFiles) return UploadExported(client, r, imagesOnly);
            var atts = client.GetAttachments("test-instances", r.Id)
                             .Where(a => !imagesOnly || Util.ImageExts.Contains(Path.GetExtension(a.Name).ToLowerInvariant()))
                             .ToList();
            var there = client.ListAttachmentNames("test-instances", r.Target.Id);
            int copied = 0, skipped = 0;
            foreach (var a in atts)
            {
                if (there.Contains(a.Name)) { skipped++; continue; }
                client.UploadAttachment("test-instances", r.Target.Id, client.DownloadAttachment("test-instances", r.Id, a), a.Name);
                there.Add(a.Name);
                copied++;
            }
            r.AttachmentsDone = true;
            if (atts.Count == 0) return "nothing to copy";
            return "copied " + copied + " attachment(s)" + (skipped > 0 ? ", " + skipped + " already there" : "");
        }

        /// Uploads the files the export saved for this row ("<file> - attachments\...") to the target.
        static string UploadExported(AlmClient client, ImportRow r, bool imagesOnly)
        {
            var there = client.ListAttachmentNames("test-instances", r.Target.Id);
            int copied = 0, skipped = 0, missing = 0;
            foreach (var rel in r.AttachFiles)
            {
                var name = Path.GetFileName(rel);
                if (imagesOnly && !Util.ImageExts.Contains(Path.GetExtension(name).ToLowerInvariant())) continue;
                var full = Path.Combine(r.AttachRoot, rel);
                if (!File.Exists(full)) { missing++; continue; }
                if (there.Contains(name)) { skipped++; continue; }
                client.UploadAttachment("test-instances", r.Target.Id, File.ReadAllBytes(full), name);
                there.Add(name);
                copied++;
            }
            r.AttachmentsDone = true;
            return "uploaded " + copied + " file(s)" + (skipped > 0 ? ", " + skipped + " already there" : "")
                   + (missing > 0 ? ", " + missing + " missing in the folder" : "");
        }

        /// Writes one row to ALM. The status is set directly, or through a run when ALM refuses that.
        public static string Apply(AlmClient client, ImportRow r, ResultFields f, bool status, bool comment)
        {
            var done = new List<string>();
            if (comment && r.CommentChanges)
            {
                client.UpdateEntity("test-instances", r.Target.Id, new Dictionary<string, string> { { f.Comment.Name, r.NewComment } });
                done.Add(f.Comment.Label);
            }
            if (status && r.StatusChanges)
            {
                try
                {
                    client.UpdateEntity("test-instances", r.Target.Id, new Dictionary<string, string> { { f.Status.Name, r.NewStatus } });
                    done.Add(f.Status.Label);
                }
                catch (AlmException)
                {
                    client.CreateRun(r.Target, r.NewStatus);
                    done.Add(f.Status.Label + " (as a run)");
                }
            }
            return done.Count == 0 ? "no change" : "updated " + string.Join(" + ", done);
        }
    }
}
