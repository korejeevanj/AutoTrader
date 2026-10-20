using System.IO.Compression;
using System.Text;

namespace Trading.Tests.Replay;

internal static class PublishedBankInputsFixture
{
    public static byte[] WeightArchive()
    {
        // Synthetic symbols and a separate uppercase company-name column.
        const string content = "BT /F1 10 Tf 28 760 Td (September 30, 2026) Tj ET\n"
            + "BT /F1 10 Tf 28 692 Td (AAA) Tj ET\nBT /F1 10 Tf 105 693 Td (BANK) Tj ET\nBT /F1 10 Tf 554 694 Td (60.00) Tj ET\n"
            + "BT /F1 10 Tf 28 669 Td (BBB) Tj ET\nBT /F1 10 Tf 105 670 Td (HDFC) Tj ET\nBT /F1 10 Tf 554 671 Td (40.00) Tj ET\n";
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream" };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString())); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString()); pdf.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        { using var entry = zip.CreateEntry("NIFTY_Bank_Sep2026.pdf").Open(); entry.Write(Encoding.ASCII.GetBytes(pdf.ToString())); }
        return bytes.ToArray();
    }

    public static byte[] EquityArchive()
    {
        const string csv = "FinInstrmId,TckrSymb,SctySrs,ISIN,NewBrdLotQty,BidIntrvl,DelFlg,ElgbltyNrmlMkt,SctyStsNrmlMkt,ElgbltyClsgAuctnSsn\n"
            + "123,AAA,EQ,SYNTHETIC-ISIN,1,5,N,1,6,1\n";
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(csv));
        return bytes.ToArray();
    }
}
