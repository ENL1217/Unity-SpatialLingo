// Copyright (c) Meta Platforms, Inc. and affiliates.
// AIEnglish fork: OpenRouter free-only transport. Public sample API is preserved.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Meta.Utilities.MetaModelAPI
{
    public partial class MetaModelRestApi
    {
        public const string MODEL_NAME_MUSE_SPARK_1_3 = OpenRouterRequest.DefaultModel;
        public const string MODEL_NAME_MUSE_SPARK_1_2 = OpenRouterRequest.DefaultModel;
        public const string MODEL_NAME_MUSE_SPARK_1_1 = OpenRouterRequest.DefaultModel;
        public static string DefaultModel { get; set; } = OpenRouterRequest.DefaultModel;
        public string Model { get; set; }
        // Kept for sample/Visual Scripting compatibility; no Meta-specific field is sent.
        public ReasoningEffort Effort { get; set; } = ReasoningEffort.Minimal;
        public static Func<Awaitable<(string apiKey, string endpointOverride)>> GetApiKeyAsync;
        public static string LastServiceMessage { get; private set; } = "OpenRouter not contacted";
        public static int SessionRequests { get; private set; }
        public static DateTime RetryAfterUtc { get; private set; }
        public const int SessionRequestLimit = 35;
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly Dictionary<string, string> Cache = new();
        private static DateTime s_nextRequest;
        private HttpClient m_client;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntime()
        {
            Cache.Clear();
            SessionRequests = 0;
            RetryAfterUtc = s_nextRequest = DateTime.MinValue;
            LastServiceMessage = "OpenRouter not contacted";
        }

        public MetaModelRestApi() => Model = DefaultModel;

        private async Awaitable<HttpClient> GetHttpClient()
        {
            if (m_client != null) return m_client;
            if (GetApiKeyAsync == null) throw new InvalidOperationException("OpenRouter configuration is missing.");
            var (key, endpoint) = await GetApiKeyAsync();
            OpenRouterRequest.ValidateEndpoint(endpoint);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OpenRouter API key is missing.");
            m_client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(OpenRouterRequest.BaseUrl),
                Timeout = TimeSpan.FromSeconds(90)
            };
            m_client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            m_client.DefaultRequestHeaders.UserAgent.ParseAdd("AIEnglish/0.1");
            m_client.DefaultRequestHeaders.Add("X-OpenRouter-Title", "AIEnglish Quest");
            return m_client;
        }

        public ModelChat StartNewChat(string prompt)
        {
            var chat = new ModelChat();
            chat.AddMessage(new ModelChatMessage(ModelChatMessage.ROLE_SYSTEM, prompt));
            return chat;
        }

        public bool AddChatHistoryAsUser(ModelChat chat, string text) => AddChatHistory(chat, ModelChatMessage.ROLE_USER, text);
        public bool AddChatHistoryAsAI(ModelChat chat, string text) => AddChatHistory(chat, ModelChatMessage.ROLE_ASSISTANT, text);
        private bool AddChatHistory(ModelChat chat, string role, string text)
        {
            var count = chat.MessageCount;
            return chat.AddMessage(new ModelChatMessage(role, text)) > count;
        }

        public async Awaitable<ModelChatResponse> ContinueChat(ModelChat history, string text)
        {
            history.AddMessage(new ModelChatMessage(ModelChatMessage.ROLE_USER, text));
            var (response, elapsed) = await Send(OpenRouterRequest.Text(Model, history));
            var message = Completion(response);
            if (message == null) return null;
            var assistant = new ModelChatMessage(ModelChatMessage.ROLE_ASSISTANT, message);
            history.AddMessage(assistant);
            return new ModelChatResponse(history, assistant, Usage(response, elapsed));
        }

        public async Awaitable<ModelImageResponse> ImageUnderstanding(string systemText, string userText, string[] images)
        {
            var (response, elapsed) = await Send(OpenRouterRequest.Image(Model, systemText, userText, images));
            var message = Completion(response);
            return message == null ? null : new ModelImageResponse(message, Usage(response, elapsed));
        }

        private static string Completion(ChatResponseDTO response) =>
            response?.choices is { Length: > 0 } ? response.choices[0]?.message?.content : null;

        private static ModelUsage Usage(ChatResponseDTO response, long elapsed) => new(
            response.usage?.prompt_tokens ?? 0, response.usage?.completion_tokens ?? 0,
            response.usage?.total_tokens ?? 0, response.model, response.choices[0].finish_reason, elapsed);

        [Serializable] private class Catalog { public CatalogModel[] data; }
        [Serializable] private class CatalogModel { public string id; public Pricing pricing; public Architecture architecture; }
        [Serializable] private class Architecture { public string[] input_modalities; }
        [Serializable] private class Pricing { public string prompt; public string completion; public string request; public string image; }
        private static bool Zero(string value, bool required = false) =>
            string.IsNullOrEmpty(value) ? !required :
            decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price == 0;

        // Check the live catalogue before every billable POST. A removed/repriced model fails closed.
        private async Task VerifyFreeModel(HttpClient client)
        {
            OpenRouterRequest.ValidateModel(Model);
            using var result = await client.GetAsync("models");
            result.EnsureSuccessStatusCode();
            var catalog = JsonUtility.FromJson<Catalog>(await result.Content.ReadAsStringAsync());
            foreach (var model in catalog?.data ?? Array.Empty<CatalogModel>())
            {
                if (model.id != Model) continue;
                var p = model.pricing;
                if (p != null && Zero(p.prompt, true) && Zero(p.completion, true) && Zero(p.request) && Zero(p.image) &&
                    Array.IndexOf(model.architecture?.input_modalities ?? Array.Empty<string>(), "image") >= 0)
                    return;
                break;
            }
            throw new InvalidOperationException("Selected vision model is unavailable or no longer free.");
        }

        private async Awaitable<(ChatResponseDTO response, long elapsed)> Send(string body)
        {
            var timer = Stopwatch.StartNew();
            await Gate.WaitAsync();
            try
            {
                if (Cache.TryGetValue(body, out var cached))
                    return (JsonUtility.FromJson<ChatResponseDTO>(cached), 0);
                if (DateTime.UtcNow < RetryAfterUtc) return (null, 0);
                if (SessionRequests >= SessionRequestLimit)
                {
                    LastServiceMessage = "Free request session limit reached (35).";
                    return (null, 0);
                }
                var client = await GetHttpClient();
                await VerifyFreeModel(client);
                var delay = s_nextRequest - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 0) await Task.Delay(delay);
                s_nextRequest = DateTime.UtcNow.AddSeconds(4);
                SessionRequests++;
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("chat/completions", content);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var wait = status is 401 or 402 or 403 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1);
                    var headerWait = response.Headers.RetryAfter;
                    if (headerWait?.Delta is { } delta && delta > wait) wait = delta;
                    if (headerWait?.Date is { } date && date.UtcDateTime - DateTime.UtcNow > wait)
                        wait = date.UtcDateTime - DateTime.UtcNow;
                    RetryAfterUtc = DateTime.UtcNow + wait;
                    LastServiceMessage = $"OpenRouter HTTP {status}; waiting before retry. No paid fallback.";
                    Debug.LogWarning(LastServiceMessage);
                    ModelServiceHealth.Report(ModelRequestOutcome.ServerError);
                    return (null, timer.ElapsedMilliseconds);
                }
                var json = await response.Content.ReadAsStringAsync();
                var parsed = JsonUtility.FromJson<ChatResponseDTO>(json);
                if (string.IsNullOrWhiteSpace(Completion(parsed)))
                    throw new InvalidOperationException("OpenRouter returned no answer.");
                if (Cache.Count < 64) Cache[body] = json;
                LastServiceMessage = $"OpenRouter free model ready ({SessionRequests}/{SessionRequestLimit} requests)";
                ModelServiceHealth.Report(ModelRequestOutcome.Success);
                return (parsed, timer.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                RetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                // Do not log request bodies, response bodies, credentials or room images.
                LastServiceMessage = exception is OperationCanceledException
                    ? "OpenRouter timed out; waiting before retry."
                    : "OpenRouter unavailable. Check the key, free model and connection.";
                Debug.LogWarning(LastServiceMessage);
                ModelServiceHealth.Report(exception is OperationCanceledException ? ModelRequestOutcome.Timeout : ModelRequestOutcome.ServerError);
                return (null, timer.ElapsedMilliseconds);
            }
            finally { Gate.Release(); }
        }
    }
}
