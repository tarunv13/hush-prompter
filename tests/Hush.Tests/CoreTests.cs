using System;
using System.IO;
using System.Linq;
using Hush.Core;
using static Hush.Tests.Program;

namespace Hush.Tests
{
    static class CoreTests
    {
        public static void Register()
        {
            // ---------------- script parsing ----------------
            Test("Script: plain text is one cue with an automatic title", () =>
            {
                var c = ScriptParser.Parse("Hello everyone and welcome to the quarterly update today.");
                Assert.Equal(1, c.Count);
                Assert.Equal("Hello everyone and welcome to…", c[0].Title);
            });
            Test("Script: headings and dividers start cues", () =>
            {
                var c = ScriptParser.Parse("Intro words.\n\n## Roadmap\nWe ship in May.\n---\nQuestions welcome.\n# Close\nThanks.");
                Assert.Equal(4, c.Count);
                Assert.Equal("Roadmap", c[1].Title);
                Assert.Equal("We ship in May.", c[1].Text);
                Assert.Equal("Questions welcome.", c[2].Text);
                Assert.Equal("Close", c[3].Title);
            });
            Test("Script: markdown syntax is not read aloud", () =>
            {
                string t = ScriptParser.Clean("**Bold** and _italic_ and `code` and [a link](http://x.y) and ~~old~~\n- item\n> quote");
                Assert.Equal("Bold and italic and code and a link and old\n• item\nquote", t);
            });
            Test("Script: snake_case and maths survive the italic rule", () =>
            {
                Assert.Equal("use file_name_here and 2*3*4", ScriptParser.Clean("use file_name_here and 2*3*4"));
            });
            Test("Script: a heading with no body still makes a cue", () =>
            {
                var c = ScriptParser.Parse("## Only a title");
                Assert.Equal(1, c.Count); Assert.Equal("Only a title", c[0].Title); Assert.Equal("", c[0].Text);
            });
            Test("Script: empty input gives no cues", () => Assert.Equal(0, ScriptParser.Parse("").Count));
            Test("Script: word count and reading time", () =>
            {
                Assert.Equal(6, ScriptParser.WordCount("It's a well-known fact, isn't it?"));
                Assert.Equal(60.0, ScriptParser.ReadingTime(string.Join(" ", Enumerable.Repeat("word", 140))).TotalSeconds);
            });
            Test("Script: CRLF and CR line endings", () =>
            {
                var c = ScriptParser.Parse("A\r\n## B\r\nText\rMore");
                Assert.Equal(2, c.Count); Assert.Equal("Text\nMore", c[1].Text);
            });

            // ---------------- scroll engine ----------------
            Test("Engine: countdown holds the text still", () =>
            {
                var e = Engine(ScrollMode.Continuous); e.StartCountdown(3);
                Step(e, 2.9); Assert.Equal(0.0, e.Offset);
                Step(e, 1.5); Assert.True(e.Offset > 0, "moves after countdown");
            });
            Test("Engine: continuous mode reaches the set speed", () =>
            {
                var e = Engine(ScrollMode.Continuous); e.SetSpeedStep(6); // 30 px/s
                Step(e, 2); double a = e.Offset; Step(e, 1);
                Assert.Near(30, e.Offset - a, 1.5, "px in one second at steady state");
            });
            Test("Engine: voice mode moves only while speaking", () =>
            {
                var e = Engine(ScrollMode.Voice);
                Step(e, 1); Assert.Equal(0.0, e.Offset, "silent");
                e.Speaking = true; Step(e, 2); double moved = e.Offset; Assert.True(moved > 40, "moved while speaking: " + moved);
                e.Speaking = false; Step(e, 1.5); double after = e.Offset;
                Step(e, 2); Assert.Near(after, e.Offset, 0.01, "stopped after speech ends");
            });
            Test("Engine: tempo factor scales speed and is clamped", () =>
            {
                var e = Engine(ScrollMode.Voice); e.Speaking = true; e.TempoFactor = 1.5;
                Step(e, 3); double a = e.Offset; Step(e, 1);
                Assert.Near(45, e.Offset - a, 2);
                e.TempoFactor = 9; Assert.Equal(1.8, e.TempoFactor);
                e.TempoFactor = 0.01; Assert.Equal(0.5, e.TempoFactor);
            });
            Test("Engine: hover pause and user pause stop motion", () =>
            {
                var e = Engine(ScrollMode.Continuous); Step(e, 1);
                e.HoverPaused = true; Step(e, 1); double a = e.Offset; Step(e, 1); Assert.Near(a, e.Offset, 0.01);
                e.HoverPaused = false; e.UserPaused = true; Step(e, 2); double b = e.Offset; Step(e, 1); Assert.Near(b, e.Offset, 0.01);
            });
            Test("Engine: never scrolls past the end and reports it", () =>
            {
                var e = Engine(ScrollMode.Continuous); e.SetBounds(0, 50); e.SetSpeedStep(14);
                Step(e, 5); Assert.Equal(50.0, e.Offset); Assert.True(e.AtEnd);
            });
            Test("Engine: speed steps stop at both ends, slowest is 4 px/s", () =>
            {
                var e = new ScrollEngine();
                for (int i = 0; i < 40; i++) e.Slower(); Assert.Equal(4.0, e.Speed);
                for (int i = 0; i < 40; i++) e.Faster(); Assert.Equal(120.0, e.Speed);
            });
            Test("Engine: a stalled frame cannot jump the text", () =>
            {
                var e = Engine(ScrollMode.Continuous); e.SetSpeedStep(14); Step(e, 2);
                double a = e.Offset; e.Tick(10); Assert.True(e.Offset - a <= 120 * 0.25 + 0.01, "capped to a quarter second");
            });
            Test("Engine: word follow eases to the target", () =>
            {
                var e = Engine(ScrollMode.WordFollow); e.FollowTarget = 200;
                Step(e, 4); Assert.Near(200, e.Offset, 3);
            });
            Test("Engine: nudge and jump are clamped", () =>
            {
                var e = Engine(ScrollMode.Voice); e.SetBounds(0, 100);
                e.Nudge(-50); Assert.Equal(0.0, e.Offset); e.JumpTo(500); Assert.Equal(100.0, e.Offset);
            });

            // ---------------- word aligner ----------------
            Test("Aligner: follows in-order speech", () =>
            {
                var a = new WordAligner("Good morning everyone. Today we look at the roadmap for next year.");
                Assert.True(a.Feed("good morning")); Assert.Equal(2, a.Cursor);
                Assert.True(a.Feed("today we look")); Assert.Equal(6, a.Cursor);
            });
            Test("Aligner: tolerates a dropped small word", () =>
            {
                var a = new WordAligner("we look at the roadmap for next year");
                Assert.True(a.Feed("look the roadmap")); Assert.Equal(5, a.Cursor);
            });
            Test("Aligner: one stray short word does not move the cursor", () =>
            {
                var a = new WordAligner("the plan is simple and the team is ready for the launch");
                Assert.False(a.Feed("the")); Assert.Equal(0, a.Cursor);
            });
            Test("Aligner: ignores text far ahead of the reader", () =>
            {
                var words = string.Join(" ", Enumerable.Range(0, 200).Select(i => "w" + i));
                var a = new WordAligner(words + " distant phrase here");
                Assert.False(a.Feed("distant phrase here"));
            });
            Test("Aligner: never moves backwards on a repeated phrase", () =>
            {
                var a = new WordAligner("thank you all. more content here. thank you all again");
                a.Feed("thank you all"); a.Feed("more content here"); int c = a.Cursor;
                a.Feed("thank you"); Assert.True(a.Cursor >= c);
            });
            Test("Aligner: punctuation, case, accents and curly apostrophes", () =>
            {
                var a = new WordAligner("Don’t panic — Café opens at NINE.");
                Assert.True(a.Feed("don't panic cafe")); Assert.Equal(3, a.Cursor);
                Assert.True(a.Vocabulary().Contains("don't"));
            });
            Test("Aligner: reset jumps to a cue", () =>
            {
                var a = new WordAligner("one two three four five six");
                a.Reset(4); Assert.Equal(4, a.Cursor); Assert.True(a.Feed("five six")); Assert.Equal(6, a.Cursor);
            });

            // ---------------- notes ----------------
            Test("Notes: create, save, reload, search, delete", () =>
            {
                var dir = TempDir();
                var s = new NoteStore(dir);
                var n = s.Create("Pitch", "We help farmers enrol in crop insurance.");
                n.Body += "\nSecond line about Odisha."; s.Save(n);
                var again = new NoteStore(dir).Get(n.Id);
                Assert.Equal("Pitch", again.Title); Assert.Contains("Odisha", again.Body);
                var hits = s.Search("odisha"); Assert.Equal(1, hits.Count); Assert.Contains("Odisha", hits[0].Snippet);
                s.Delete(n.Id); Assert.True(s.Get(n.Id) == null);
            });
            Test("Notes: title with newlines stays one line", () =>
            {
                var s = new NoteStore(TempDir());
                var n = s.Create("A\nB", "body"); var r = s.Get(n.Id);
                Assert.Equal("A B", r.Title); Assert.Equal("body", r.Body);
            });
            Test("Notes: unicode survives a round trip", () =>
            {
                var s = new NoteStore(TempDir());
                var n = s.Create("नमस्ते", "émoji \U0001F3A4 and हिंदी"); Assert.Equal("émoji \U0001F3A4 and हिंदी", s.Get(n.Id).Body);
            });

            // ---------------- settings ----------------
            Test("Settings: save and load round trip", () =>
            {
                string p = Path.Combine(TempDir(), "s.json");
                var s = new Settings { FontSize = 30, TextColor = "#A8F0D0", Mode = "WordFollow", CalibratedNoiseDb = -55.5, FloatLeft = 120 };
                s.Save(p);
                var r = Settings.Load(p);
                Assert.Equal(30.0, r.FontSize); Assert.Equal("#A8F0D0", r.TextColor); Assert.Equal(ScrollMode.WordFollow, r.ScrollModeValue);
                Assert.Equal(-55.5, r.CalibratedNoiseDb.Value); Assert.Equal(120.0, r.FloatLeft); Assert.True(double.IsNaN(r.FloatTop));
            });
            Test("Settings: tampered values are clamped", () =>
            {
                string p = Path.Combine(TempDir(), "s.json");
                File.WriteAllText(p, "{\"FontSize\":99999,\"SpeedStep\":-5,\"TextColor\":\"javascript:alert(1)\",\"Mode\":\"Hack\",\"PauseMs\":1e308,\"AiProvider\":\"Evil\",\"NotchWidth\":\"NaN\"}");
                var r = Settings.Load(p);
                Assert.Equal(72.0, r.FontSize); Assert.Equal(0, r.SpeedStep); Assert.Equal("#F4F5F8", r.TextColor);
                Assert.Equal("Voice", r.Mode); Assert.Equal(2000.0, r.PauseMs); Assert.Equal("Auto", r.AiProvider); Assert.Equal(560.0, r.NotchWidth);
            });
            Test("Settings: a corrupt file falls back to defaults", () =>
            {
                string p = Path.Combine(TempDir(), "s.json");
                File.WriteAllText(p, "{ this is not json");
                Assert.Equal(22.0, Settings.Load(p).FontSize);
            });
            Test("Settings: deeply nested JSON is refused", () =>
            {
                string p = Path.Combine(TempDir(), "s.json");
                File.WriteAllText(p, new string('[', 5000) + new string(']', 5000));
                Assert.Equal(22.0, Settings.Load(p).FontSize);
            });

            // ---------------- router ----------------
            Test("Router: each task gets the expected tier", () =>
            {
                Assert.Equal(Tier.Quick, R(AiTask.SplitCues, 100).Tier);
                Assert.Equal(Tier.Quick, R(AiTask.Bullets, 100).Tier);
                Assert.Equal(Tier.Balanced, R(AiTask.Speakable, 100).Tier);
                Assert.Equal(Tier.Balanced, R(AiTask.Shorten, 100).Tier);
                Assert.Equal(Tier.Deep, R(AiTask.Coach, 100).Tier);
            });
            Test("Router: long scripts move up a tier", () =>
            {
                Assert.Equal(Tier.Balanced, R(AiTask.SplitCues, 4000).Tier);
                Assert.Equal(Tier.Deep, R(AiTask.Speakable, 12000).Tier);
            });
            Test("Router: a fixed model choice wins", () =>
            {
                var d = ModelRouter.Route(AiTask.Coach, 50, ModelPreference.Quick, ProviderPreference.Auto, true, true, true);
                Assert.Equal(Tier.Quick, d.Tier);
            });
            Test("Router: provider order and availability", () =>
            {
                var d = ModelRouter.Route(AiTask.Speakable, 50, ModelPreference.Auto, ProviderPreference.Auto, true, true, true);
                Assert.Equal("ClaudeCode,AnthropicApi,Ollama", string.Join(",", d.Providers));
                d = ModelRouter.Route(AiTask.Speakable, 50, ModelPreference.Auto, ProviderPreference.Auto, false, false, true);
                Assert.Equal("Ollama", string.Join(",", d.Providers));
                d = ModelRouter.Route(AiTask.Speakable, 50, ModelPreference.Auto, ProviderPreference.AnthropicApi, true, false, true);
                Assert.Equal(0, d.Providers.Count, "explicit provider that is unavailable");
            });
            Test("Router: model names", () =>
            {
                Assert.Equal("haiku", ModelRouter.ClaudeCodeAlias(Tier.Quick));
                Assert.Equal("claude-sonnet-5-5", ModelRouter.ApiModel(Tier.Balanced));
                Assert.Equal("claude-opus-5-5", ModelRouter.ApiModel(Tier.Deep));
            });
        }

        static RouteDecision R(AiTask t, int words) => ModelRouter.Route(t, words, ModelPreference.Auto, ProviderPreference.Auto, true, true, true);

        static ScrollEngine Engine(ScrollMode m)
        {
            var e = new ScrollEngine { Mode = m };
            e.SetBounds(0, 100000);
            return e;
        }

        static void Step(ScrollEngine e, double seconds)
        {
            for (double t = 0; t < seconds - 1e-9; t += 1.0 / 60) e.Tick(1.0 / 60);
        }

        public static string TempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "hush-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
