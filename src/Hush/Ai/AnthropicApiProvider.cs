using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Hush.Core;

namespace Hush.Ai
{
    /// <summary>
    /// Calls the Claude Messages API over HTTPS with an API key the user saved (DPAPI-encrypted).
    /// Raw HTTP is used instead of the SDK to keep the app free of third-party packages.
    /// TLS 1.2 or newer only, certificate validation left on, the key sent only to api.anthropic.com.
    /// </summary>
    public sealed class AnthropicApiProvider : IAiProvider
    {
        public const string Endpoint = "https://api.anthropic.com/v1/messages";
        public ProviderKind Kind => ProviderKind.AnthropicApi;
        public string Name => "Claude API";

        readonly Func<string> _key;

        public AnthropicApiProvider(Func<string> key) { _key = key; }

        public bool IsAvailable() => !string.IsNullOrEmpty(_key());

        public static string BuildBody(string system, string user, Tier tier)
        {
            string model = ModelRouter.ApiModel(tier);
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["max_tokens"] = 16000,
                ["system"] = system,
                ["messages"] = new object[] { new Dictionary<string, object> { ["role"] = "user", ["content"] = user } },
                ["output_config"] = new Dictionary<string, object> { ["effort"] = ModelRouter.ApiEffort(tier) }
            };
            // Server-side fallback when a safety classifier declines (Opus and Sonnet tiers).
            if (tier != Tier.Quick) body["fallbacks"] = "default";
            return new JavaScriptSerializer().Serialize(body);
        }

        public async Task<AiResult> CompleteAsync(string system, string user, Tier tier, CancellationToken ct)
        {
            string key = _key();
            if (string.IsNullOrEmpty(key)) throw new AiException("No API key saved.");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            var req = (HttpWebRequest)WebRequest.Create(Endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = req.ReadWriteTimeout = 300000;
            req.AllowAutoRedirect = false; // never forward the key to another host
            req.Headers["x-api-key"] = key;
            req.Headers["anthropic-version"] = "2023-06-01";
            if (tier != Tier.Quick) req.Headers["anthropic-beta"] = "server-side-fallback-2026-07-01";

            byte[] payload = Encoding.UTF8.GetBytes(BuildBody(system, user, tier));
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            {
                try
                {
                    using (var s = await req.GetRequestStreamAsync().ConfigureAwait(false))
                        await s.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                    using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        return Parse(await sr.ReadToEndAsync().ConfigureAwait(false), tier);
                }
                catch (WebException we) when (ct.IsCancellationRequested) { throw new OperationCanceledException("Cancelled", we, ct); }
                catch (WebException we)
                {
                    string detail = "";
                    if (we.Response is HttpWebResponse er)
                    {
                        using (er)
                        using (var sr = new StreamReader(er.GetResponseStream(), Encoding.UTF8))
                            detail = ErrorMessage(sr.ReadToEnd()) ?? ((int)er.StatusCode).ToString();
                        throw new AiException("Claude API error " + (int)er.StatusCode + ": " + detail, we);
                    }
                    throw new AiException("Could not reach the Claude API: " + we.Message, we);
                }
            }
        }

        public static AiResult Parse(string json, Tier tier)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 64 };
            var d = ser.DeserializeObject(json) as Dictionary<string, object> ?? throw new AiException("Unreadable API response.");
            string stop = d.TryGetValue("stop_reason", out object sr) ? sr as string : null;
            if (stop == "refusal") throw new AiException("The model declined this request.");
            var sb = new StringBuilder();
            if (d.TryGetValue("content", out object c) && c is object[] blocks)
            {
                foreach (var b in blocks)
                    if (b is Dictionary<string, object> bd && bd.TryGetValue("type", out object t) && (t as string) == "text" && bd.TryGetValue("text", out object tx))
                        sb.Append(tx as string);
            }
            if (sb.Length == 0) throw new AiException("The API returned no text" + (stop != null ? " (stop reason: " + stop + ")" : "") + ".");
            return new AiResult
            {
                Text = sb.ToString(),
                Provider = "Claude API",
                Model = d.TryGetValue("model", out object m) ? m as string : ModelRouter.ApiModel(tier)
            };
        }

        static string ErrorMessage(string json)
        {
            try
            {
                var d = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (d != null && d.TryGetValue("error", out object e) && e is Dictionary<string, object> ed && ed.TryGetValue("message", out object m))
                    return m as string;
            }
            catch { }
            return null;
        }
    }
}
