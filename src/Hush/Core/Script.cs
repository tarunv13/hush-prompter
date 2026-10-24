using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Hush.Core
{
    /// <summary>One cue: a titled section of a script.</summary>
    public sealed class Cue
    {
        public string Title { get; set; }
        /// <summary>Prompter-ready text with markdown markers removed.</summary>
        public string Text { get; set; }
        public int Index { get; set; }
    }

    /// <summary>
    /// Splits a note into cues. A cue starts at a markdown heading (#, ##, ###) or after a divider
    /// line (---, ***, ___). Text before the first marker becomes the first cue.
    /// </summary>
    public static class ScriptParser
    {
        static readonly Regex Heading = new Regex(@"^\s{0,3}(#{1,3})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);
        static readonly Regex Divider = new Regex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);

        public static List<Cue> Parse(string text)
        {
            var cues = new List<Cue>();
            if (string.IsNullOrEmpty(text)) return cues;

            string title = null;
            var body = new StringBuilder();
            bool started = false;

            void Flush()
            {
                string cleaned = Clean(body.ToString());
                if (cleaned.Length > 0 || title != null)
                {
                    cues.Add(new Cue
                    {
                        Title = string.IsNullOrWhiteSpace(title) ? AutoTitle(cleaned, cues.Count + 1) : title.Trim(),
                        Text = cleaned,
                        Index = cues.Count
                    });
                }
                title = null;
                body.Clear();
            }

            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var h = Heading.Match(raw);
                if (h.Success)
                {
                    if (started) Flush();
                    title = StripInline(h.Groups[2].Value);
                    started = true;
                    continue;
                }
                if (Divider.IsMatch(raw))
                {
                    if (started || body.Length > 0) Flush();
                    started = true;
                    continue;
                }
                started = true;
                body.Append(raw).Append('\n');
            }
            Flush();
            return cues;
        }

        /// <summary>The whole script as one prompter text, cues separated by a blank line.</summary>
        public static string FullText(List<Cue> cues)
        {
            var sb = new StringBuilder();
            foreach (var c in cues)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(c.Text);
            }
            return sb.ToString();
        }

        static string AutoTitle(string cleaned, int n)
        {
            if (string.IsNullOrWhiteSpace(cleaned)) return "Section " + n;
            var words = cleaned.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int take = Math.Min(5, words.Length);
            string t = string.Join(" ", words, 0, take);
            return words.Length > take ? t + "…" : t;
        }

        /// <summary>Removes markdown syntax that should not be read aloud.</summary>
        public static string Clean(string md)
        {
            var lines = md.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                string line = l.TrimEnd();
                line = Regex.Replace(line, @"^\s{0,3}>\s?", "");                 // quotes
                line = Regex.Replace(line, @"^\s*[-*+]\s+\[[ xX]\]\s+", "• "); // task items
                line = Regex.Replace(line, @"^\s*[-*+]\s+", "• ");            // bullets
                line = StripInline(line);
                sb.Append(line).Append('\n');
            }
            // Collapse 3+ blank lines into one paragraph break.
            string s = Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n");
            return s.Trim('\n', ' ', '\t');
        }

        static string StripInline(string s)
        {
            s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");     // images
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");      // links
            s = Regex.Replace(s, @"`([^`]*)`", "$1");                  // code
            s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");          // bold
            s = Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", "$1"); // italic *
            s = Regex.Replace(s, @"(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])", "$1");   // italic _
            s = Regex.Replace(s, @"~~(.+?)~~", "$1");
            return s;
        }

        public static int WordCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}'’-]*").Count;
        }

        /// <summary>Estimated speaking time at a given rate (words per minute).</summary>
        public static TimeSpan ReadingTime(string text, double wpm = 140)
        {
            return TimeSpan.FromSeconds(Math.Round(WordCount(text) / Math.Max(60, wpm) * 60));
        }
    }
}
