using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Hush.Tests
{
    /// <summary>
    /// Minimal test runner. Usage: Hush.Tests.exe [--live] [filter]
    /// --live also runs the tests that call the real Claude Code CLI and a local Ollama server.
    /// </summary>
    public static class Program
    {
        static readonly List<(string Name, Action Body, bool Live)> Tests = new List<(string, Action, bool)>();
        public static void Test(string name, Action body) => Tests.Add((name, body, false));
        public static void Live(string name, Action body) => Tests.Add((name, body, true));

        public static int Main(string[] args)
        {
            bool live = args.Contains("--live");
            string filter = args.FirstOrDefault(a => !a.StartsWith("--"));

            CoreTests.Register();
            AudioTests.Register();
            SecurityTests.Register();
            AiTests.Register();

            int pass = 0, fail = 0, skipped = 0;
            var sw = Stopwatch.StartNew();
            foreach (var t in Tests)
            {
                if (filter != null && t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.Live && !live) { skipped++; continue; }
                var tsw = Stopwatch.StartNew();
                try
                {
                    t.Body();
                    pass++;
                    Console.WriteLine($"  PASS  {t.Name} ({tsw.ElapsedMilliseconds} ms)");
                }
                catch (Exception e)
                {
                    fail++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  FAIL  {t.Name}: {e.GetType().Name}: {e.Message}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine($"\n{pass} passed, {fail} failed, {skipped} live tests skipped, {sw.Elapsed.TotalSeconds:0.0} s");
            return fail == 0 ? 0 : 1;
        }
    }

    public static class Assert
    {
        public static void True(bool c, string msg = "expected true") { if (!c) throw new Exception(msg); }
        public static void False(bool c, string msg = "expected false") { if (c) throw new Exception(msg); }
        public static void Equal<T>(T expected, T actual, string msg = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception((msg != null ? msg + ": " : "") + $"expected <{expected}> but got <{actual}>");
        }
        public static void Near(double expected, double actual, double tol, string msg = null)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > tol)
                throw new Exception((msg != null ? msg + ": " : "") + $"expected {expected}±{tol} but got {actual}");
        }
        public static void Contains(string needle, string hay, string msg = null)
        {
            if (hay == null || hay.IndexOf(needle, StringComparison.Ordinal) < 0) throw new Exception((msg ?? "missing") + $": <{needle}> not in <{Trunc(hay)}>");
        }
        public static void NotContains(string needle, string hay, string msg = null)
        {
            if (hay != null && hay.IndexOf(needle, StringComparison.Ordinal) >= 0) throw new Exception((msg ?? "unexpected") + $": <{needle}> in <{Trunc(hay)}>");
        }
        public static T Throws<T>(Action a, string msg = null) where T : Exception
        {
            try { a(); }
            catch (T e) { return e; }
            catch (Exception e) { throw new Exception((msg ?? "wrong exception") + $": expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}"); }
            throw new Exception((msg ?? "no exception") + $": expected {typeof(T).Name}");
        }
        static string Trunc(string s) => s == null ? "null" : (s.Length > 200 ? s.Substring(0, 200) + "…" : s);
    }
}
