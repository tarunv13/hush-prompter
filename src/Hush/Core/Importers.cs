using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Hush.Core
{
    public sealed class ImportResult
    {
        public string Title { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Turns dropped files into notes. Every parser is defensive: a size cap on the file and on
    /// each archive entry (zip bombs), XML with DTDs and external entities disabled (XXE), and HTML
    /// read as text only, never rendered or executed.
    /// </summary>
    public static class Importers
    {
        public const long MaxFileBytes = 25L * 1024 * 1024;
        public const long MaxEntryBytes = 40L * 1024 * 1024;

        public static readonly string[] Supported = { ".txt", ".md", ".markdown", ".rtf", ".docx", ".html", ".htm" };

        public static bool IsSupported(string path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return Array.IndexOf(Supported, ext) >= 0;
        }

        public static ImportResult Import(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("File not found.", path);
            if (info.Length > MaxFileBytes) throw new InvalidDataException("The file is larger than 25 MB.");
            string ext = info.Extension.ToLowerInvariant();
            string title = Path.GetFileNameWithoutExtension(info.Name);
            string text;
            switch (ext)
            {
                case ".txt":
                case ".md":
                case ".markdown": text = ReadText(File.ReadAllBytes(path)); break;
                case ".rtf": text = RtfToText(ReadText(File.ReadAllBytes(path))); break;
                case ".docx": using (var fs = File.OpenRead(path)) text = DocxToText(fs); break;
                case ".html":
                case ".htm": text = HtmlToText(ReadText(File.ReadAllBytes(path))); break;
                case ".pdf": throw new NotSupportedException("PDF import is not supported. Copy the text into a new note instead.");
                default: throw new NotSupportedException("This file type is not supported.");
            }
            return new ImportResult { Title = title, Text = Normalise(text) };
        }

        /// <summary>Decodes UTF-8 (with or without BOM) or UTF-16 with BOM; falls back to Windows-1252.</summary>
        public static string ReadText(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            int off = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, off, bytes.Length - off);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        public static string DocxToText(Stream docx)
        {
            using (var zip = new ZipArchive(docx, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) throw new InvalidDataException("This is not a Word document.");
                if (entry.Length > MaxEntryBytes) throw new InvalidDataException("The document is too large.");
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxEntryBytes,
                    IgnoreComments = true
                };
                var sb = new StringBuilder();
                using (var s = entry.Open())
                using (var xr = XmlReader.Create(new LimitedStream(s, MaxEntryBytes), settings))
                {
                    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    xr.Read();
                    while (!xr.EOF)
                    {
                        if (xr.NodeType == XmlNodeType.Element && xr.NamespaceURI == W && xr.LocalName == "t" && !xr.IsEmptyElement)
                        {
                            // ReadElementContentAsString leaves the reader on the next node already.
                            sb.Append(xr.ReadElementContentAsString());
                            continue;
                        }
                        if (xr.NodeType == XmlNodeType.Element && xr.NamespaceURI == W)
                        {
                            switch (xr.LocalName)
                            {
                                case "tab": sb.Append('\t'); break;
                                case "br":
                                case "cr": sb.Append('\n'); break;
                            }
                        }
                        else if (xr.NodeType == XmlNodeType.EndElement && xr.NamespaceURI == W && xr.LocalName == "p")
                        {
                            sb.Append('\n');
                        }
                        xr.Read();
                    }
                }
                return sb.ToString();
            }
        }

        public static string HtmlToText(string html)
        {
            string s = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"<(script|style|head|noscript|template)\b.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*(br|hr)\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*h([1-3])\b[^>]*>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</\s*(p|div|h[1-6]|li|tr|section|article|blockquote|ul|ol|table)\s*>", "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t ]+", " ");
            return s;
        }

        /// <summary>Minimal RTF to plain text: keeps text, paragraph breaks and escaped characters.</summary>
        public static string RtfToText(string rtf)
        {
            if (string.IsNullOrEmpty(rtf) || !rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal))
                throw new InvalidDataException("This is not an RTF file.");
            var sb = new StringBuilder(rtf.Length / 2);
            var skipStack = new System.Collections.Generic.Stack<bool>();
            bool skip = false;
            int ucSkip = 1, pendingSkip = 0;
            string[] destinations = { "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "footer", "headerl", "headerr", "footerl", "footerr", "object", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "themedata", "colorschememapping", "latentstyles", "datastore", "fldinst" };
            for (int i = 0; i < rtf.Length; i++)
            {
                char c = rtf[i];
                if (c == '{') { skipStack.Push(skip); continue; }
                if (c == '}') { skip = skipStack.Count > 0 && skipStack.Pop(); continue; }
                if (c == '\\')
                {
                    if (i + 1 >= rtf.Length) break;
                    char n = rtf[i + 1];
                    if (n == '\\' || n == '{' || n == '}') { if (!skip) sb.Append(n); i++; continue; }
                    if (n == '*') { skip = true; i++; continue; }
                    if (n == '\'')
                    {
                        if (i + 3 < rtf.Length && !skip)
                        {
                            if (pendingSkip > 0) pendingSkip--;
                            else if (int.TryParse(rtf.Substring(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out int code))
                                sb.Append(Encoding.GetEncoding(1252).GetString(new[] { (byte)code }));
                        }
                        i += 3; continue;
                    }
                    if (n == '~') { if (!skip) sb.Append(' '); i++; continue; }
                    if (n == '\n' || n == '\r') { if (!skip) sb.Append('\n'); i++; continue; }
                    int j = i + 1;
                    while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                    string word = rtf.Substring(i + 1, j - i - 1);
                    int k = j;
                    if (k < rtf.Length && (rtf[k] == '-' || char.IsDigit(rtf[k]))) { k++; while (k < rtf.Length && char.IsDigit(rtf[k])) k++; }
                    string param = rtf.Substring(j, k - j);
                    if (k < rtf.Length && rtf[k] == ' ') k++;
                    i = k - 1;
                    if (Array.IndexOf(destinations, word) >= 0) { skip = true; continue; }
                    if (skip) continue;
                    switch (word)
                    {
                        case "par": case "line": case "sect": case "page": sb.Append('\n'); break;
                        case "tab": sb.Append('\t'); break;
                        case "emdash": sb.Append('—'); break;
                        case "endash": sb.Append('–'); break;
                        case "bullet": sb.Append('•'); break;
                        case "lquote": sb.Append('‘'); break;
                        case "rquote": sb.Append('’'); break;
                        case "ldblquote": sb.Append('“'); break;
                        case "rdblquote": sb.Append('”'); break;
                        case "uc": int.TryParse(param, out ucSkip); break;
                        case "u":
                            if (int.TryParse(param, out int u)) { if (u < 0) u += 65536; sb.Append((char)u); pendingSkip = ucSkip; }
                            break;
                    }
                    continue;
                }
                if (c == '\r' || c == '\n') continue;
                if (skip) continue;
                if (pendingSkip > 0) { pendingSkip--; continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Normalise(string text)
        {
            string s = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>Stops reading after a byte budget, so a crafted archive cannot inflate forever.</summary>
        sealed class LimitedStream : Stream
        {
            readonly Stream _inner; long _left;
            public LimitedStream(Stream inner, long max) { _inner = inner; _left = max; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) throw new InvalidDataException("The document is too large.");
                int n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= n; return n;
            }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}
