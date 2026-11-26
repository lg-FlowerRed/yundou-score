using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace YundouScore
{
    internal static class Xlsx
    {
        private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string PackageRelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string DocumentRelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
        private const string SpreadsheetMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
        private const string ScoresName = "成绩汇总";
        private const string SourcesName = "来源与说明";
        private const int MaxRows = 1048576;
        private const int MaxColumns = 16384;
        private const int MaxTextLength = 32767;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        internal static void Save(string path, IList<IList<object>> scores, IList<IList<object>> sources)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("An XLSX destination path is required.", "path");

            string destination = Path.GetFullPath(path);
            if (File.Exists(destination))
                throw new IOException("Refusing to overwrite an existing file: " + destination);

            int scoreWidth = GetColumnCount(scores, "scores", ScoresName);
            int sourceWidth = GetColumnCount(sources, "sources", SourcesName);
            string temporary = Path.Combine(Path.GetDirectoryName(destination), ".yundou-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporary = false;
            try
            {
                using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    using (ZipArchive zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                    {
                        WriteContentTypes(zip);
                        WriteRelationships(zip, "_rels/.rels", new[] { "officeDocument" }, new[] { "xl/workbook.xml" });
                        WriteWorkbook(zip);
                        WriteRelationships(zip, "xl/_rels/workbook.xml.rels",
                            new[] { "worksheet", "worksheet", "styles" },
                            new[] { "worksheets/sheet1.xml", "worksheets/sheet2.xml", "styles.xml" });
                        WriteStyles(zip);
                        WriteSheet(zip, "xl/worksheets/sheet1.xml", ScoresName, scores, scoreWidth, true);
                        WriteSheet(zip, "xl/worksheets/sheet2.xml", SourcesName, sources, sourceWidth, false);
                    }
                }

                // Same-directory rename publishes the completed ZIP. File.Move does not replace a file,
                // even if a different process created it after the initial check.
                File.Move(temporary, destination);
                ownsTemporary = false;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { } // Preserve the original failure if cleanup also fails.
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static int GetColumnCount(IList<IList<object>> rows, string parameter, string sheet)
        {
            if (rows == null)
                throw new ArgumentNullException(parameter);
            if (rows.Count > MaxRows)
                throw new ArgumentException(sheet + " exceeds Excel's 1,048,576-row limit (including the header).", parameter);

            int columns = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                IList<object> row = rows[i];
                if (row == null)
                    continue;
                if (row.Count > MaxColumns)
                    throw new ArgumentException(sheet + " row " + Number(i + 1) + " exceeds Excel's 16,384-column limit (XFD).", parameter);
                columns = Math.Max(columns, row.Count);
            }
            return columns;
        }

        private static XmlWriter OpenPart(ZipArchive zip, string name)
        {
            return XmlWriter.Create(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                NewLineHandling = NewLineHandling.Entitize,
                CloseOutput = true
            });
        }

        private static void WriteContentTypes(ZipArchive zip)
        {
            using (XmlWriter xml = OpenPart(zip, "[Content_Types].xml"))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("Types", ContentTypesNs);
                ContentType(xml, "Default", "Extension", "rels", "application/vnd.openxmlformats-package.relationships+xml");
                ContentType(xml, "Default", "Extension", "xml", "application/xml");
                ContentType(xml, "Override", "PartName", "/xl/workbook.xml", SpreadsheetMime + "sheet.main+xml");
                ContentType(xml, "Override", "PartName", "/xl/styles.xml", SpreadsheetMime + "styles+xml");
                ContentType(xml, "Override", "PartName", "/xl/worksheets/sheet1.xml", SpreadsheetMime + "worksheet+xml");
                ContentType(xml, "Override", "PartName", "/xl/worksheets/sheet2.xml", SpreadsheetMime + "worksheet+xml");
                xml.WriteEndDocument();
            }
        }

        private static void ContentType(XmlWriter xml, string element, string key, string value, string mime)
        {
            xml.WriteStartElement(element, ContentTypesNs);
            xml.WriteAttributeString(key, value);
            xml.WriteAttributeString("ContentType", mime);
            xml.WriteEndElement();
        }

        private static void WriteRelationships(ZipArchive zip, string part, string[] types, string[] targets)
        {
            using (XmlWriter xml = OpenPart(zip, part))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("Relationships", PackageRelationshipsNs);
                for (int i = 0; i < types.Length; i++)
                {
                    xml.WriteStartElement("Relationship", PackageRelationshipsNs);
                    xml.WriteAttributeString("Id", "rId" + Number(i + 1));
                    xml.WriteAttributeString("Type", DocumentRelationshipsNs + "/" + types[i]);
                    xml.WriteAttributeString("Target", targets[i]);
                    xml.WriteEndElement();
                }
                xml.WriteEndDocument();
            }
        }

        private static void WriteWorkbook(ZipArchive zip)
        {
            using (XmlWriter xml = OpenPart(zip, "xl/workbook.xml"))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("workbook", SpreadsheetNs);
                xml.WriteAttributeString("xmlns", "r", null, DocumentRelationshipsNs);
                xml.WriteStartElement("bookViews", SpreadsheetNs);
                xml.WriteStartElement("workbookView", SpreadsheetNs);
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteStartElement("sheets", SpreadsheetNs);
                WriteSheetReference(xml, ScoresName, "1");
                WriteSheetReference(xml, SourcesName, "2");
                xml.WriteEndDocument();
            }
        }

        private static void WriteSheetReference(XmlWriter xml, string name, string id)
        {
            xml.WriteStartElement("sheet", SpreadsheetNs);
            xml.WriteAttributeString("name", name);
            xml.WriteAttributeString("sheetId", id);
            xml.WriteAttributeString("r", "id", DocumentRelationshipsNs, "rId" + id);
            xml.WriteEndElement();
        }

        private static void WriteStyles(ZipArchive zip)
        {
            using (XmlWriter xml = OpenPart(zip, "xl/styles.xml"))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("styleSheet", SpreadsheetNs);
                // Fixed styles: cellXfs[0] is the default and cellXfs[1] is the header.
                xml.WriteRaw(@"
<fonts count=""2"">
  <font><sz val=""11""/><name val=""Arial""/><family val=""2""/></font>
  <font><b/><sz val=""11""/><color rgb=""FFFFFFFF""/><name val=""Arial""/><family val=""2""/></font>
</fonts>
<fills count=""3"">
  <fill><patternFill patternType=""none""/></fill>
  <fill><patternFill patternType=""gray125""/></fill>
  <fill><patternFill patternType=""solid""><fgColor rgb=""FF1E3A5F""/><bgColor indexed=""64""/></patternFill></fill>
</fills>
<borders count=""1""><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
<cellXfs count=""2"">
  <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
  <xf numFmtId=""0"" fontId=""1"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyAlignment=""1""><alignment vertical=""center"" wrapText=""1""/></xf>
</cellXfs>
<cellStyles count=""1""><cellStyle name=""Normal"" xfId=""0"" builtinId=""0""/></cellStyles>
");
                xml.WriteEndDocument();
            }
        }

        private static void WriteSheet(ZipArchive zip, string part, string name, IList<IList<object>> rows, int columns, bool scores)
        {
            string range = columns == 0 ? "A1" : "A1:" + ColumnName(columns) + Number(rows.Count);
            using (XmlWriter xml = OpenPart(zip, part))
            {
                xml.WriteStartDocument();
                xml.WriteStartElement("worksheet", SpreadsheetNs);
                xml.WriteStartElement("dimension", SpreadsheetNs);
                xml.WriteAttributeString("ref", range);
                xml.WriteEndElement();
                if (scores)
                    WriteFrozenView(xml);
                if (columns > 0)
                    WriteColumns(xml, columns, scores);

                xml.WriteStartElement("sheetData", SpreadsheetNs);
                for (int i = 0; i < rows.Count; i++)
                {
                    string rowNumber = Number(i + 1);
                    xml.WriteStartElement("row", SpreadsheetNs);
                    xml.WriteAttributeString("r", rowNumber);
                    if (i == 0)
                    {
                        xml.WriteAttributeString("ht", "25");
                        xml.WriteAttributeString("customHeight", "1");
                    }

                    IList<object> row = rows[i];
                    if (row != null)
                    {
                        for (int j = 0; j < row.Count; j++)
                        {
                            object value = row[j];
                            if (value != null || i == 0)
                                WriteCell(xml, name, ColumnName(j + 1) + rowNumber, value, i == 0);
                        }
                    }
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();

                if (scores && columns > 0)
                {
                    xml.WriteStartElement("autoFilter", SpreadsheetNs);
                    xml.WriteAttributeString("ref", range);
                    xml.WriteEndElement();
                }
                xml.WriteEndDocument();
            }
        }

        private static void WriteFrozenView(XmlWriter xml)
        {
            xml.WriteStartElement("sheetViews", SpreadsheetNs);
            xml.WriteStartElement("sheetView", SpreadsheetNs);
            xml.WriteAttributeString("workbookViewId", "0");
            xml.WriteStartElement("pane", SpreadsheetNs);
            xml.WriteAttributeString("ySplit", "1");
            xml.WriteAttributeString("topLeftCell", "A2");
            xml.WriteAttributeString("activePane", "bottomLeft");
            xml.WriteAttributeString("state", "frozen");
            xml.WriteEndElement();
            xml.WriteStartElement("selection", SpreadsheetNs);
            xml.WriteAttributeString("pane", "bottomLeft");
            xml.WriteAttributeString("activeCell", "A2");
            xml.WriteAttributeString("sqref", "A2");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        private static void WriteColumns(XmlWriter xml, int columns, bool scores)
        {
            int[] widths = scores ? new[] { 15, 14, 15, 26, 13 } : new[] { 24, 52 };
            xml.WriteStartElement("cols", SpreadsheetNs);
            for (int i = 0; i < widths.Length && i < columns; i++)
                WriteWidth(xml, i + 1, i + 1, widths[i]);
            if (columns > widths.Length)
                WriteWidth(xml, widths.Length + 1, columns, scores ? 12 : 26);
            xml.WriteEndElement();
        }

        private static void WriteWidth(XmlWriter xml, int first, int last, int width)
        {
            xml.WriteStartElement("col", SpreadsheetNs);
            xml.WriteAttributeString("min", Number(first));
            xml.WriteAttributeString("max", Number(last));
            xml.WriteAttributeString("width", Number(width));
            xml.WriteAttributeString("customWidth", "1");
            xml.WriteEndElement();
        }

        private static void WriteCell(XmlWriter xml, string sheet, string reference, object value, bool header)
        {
            xml.WriteStartElement("c", SpreadsheetNs);
            xml.WriteAttributeString("r", reference);
            if (header)
                xml.WriteAttributeString("s", "1");

            if (value != null)
            {
                string number = NumericText(value);
                if (number != null)
                {
                    xml.WriteAttributeString("t", "n");
                    xml.WriteElementString("v", SpreadsheetNs, number);
                }
                else if (value is bool)
                {
                    xml.WriteAttributeString("t", "b");
                    xml.WriteElementString("v", SpreadsheetNs, (bool)value ? "1" : "0");
                }
                else
                {
                    string text = Convert.ToString(value, Invariant) ?? string.Empty;
                    if (text.Length > MaxTextLength)
                        throw new ArgumentException(sheet + "!" + reference + " exceeds Excel's 32,767-character cell text limit.");
                    xml.WriteAttributeString("t", "inlineStr");
                    xml.WriteStartElement("is", SpreadsheetNs);
                    xml.WriteStartElement("t", SpreadsheetNs);
                    xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                    xml.WriteString(EncodeText(text));
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }
            }
            xml.WriteEndElement();
        }

        private static string NumericText(object value)
        {
            if (value is double)
            {
                double number = (double)value;
                return double.IsNaN(number) || double.IsInfinity(number) ? null : number.ToString("R", Invariant);
            }
            if (value is float)
            {
                float number = (float)value;
                return float.IsNaN(number) || float.IsInfinity(number) ? null : number.ToString("R", Invariant);
            }
            if (value is byte || value is sbyte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong || value is decimal)
                return ((IFormattable)value).ToString(null, Invariant);
            if (value is IntPtr)
                return ((IntPtr)value).ToInt64().ToString(Invariant);
            if (value is UIntPtr)
                return ((UIntPtr)value).ToUInt64().ToString(Invariant);
            return null;
        }

        private static string EncodeText(string text)
        {
            StringBuilder encoded = null;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                string replacement = null;
                if (c == '\r')
                    replacement = "_x000D_";
                else if (!XmlConvert.IsXmlChar(c))
                    replacement = "�";
                else if (c == '_' && LooksLikeXStringEscape(text, i))
                    replacement = "_x005F_"; // Keep literal text such as _x0041_ from decoding as A.

                if (replacement != null)
                {
                    if (encoded == null)
                        encoded = new StringBuilder(text.Length);
                    encoded.Append(text, start, i - start);
                    encoded.Append(replacement);
                    start = i + 1;
                }
            }
            return encoded == null ? text : encoded.Append(text, start, text.Length - start).ToString();
        }

        private static bool LooksLikeXStringEscape(string text, int i)
        {
            if (i + 6 >= text.Length || (text[i + 1] != 'x' && text[i + 1] != 'X') || text[i + 6] != '_')
                return false;
            for (int j = i + 2; j <= i + 5; j++)
            {
                char c = text[j];
                if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F') && !(c >= 'a' && c <= 'f'))
                    return false;
            }
            return true;
        }

        private static string ColumnName(int column)
        {
            string result = string.Empty;
            while (column > 0)
            {
                column--;
                result = (char)('A' + column % 26) + result;
                column /= 26;
            }
            return result;
        }

        private static string Number(int value)
        {
            return value.ToString(Invariant);
        }
    }
}
