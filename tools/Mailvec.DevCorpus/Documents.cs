using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Mailvec.DevCorpus;

/// <summary>
/// Attachment payloads, built by hand so the corpus depends on nothing but
/// the SDK. Each builder produces the smallest well-formed file that reaches
/// the extraction path it is named for.
/// </summary>
internal static class Documents
{
    private static readonly DateTimeOffset ZipTime = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── PDF ─────────────────────────────────────────────────────────────────

    /// <summary>A text PDF (Helvetica, one line per entry). PdfPig extracts it → <c>done</c>.</summary>
    public static byte[] TextPdf(params string[] lines)
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 720 Td 16 TL\n");
        foreach (var line in lines)
        {
            content.Append('(').Append(PdfEscape(line)).Append(") Tj T*\n");
        }
        content.Append("ET\n");
        return Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", Encoding.ASCII.GetBytes(content.ToString())),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    }

    /// <summary>
    /// An image-only PDF: one page that is a grayscale bitmap of the given
    /// lines, and no text layer. PdfPig finds nothing → <c>no_text</c>, which
    /// makes it an OCR candidate; the bitmap text is real, so a vision model
    /// has something to transcribe.
    /// </summary>
    public static byte[] ScannedPdf(params string[] lines)
    {
        var (w, h, pixels) = BitmapFont.Render(lines, scale: 4, margin: 24);
        // Draw at one point per pixel, shrunk to fit inside half-inch margins.
        var drawW = Math.Min(w, 612 - 72);
        var drawH = h * drawW / w;
        var drawn = Encoding.ASCII.GetBytes($"q {drawW} 0 0 {drawH} 36 {792 - 36 - drawH} cm /Im1 Do Q\n");
        return Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", drawn),
            Stream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode ",
                Zlib(pixels)));
    }

    /// <summary>
    /// A PDF that needs a password: a Standard security handler whose /O and
    /// /U match no password, the shape of a bank statement locked to a
    /// customer number. PdfPig refuses it → <c>encrypted</c>.
    /// </summary>
    public static byte[] EncryptedPdf()
    {
        var o = new string('A', 64);
        var u = new string('B', 64);
        var id = new string('C', 32);
        return Pdf(
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
                Stream("", Encoding.ASCII.GetBytes("BT ET\n")),
                $"<< /Filter /Standard /V 1 /R 2 /O <{o}> /U <{u}> /P -3904 >>",
            ],
            extraTrailer: $"/Encrypt 5 0 R /ID [<{id}> <{id}>]");
    }

    private static string PdfEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\');
            if (c > 'ÿ') throw new ArgumentException($"'{c}' cannot go in a WinAnsi string", nameof(s));
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A stream object body: dictionary entries (without the brackets or /Length) plus data.</summary>
    private static byte[] Stream(string dictEntries, byte[] data)
    {
        var head = Encoding.ASCII.GetBytes($"<< {dictEntries}/Length {data.Length} >>\nstream\n");
        var tail = Encoding.ASCII.GetBytes("\nendstream");
        return [.. head, .. data, .. tail];
    }

    private static byte[] Pdf(params object[] objects) => Pdf(objects, extraTrailer: "");

    /// <summary>Numbers the objects from 1, writes a correct xref table and trailer.</summary>
    private static byte[] Pdf(object[] objects, string extraTrailer)
    {
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = ms.Position;
            Write($"{i + 1} 0 obj\n");
            switch (objects[i])
            {
                case string s: Write(s); break;
                case byte[] b: ms.Write(b); break;
                default: throw new ArgumentException("PDF objects are strings or byte[]");
            }
            Write("\nendobj\n");
        }
        var xref = ms.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Write($"{off:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R {extraTrailer}>>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ── Office (the package shapes tests/…/OfficePartLimitTests proves extract) ──

    private const string CtNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeDocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string PptxType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public static byte[] Docx(params string[] paragraphs) => Zip(
        ("[Content_Types].xml",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{CtNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="{DocxType}.main+xml"/></Types>"""),
        ("_rels/.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="{OfficeDocRel}" Target="word/document.xml"/></Relationships>"""),
        ("word/document.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>"""
         + string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{Xml(p)}</w:t></w:r></w:p>"))
         + "</w:body></w:document>"));

    /// <summary>
    /// A DOCX whose one part expands far past the extractor's per-part
    /// character ceiling while the package stays small — the zip-bomb shape
    /// OfficePartLimitTests pins.
    /// </summary>
    public static byte[] DocxBomb() =>
        Docx(Enumerable.Repeat("MAILVEC DEV CORPUS ZIP BOMB PARAGRAPH PADDING", 900_000).ToArray());

    public static byte[] Xlsx(params string[] cells) => Zip(
        ("[Content_Types].xml",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{CtNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="{XlsxType}.main+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>"""),
        ("_rels/.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="{OfficeDocRel}" Target="xl/workbook.xml"/></Relationships>"""),
        ("xl/_rels/workbook.xml.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>"""),
        ("xl/workbook.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets><sheet name="Budget" sheetId="1" r:id="rId9" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"/></sheets></workbook>"""),
        ("xl/sharedStrings.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">"""
         + string.Concat(cells.Select(c => $"<si><t>{Xml(c)}</t></si>"))
         + "</sst>"));

    public static byte[] Pptx(params string[] runs) => Zip(
        ("[Content_Types].xml",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{CtNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/ppt/presentation.xml" ContentType="{PptxType}.main+xml"/><Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/></Types>"""),
        ("_rels/.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId1" Type="{OfficeDocRel}" Target="ppt/presentation.xml"/></Relationships>"""),
        ("ppt/_rels/presentation.xml.rels",
         $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelsNs}"><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/></Relationships>"""),
        ("ppt/presentation.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst></p:presentation>"""),
        ("ppt/slides/slide1.xml",
         """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:sp><p:txBody>"""
         + string.Concat(runs.Select(r => $"<a:p><a:r><a:t>{Xml(r)}</a:t></a:r></a:p>"))
         + "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));

    private static string Xml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Fixed entry timestamps, so the same input is the same bytes every run.</summary>
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTime;
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }

    // ── Images ──────────────────────────────────────────────────────────────

    /// <summary>An 8-bit grayscale PNG of the given lines.</summary>
    public static byte[] Png(params string[] lines)
    {
        var (w, h, pixels) = BitmapFont.Render(lines, scale: 3, margin: 12);
        var raw = new byte[h * (w + 1)];
        for (var y = 0; y < h; y++)
        {
            raw[y * (w + 1)] = 0; // filter: None
            Array.Copy(pixels, y * w, raw, y * (w + 1) + 1, w);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // grayscale

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        PngChunk(ms, "IHDR", ihdr);
        PngChunk(ms, "IDAT", Zlib(raw));
        PngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void PngChunk(Stream s, string type, byte[] data)
    {
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)data.Length);
        s.Write(u32);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(u32, Crc32(typeAndData));
        s.Write(u32);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>
    /// An uncompressed little-endian 8-bit grayscale TIFF. SkiaSharp has no
    /// TIFF codec, so this is the file that routes image OCR through LibTiff.
    /// </summary>
    public static byte[] Tiff(params string[] lines)
    {
        var (w, h, pixels) = BitmapFont.Render(lines, scale: 3, margin: 12);
        (ushort Tag, ushort Type, uint Value)[] entries =
        [
            (256, 4, (uint)w),        // ImageWidth
            (257, 4, (uint)h),        // ImageLength
            (258, 3, 8),              // BitsPerSample
            (259, 3, 1),              // Compression: none
            (262, 3, 1),              // Photometric: BlackIsZero
            (273, 4, 0),              // StripOffsets (patched below)
            (277, 3, 1),              // SamplesPerPixel
            (278, 4, (uint)h),        // RowsPerStrip
            (279, 4, (uint)pixels.Length), // StripByteCounts
        ];
        var ifdSize = 2 + entries.Length * 12 + 4;
        var dataOffset = (uint)(8 + ifdSize);

        var buf = new byte[dataOffset + pixels.Length];
        buf[0] = (byte)'I'; buf[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8), (ushort)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var (tag, type, value) = entries[i];
            var at = buf.AsSpan(10 + i * 12);
            BinaryPrimitives.WriteUInt16LittleEndian(at, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(at[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(at[4..], 1);
            var v = tag == 273 ? dataOffset : value;
            if (type == 3) BinaryPrimitives.WriteUInt16LittleEndian(at[8..], (ushort)v);
            else BinaryPrimitives.WriteUInt32LittleEndian(at[8..], v);
        }
        pixels.CopyTo(buf, (int)dataOffset);
        return buf;
    }

    /// <summary>
    /// Bytes labelled HEIC: an ISO-BMFF 'ftyp heic' header and no image. The
    /// label is what matters — HEIC has no decoder here, so the OCR pass
    /// retires it whatever the bytes are.
    /// </summary>
    public static byte[] FakeHeic()
    {
        var header = new byte[] { 0, 0, 0, 0x18 }.Concat(Encoding.ASCII.GetBytes("ftypheic\0\0\0\0mif1heic")).ToArray();
        return [.. header, .. Enumerable.Range(0, 512).Select(i => (byte)(i * 31 % 251))];
    }

    // ── Text formats ────────────────────────────────────────────────────────

    public static byte[] Ics(string uid, string summary, DateTimeOffset start, string location, string description) =>
        Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Mailvec DevCorpus//EN\r\nMETHOD:REQUEST\r\n"
            + "BEGIN:VEVENT\r\n"
            + $"UID:{uid}\r\n"
            + $"DTSTART:{start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n"
            + $"DTEND:{start.AddHours(6).UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n"
            + $"SUMMARY:{summary}\r\n"
            + $"LOCATION:{location}\r\n"
            // RFC 5545 folding: a continuation line starts with one space.
            + $"DESCRIPTION:{description[..40]}\r\n {description[40..]}\r\n"
            + "ORGANIZER;CN=Ada Example:mailto:ada@example.com\r\n"
            + "ATTENDEE;CN=Sam Rivera:mailto:sam@example.org\r\n"
            + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    public static byte[] Vcard(string fullName, string org, string email, string tel, string note) =>
        Encoding.UTF8.GetBytes(
            "BEGIN:VCARD\r\nVERSION:3.0\r\n"
            + $"FN:{fullName}\r\nN:{string.Join(';', fullName.Split(' ').Reverse())};;;\r\n"
            + $"ORG:{org}\r\nEMAIL;TYPE=INTERNET:{email}\r\nTEL;TYPE=CELL:{tel}\r\nNOTE:{note}\r\n"
            + "END:VCARD\r\n");
}
