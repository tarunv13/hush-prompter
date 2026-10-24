using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hush.Core
{
    public sealed class Note
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public DateTime Modified { get; set; }
    }

    public sealed class SearchHit
    {
        public Note Note { get; set; }
        public string Snippet { get; set; }
    }

    /// <summary>
    /// Notes are plain markdown files in the user's own profile: the first line is "# Title",
    /// the rest is the body. File names are random ids, never derived from the title, so a title
    /// cannot steer a write outside the folder.
    /// </summary>
    public sealed class NoteStore
    {
        public string Folder { get; }

        public NoteStore(string folder)
        {
            Folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(Folder);
        }

        public List<Note> All()
        {
            var list = new List<Note>();
            foreach (var f in Directory.EnumerateFiles(Folder, "*.md"))
            {
                var n = TryLoad(f);
                if (n != null) list.Add(n);
            }
            return list.OrderByDescending(n => n.Modified).ToList();
        }

        public Note Get(string id)
        {
            string p = PathFor(id);
            return p != null && File.Exists(p) ? TryLoad(p) : null;
        }

        public Note Create(string title, string body)
        {
            var n = new Note { Id = Guid.NewGuid().ToString("N"), Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(), Body = body ?? "" };
            Save(n);
            return n;
        }

        public void Save(Note n)
        {
            string p = PathFor(n.Id) ?? throw new ArgumentException("Bad note id.");
            string title = (n.Title ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            string content = "# " + (title.Length == 0 ? "Untitled" : title) + "\n\n" + (n.Body ?? "");
            AtomicWrite(p, content);
            n.Modified = File.GetLastWriteTimeUtc(p);
        }

        public void Delete(string id)
        {
            string p = PathFor(id);
            if (p != null && File.Exists(p)) File.Delete(p);
        }

        public List<SearchHit> Search(string query, int max = 50)
        {
            var hits = new List<SearchHit>();
            if (string.IsNullOrWhiteSpace(query)) return hits;
            string q = query.Trim();
            foreach (var n in All())
            {
                int ti = (n.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
                int bi = (n.Body ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
                if (ti < 0 && bi < 0) continue;
                string snippet = bi >= 0 ? Snip(n.Body, bi, q.Length) : Snip(n.Body, 0, 0);
                hits.Add(new SearchHit { Note = n, Snippet = snippet });
                if (hits.Count >= max) break;
            }
            return hits;
        }

        static string Snip(string body, int at, int len)
        {
            if (string.IsNullOrEmpty(body)) return "";
            int start = Math.Max(0, at - 30);
            int end = Math.Min(body.Length, at + len + 50);
            string s = body.Substring(start, end - start).Replace('\n', ' ').Trim();
            return (start > 0 ? "…" : "") + s + (end < body.Length ? "…" : "");
        }

        string PathFor(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return null;
            foreach (char c in id) if (!(char.IsLetterOrDigit(c) || c == '-')) return null;
            return Path.Combine(Folder, id + ".md");
        }

        static Note TryLoad(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > Importers.MaxFileBytes) return null;
                string text = Importers.ReadText(File.ReadAllBytes(path)).Replace("\r\n", "\n");
                string title = "Untitled", body = text;
                if (text.StartsWith("# ", StringComparison.Ordinal))
                {
                    int nl = text.IndexOf('\n');
                    title = (nl < 0 ? text.Substring(2) : text.Substring(2, nl - 2)).Trim();
                    body = nl < 0 ? "" : text.Substring(nl + 1).TrimStart('\n');
                }
                return new Note { Id = Path.GetFileNameWithoutExtension(path), Title = title, Body = body, Modified = fi.LastWriteTimeUtc };
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>Writes to a temp file and swaps it in, so a crash never leaves a half-written note.</summary>
        public static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null, true);
            else File.Move(tmp, path);
        }
    }
}
