// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuickLookNext.Helpers;

/// <summary>
/// v3.42.0: tiny, valid sample files for the preview warm-up. They live in the temp
/// folder and are removed at the start of the next warm-up. They are deliberately
/// *not* deleted as soon as the panel reports "ready": the plugins keep reading the
/// file from background tasks after that (the image thumbnail provider, the text
/// panel's highlight pass), and deleting it under them produced file-not-found
/// exceptions in the log. The user's own files are never touched.
/// </summary>
internal static class WarmUpSamples
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "QuickLookNext.WarmUp");

    /// <summary>Returns the path of a valid sample for <paramref name="extension"/>, or null.</summary>
    internal static string Create(string extension)
    {
        try
        {
            Directory.CreateDirectory(Root);

            var path = Path.Combine(Root, "sample" + extension);

            switch (extension.ToLowerInvariant())
            {
                case ".txt":
                    File.WriteAllText(path, "QuickLook-Next warm-up" + Environment.NewLine, Utf8);
                    break;
                case ".md":
                    File.WriteAllText(path, "# QuickLook-Next warm-up" + Environment.NewLine, Utf8);
                    break;
                case ".html":
                    File.WriteAllText(path, "<html><body>QuickLook-Next warm-up</body></html>", Utf8);
                    break;
                case ".csv":
                    File.WriteAllText(path, "a,b" + Environment.NewLine + "1,2" + Environment.NewLine, Utf8);
                    break;
                case ".png":
                    // 1x1 transparent PNG, the same trick the image plugin's own
                    // decoder warm-up uses.
                    File.WriteAllBytes(path, Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="));
                    break;
                case ".zip":
                    WriteEmptyZip(path);
                    break;
                case ".pdf":
                    WriteMinimalPdf(path);
                    break;
                case ".ttf":
                    return CopySystemFont(path);
                default:
                    return null;
            }

            return path;
        }
        catch
        {
            // A sample that cannot be written simply means "do not warm this family".
            return null;
        }
    }

    internal static void Delete(string path)
        => _ = path; // superseded by ResetFolder; kept for call-site clarity

    /// <summary>
    /// Drops the samples of the previous session. Called once, before anything is
    /// built, so no background reader can be holding a file at that moment.
    /// </summary>
    internal static void ResetFolder()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // A previous process may still hold a handle for a moment; the next
            // run tries again and %TEMP% cleanup covers the rest.
        }
    }

    private static void WriteEmptyZip(string path)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("warmup.txt");
        using var writer = new StreamWriter(entry.Open(), Utf8);
        writer.Write("QuickLook-Next warm-up");
    }

    /// <summary>
    /// The smallest PDF that pdfium opens happily: one page, one text object.
    /// </summary>
    private static void WriteMinimalPdf(string path)
    {
        const string content = "BT /F1 12 Tf 40 100 Td (QuickLook-Next warm-up) Tj ET";

        var body = new StringBuilder();
        var offsets = new int[6];

        body.Append("%PDF-1.4\n");

        offsets[1] = body.Length;
        body.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");

        offsets[2] = body.Length;
        body.Append("2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n");

        offsets[3] = body.Length;
        body.Append("3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]")
            .Append("/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>endobj\n");

        offsets[4] = body.Length;
        body.Append("4 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n");

        offsets[5] = body.Length;
        body.Append($"5 0 obj<</Length {content.Length}>>stream\n")
            .Append(content)
            .Append("\nendstream\nendobj\n");

        var xref = body.Length;
        body.Append("xref\n0 6\n")
            .Append("0000000000 65535 f \n");

        for (var i = 1; i <= 5; i++)
            body.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");

        body.Append("trailer<</Size 6/Root 1 0 R>>\nstartxref\n")
            .Append(xref)
            .Append("\n%%EOF\n");

        // Latin-1 keeps every byte of the offsets above a single byte long.
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(body.ToString()));
    }

    private static string CopySystemFont(string path)
    {
        var fonts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

        foreach (var name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" })
        {
            var source = Path.Combine(fonts, name);
            if (!File.Exists(source))
                continue;

            File.Copy(source, path, overwrite: true);
            return path;
        }

        return null;
    }

    // No BOM: the preview plugins sniff encodings and a BOM would only add noise.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
}
