using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hush.Core
{
    /// <summary>A word of the script and where it sits in the prompter text.</summary>
    public struct ScriptWord
    {
        public string Norm;
        public string Raw;
        public int CharIndex;
        public int Length;
    }

    /// <summary>
    /// Keeps a reading cursor in the script by matching recognised words against the words just
    /// ahead of it. It only moves forward, except for an explicit <see cref="Reset"/>, and it needs
    /// two matching words in order (or one long, rare word) before it moves, so a stray
    /// recognition cannot throw the text off.
    /// </summary>
    public sealed class WordAligner
    {
        public IReadOnlyList<ScriptWord> Words => _words;
        public int Cursor { get; private set; }
        public int LookAhead { get; set; } = 40;
        public int LookBehind { get; set; } = 4;

        readonly List<ScriptWord> _words = new List<ScriptWord>();
        static readonly Regex WordRx = new Regex(@"[\p{L}\p{N}][\p{L}\p{N}'’]*", RegexOptions.Compiled);

        public WordAligner(string text)
        {
            foreach (Match m in WordRx.Matches(text ?? ""))
                _words.Add(new ScriptWord { Norm = Normalize(m.Value), Raw = m.Value.ToLowerInvariant().Replace('’', '\''), CharIndex = m.Index, Length = m.Length });
        }

        public void Reset(int wordIndex = 0) => Cursor = Math.Max(0, Math.Min(_words.Count, wordIndex));

        /// <summary>Character index of the word the speaker is on.</summary>
        public int CursorCharIndex => _words.Count == 0 ? 0 : _words[Math.Min(Cursor, _words.Count - 1)].CharIndex;

        /// <summary>Distinct lower-case words as written (apostrophes kept), for a recognition grammar.</summary>
        public HashSet<string> Vocabulary()
        {
            var set = new HashSet<string>();
            foreach (var w in _words) if (w.Norm.Length > 0 && w.Raw.Length > 0) set.Add(w.Raw);
            return set;
        }

        /// <summary>
        /// Matches a recognised phrase. Returns true and advances the cursor past the match when the
        /// phrase fits the text ahead well enough.
        /// </summary>
        public bool Feed(string phrase)
        {
            var heard = new List<string>();
            foreach (Match m in WordRx.Matches(phrase ?? ""))
            {
                string n = Normalize(m.Value);
                if (n.Length > 0) heard.Add(n);
            }
            if (heard.Count == 0 || _words.Count == 0) return false;

            int start = Math.Max(0, Cursor - LookBehind);
            int end = Math.Min(_words.Count, Cursor + LookAhead);
            int bestScore = 0, bestEnd = -1, bestStart = int.MaxValue;

            for (int s = start; s < end; s++)
            {
                // Greedy in-order match of the heard words from script position s, allowing one
                // skipped script word between hits (people drop small words).
                int pos = s, score = 0, lastHit = -1, firstHit = -1, skips = 0;
                foreach (var h in heard)
                {
                    int found = -1;
                    for (int p = pos; p < Math.Min(end, pos + 3); p++)
                        if (_words[p].Norm == h) { found = p; break; }
                    if (found < 0) { if (++skips > 2) break; continue; }
                    if (firstHit < 0) firstHit = found;
                    score += _words[found].Norm.Length >= 6 ? 2 : 1;
                    lastHit = found;
                    pos = found + 1;
                }
                if (firstHit < 0) continue;
                // Prefer higher scores, then matches closest to the cursor.
                bool better = score > bestScore || (score == bestScore && Math.Abs(firstHit - Cursor) < Math.Abs(bestStart - Cursor));
                if (better) { bestScore = score; bestEnd = lastHit; bestStart = firstHit; }
            }

            if (bestEnd < 0 || bestScore < 2) return false;
            int next = bestEnd + 1;
            if (next <= Cursor && bestStart < Cursor - LookBehind) return false;
            if (next > Cursor) Cursor = Math.Min(_words.Count, next);
            return true;
        }

        public static string Normalize(string w)
        {
            if (string.IsNullOrEmpty(w)) return "";
            string d = w.ToLowerInvariant().Replace('’', '\'').Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
