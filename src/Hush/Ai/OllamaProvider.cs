using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Hush.Core;

namespace Hush.Ai
{
    /// <summary>
    /// Offline fallback through a local Ollama server. Only the loopback address is ever contacted.
    /// </summary>
    public sealed class OllamaProvider : IAiProvider
    {
        public const string BaseUrl = "http://127.0.0.1:11434";
        public ProviderKind Kind => ProviderKind.Ollama;
        public string Name => "Ollama (local)";

        readonly Func<string> _preferred;
        DateTime _checkedAt = DateTime.MinValue;
        List<string> _models = new List<string>();

        public OllamaProvider(Func<string> preferredModel) { _preferred = preferredModel; }

        public bool IsAvailable() => ListModels().Count > 0;

        /// <summary>Chat-capable models (embedding models are skipped). Cached for 30 seconds.</summary>
        public List<string> ListModels()
        {
            if ((DateTime.UtcNow - _checkedAt).TotalSeconds < 30) return _models;
            _checkedAt = DateTime.UtcNow;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/tags");
                req.Timeout = 800; req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var d = new JavaScriptSerializer().DeserializeObject(sr.ReadToEnd()) as Dictionary<string, object>;
                    var list = new List<string>();
                    if (d != null && d.TryGetValue("models", out object m) && m is object[] arr)
                        foreach (var o in arr)
                            if (o is Dictionary<string, object> md && md.TryGetValue("name", out object n) && n is string name && name.IndexOf("embed", StringComparison.OrdinalIgnoreCase) < 0)
                                list.Add(name);
                    _models = list;
                }
            }
            catch (Exception) { _models = new List<string>(); }
            return _models;
        }

        public async Task<AiResult> CompleteAsync(string system, string user, Tier tier, CancellationToken ct)
        {
            var models = ListModels();
            if (models.Count == 0) throw new AiException("Ollama is not running.");
            string pref = _preferred();
            string model = !string.IsNullOrEmpty(pref) && models.Contains(pref) ? pref : models.First();

            var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = new object[]
                {
                    new Dictionary<string, object> { ["role"] = "system", ["content"] = system },
                    new Dictionary<string, object> { ["role"] = "user", ["content"] = user }
                }
            });
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/chat");
            req.Method = "POST"; req.ContentType = "application/json"; req.Proxy = null;
            req.Timeout = req.ReadWriteTimeout = 600000;
            byte[] payload = Encoding.UTF8.GetBytes(body);
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            {
                try
                {
                    using (var s = await req.GetRequestStreamAsync().ConfigureAwait(false))
                        await s.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                    using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false))
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var d = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(await sr.ReadToEndAsync().ConfigureAwait(false)) as Dictionary<string, object>;
                        string text = d != null && d.TryGetValue("message", out object m) && m is Dictionary<string, object> md && md.TryGetValue("content", out object c) ? c as string : null;
                        if (string.IsNullOrWhiteSpace(text)) throw new AiException("Ollama returned no text.");
                        // Some local models wrap reasoning in <think> tags; drop it.
                        text = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
                        return new AiResult { Text = text, Provider = "Ollama (local)", Model = model, CostUsd = 0 };
                    }
                }
                catch (WebException we) when (ct.IsCancellationRequested) { throw new OperationCanceledException("Cancelled", we, ct); }
                catch (WebException we) { throw new AiException("Ollama error: " + we.Message, we); }
            }
        }
    }
}
