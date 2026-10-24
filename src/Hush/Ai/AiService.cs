using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hush.Core;

namespace Hush.Ai
{
    public sealed class AiResult
    {
        public string Text { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string Route { get; set; }
        public double? CostUsd { get; set; }
    }

    public sealed class AiException : Exception
    {
        public AiException(string message, Exception inner = null) : base(message, inner) { }
    }

    public interface IAiProvider
    {
        ProviderKind Kind { get; }
        string Name { get; }
        bool IsAvailable();
        Task<AiResult> CompleteAsync(string system, string user, Tier tier, CancellationToken ct);
    }

    /// <summary>Routes AI requests to the best available provider and model for the task.</summary>
    public sealed class AiService
    {
        readonly Func<Settings> _settings;
        readonly Dictionary<ProviderKind, IAiProvider> _providers = new Dictionary<ProviderKind, IAiProvider>();

        public AiService(Func<Settings> settings, IEnumerable<IAiProvider> providers)
        {
            _settings = settings;
            foreach (var p in providers) _providers[p.Kind] = p;
        }

        public static AiService CreateDefault(Func<Settings> settings, string sandboxDir)
        {
            return new AiService(settings, new IAiProvider[]
            {
                new ClaudeCodeProvider(sandboxDir),
                new AnthropicApiProvider(() => settings().GetApiKey()),
                new OllamaProvider(() => settings().OllamaModel)
            });
        }

        public IAiProvider Provider(ProviderKind k) => _providers.TryGetValue(k, out var p) ? p : null;

        bool Avail(ProviderKind k) { var p = Provider(k); return p != null && SafeAvailable(p); }

        static bool SafeAvailable(IAiProvider p) { try { return p.IsAvailable(); } catch { return false; } }

        public RouteDecision Plan(AiTask task, string text)
        {
            var s = _settings();
            Enum.TryParse(s.AiModel, out ModelPreference model);
            Enum.TryParse(s.AiProvider, out ProviderPreference pref);
            return ModelRouter.Route(task, ScriptParser.WordCount(text), model, pref,
                Avail(ProviderKind.ClaudeCode), Avail(ProviderKind.AnthropicApi), Avail(ProviderKind.Ollama));
        }

        public async Task<AiResult> RunAsync(AiTask task, string script, string instruction, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(script) && task != AiTask.Custom)
                throw new AiException("The note is empty.");
            var plan = Plan(task, script);
            if (plan.Providers.Count == 0)
                throw new AiException("No AI provider is available. Install and sign in to Claude Code, add an API key in Settings, or run Ollama.");

            string system = Prompts.System;
            string user = Prompts.Build(task, script ?? "", instruction);
            var errors = new List<string>();
            foreach (var kind in plan.Providers)
            {
                ct.ThrowIfCancellationRequested();
                var p = _providers[kind];
                try
                {
                    var r = await p.CompleteAsync(system, user, plan.Tier, ct).ConfigureAwait(false);
                    if (r == null || string.IsNullOrWhiteSpace(r.Text)) throw new AiException("The model returned no text.");
                    r.Text = Prompts.CleanOutput(r.Text);
                    r.Route = plan.Tier + " (" + plan.Reason + ")";
                    return r;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    errors.Add(p.Name + ": " + e.Message);
                }
            }
            throw new AiException("Every provider failed.\n" + string.Join("\n", errors));
        }
    }
}
