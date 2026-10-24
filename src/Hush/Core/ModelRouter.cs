using System;
using System.Collections.Generic;

namespace Hush.Core
{
    public enum AiTask { SplitCues, Bullets, Speakable, Shorten, Coach, Custom }

    /// <summary>How much model a task needs.</summary>
    public enum Tier { Quick, Balanced, Deep }

    public enum ProviderKind { ClaudeCode, AnthropicApi, Ollama }

    public enum ProviderPreference { Auto, ClaudeCode, AnthropicApi, Ollama }

    public enum ModelPreference { Auto, Quick, Balanced, Deep }

    public sealed class RouteDecision
    {
        public Tier Tier { get; set; }
        public List<ProviderKind> Providers { get; set; } = new List<ProviderKind>();
        public string Reason { get; set; }
    }

    /// <summary>
    /// Picks the model tier and the order of providers to try for an AI request. Short, mechanical
    /// edits go to the fastest model; rewriting for the voice goes to the balanced one; coaching,
    /// which reads a whole rehearsal, goes to the deepest. Long scripts move a quick task up a tier.
    /// </summary>
    public static class ModelRouter
    {
        public const int LongScriptWords = 2500;
        public const int VeryLongScriptWords = 9000;

        public static Tier BaseTier(AiTask task)
        {
            switch (task)
            {
                case AiTask.SplitCues:
                case AiTask.Bullets: return Tier.Quick;
                case AiTask.Speakable:
                case AiTask.Shorten:
                case AiTask.Custom: return Tier.Balanced;
                case AiTask.Coach: return Tier.Deep;
                default: return Tier.Balanced;
            }
        }

        public static RouteDecision Route(AiTask task, int words, ModelPreference model, ProviderPreference provider,
                                          bool claudeCodeAvailable, bool apiKeyAvailable, bool ollamaAvailable)
        {
            var d = new RouteDecision();
            if (model != ModelPreference.Auto)
            {
                d.Tier = model == ModelPreference.Quick ? Tier.Quick : model == ModelPreference.Balanced ? Tier.Balanced : Tier.Deep;
                d.Reason = "your model choice";
            }
            else
            {
                d.Tier = BaseTier(task);
                d.Reason = TaskReason(task);
                if (d.Tier == Tier.Quick && words > LongScriptWords) { d.Tier = Tier.Balanced; d.Reason = "long script"; }
                if (d.Tier == Tier.Balanced && words > VeryLongScriptWords) { d.Tier = Tier.Deep; d.Reason = "very long script"; }
            }

            void Add(ProviderKind k, bool ok) { if (ok && !d.Providers.Contains(k)) d.Providers.Add(k); }
            switch (provider)
            {
                case ProviderPreference.ClaudeCode: Add(ProviderKind.ClaudeCode, claudeCodeAvailable); break;
                case ProviderPreference.AnthropicApi: Add(ProviderKind.AnthropicApi, apiKeyAvailable); break;
                case ProviderPreference.Ollama: Add(ProviderKind.Ollama, ollamaAvailable); break;
                default:
                    // Your Claude Code login first, then an API key, then the offline model.
                    Add(ProviderKind.ClaudeCode, claudeCodeAvailable);
                    Add(ProviderKind.AnthropicApi, apiKeyAvailable);
                    Add(ProviderKind.Ollama, ollamaAvailable);
                    break;
            }
            return d;
        }

        static string TaskReason(AiTask t)
        {
            switch (t)
            {
                case AiTask.SplitCues: return "quick structural edit";
                case AiTask.Bullets: return "quick condensing";
                case AiTask.Coach: return "reads a whole rehearsal";
                default: return "rewriting for the voice";
            }
        }

        /// <summary>Alias the Claude Code CLI resolves to the newest model of that family.</summary>
        public static string ClaudeCodeAlias(Tier t) => t == Tier.Quick ? "haiku" : t == Tier.Balanced ? "sonnet" : "opus";

        /// <summary>Exact Claude API model id for a tier.</summary>
        public static string ApiModel(Tier t) => t == Tier.Quick ? "claude-haiku-5-5" : t == Tier.Balanced ? "claude-sonnet-5-5" : "claude-opus-5-5";

        /// <summary>Effort level sent to the API for a tier.</summary>
        public static string ApiEffort(Tier t) => t == Tier.Quick ? "low" : t == Tier.Balanced ? "medium" : "high";
    }
}
