using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Hush.Core;

namespace Hush.Ai
{
    /// <summary>
    /// Uses the Claude Code CLI in headless mode, so requests run on the user's own Claude Code
    /// login and plan. Hardening:
    /// - the executable is resolved from absolute PATH entries and the default install folder only;
    /// - the command line holds constants only; the script goes over stdin as UTF-8 bytes;
    /// - all tools are disabled, MCP servers and settings files (with their hooks) are not loaded,
    ///   and nothing is saved as a session;
    /// - it runs in an empty sandbox folder, so no project files or CLAUDE.md are in reach.
    /// </summary>
    public sealed class ClaudeCodeProvider : IAiProvider
    {
        public ProviderKind Kind => ProviderKind.ClaudeCode;
        public string Name => "Claude Code";
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(4);

        readonly string _sandbox;
        string _exe;
        bool _searched;

        public ClaudeCodeProvider(string sandboxDir)
        {
            _sandbox = sandboxDir;
        }

        public bool IsAvailable() => FindExecutable() != null;

        public string FindExecutable()
        {
            if (_searched) return _exe;
            _searched = true;
            var candidates = new List<string>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string d = dir.Trim().Trim('"');
                if (d.Length == 0 || !Path.IsPathRooted(d) || d.StartsWith("\\\\")) continue; // no relative or network entries
                candidates.Add(Path.Combine(d, "claude.exe"));
                candidates.Add(Path.Combine(d, "claude.cmd"));
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));
            _exe = candidates.FirstOrDefault(SafeExists);
            return _exe;
        }

        static bool SafeExists(string p) { try { return File.Exists(p); } catch { return false; } }

        /// <summary>The fixed argument list. No user text is ever placed here.</summary>
        public static List<string> BuildArguments(Tier tier, string systemPromptFile)
        {
            return new List<string>
            {
                "-p",
                "--model", ModelRouter.ClaudeCodeAlias(tier),
                "--output-format", "json",
                "--tools", "",
                "--no-session-persistence",
                "--strict-mcp-config",
                "--setting-sources", "",
                "--system-prompt-file", systemPromptFile
            };
        }

        /// <summary>Quotes one argument by the rules of CommandLineToArgvW.</summary>
        public static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes).Append(c); backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        public async Task<AiResult> CompleteAsync(string system, string user, Tier tier, CancellationToken ct)
        {
            string exe = FindExecutable() ?? throw new AiException("Claude Code is not installed.");
            Directory.CreateDirectory(_sandbox);
            string spFile = Path.Combine(_sandbox, "system-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(spFile, system, new UTF8Encoding(false));
            try
            {
                var args = BuildArguments(tier, spFile);
                string argLine = string.Join(" ", args.Select(QuoteArg));
                var psi = new ProcessStartInfo
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = _sandbox
                };
                if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    // npm installs ship a .cmd shim. Every token here is a constant or our own path,
                    // and none contains cmd metacharacters.
                    if (argLine.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%' }) >= 0 || exe.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"' }) >= 0)
                        throw new AiException("Unsafe characters in the Claude Code path.");
                    psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    psi.Arguments = "/d /s /c \"\"" + exe + "\" " + argLine + "\"";
                }
                else
                {
                    psi.FileName = exe;
                    psi.Arguments = argLine;
                }
                psi.EnvironmentVariables["NO_COLOR"] = "1";
                psi.EnvironmentVariables["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

                string stdout, stderr;
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    byte[] input = Encoding.UTF8.GetBytes(user);
                    await proc.StandardInput.BaseStream.WriteAsync(input, 0, input.Length, ct).ConfigureAwait(false);
                    proc.StandardInput.Close();

                    using (var timeout = new CancellationTokenSource(Timeout))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
                    {
                        var exited = new TaskCompletionSource<bool>();
                        proc.EnableRaisingEvents = true;
                        proc.Exited += (s, e) => exited.TrySetResult(true);
                        if (proc.HasExited) exited.TrySetResult(true);
                        using (linked.Token.Register(() => exited.TrySetCanceled()))
                        {
                            try { await exited.Task.ConfigureAwait(false); }
                            catch (TaskCanceledException)
                            {
                                KillTree(proc);
                                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                                throw new AiException("Claude Code did not answer within " + (int)Timeout.TotalSeconds + " seconds.");
                            }
                        }
                    }
                    stdout = await outTask.ConfigureAwait(false);
                    stderr = await errTask.ConfigureAwait(false);
                }
                return Parse(stdout, stderr, tier);
            }
            finally
            {
                try { File.Delete(spFile); } catch { }
            }
        }

        public static AiResult Parse(string stdout, string stderr, Tier tier)
        {
            Dictionary<string, object> d = null;
            try
            {
                var ser = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 };
                d = ser.DeserializeObject((stdout ?? "").Trim()) as Dictionary<string, object>;
            }
            catch (Exception) { }
            if (d == null)
            {
                string msg = FirstLine(stderr) ?? FirstLine(stdout) ?? "no output";
                if (msg.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0)
                    msg += " (run \"claude\" once in a terminal to sign in)";
                throw new AiException("Claude Code failed: " + msg);
            }
            bool isError = d.TryGetValue("is_error", out object e) && e is bool b && b;
            string result = d.TryGetValue("result", out object r) ? r as string : null;
            if (isError || string.IsNullOrWhiteSpace(result))
                throw new AiException("Claude Code returned an error: " + (result ?? "unknown"));
            var res = new AiResult { Text = result, Provider = "Claude Code", Model = ModelRouter.ClaudeCodeAlias(tier) };
            if (d.TryGetValue("modelUsage", out object mu) && mu is Dictionary<string, object> mud && mud.Count > 0)
                res.Model = string.Join(", ", mud.Keys);
            if (d.TryGetValue("total_cost_usd", out object c))
            {
                try { res.CostUsd = Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture); } catch { }
            }
            return res;
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string l = s.Trim().Split('\n')[0].Trim();
            return l.Length > 300 ? l.Substring(0, 300) : l;
        }

        static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                // taskkill /T ends the CLI and any child process it started.
                using (var k = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + p.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                    k?.WaitForExit(5000);
            }
            catch { try { p.Kill(); } catch { } }
        }
    }
}
