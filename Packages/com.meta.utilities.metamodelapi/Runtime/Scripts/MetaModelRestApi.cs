// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Assertions;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Meta.Utilities.MetaModelAPI
{
    public partial class MetaModelRestApi
    {
        private HttpClient m_client;

        // Sample models to try. Which ones a key can reach varies by key: query GET /v1/models
        // to list them, and set Model (or DefaultModel) if the default is not available.
        public const string MODEL_NAME_MUSE_SPARK_1_3 = "muse-spark-1.3";
        public const string MODEL_NAME_MUSE_SPARK_1_2 = "muse-spark-1.2";
        public const string MODEL_NAME_MUSE_SPARK_1_1 = "muse-spark-1.1";

        /// <summary>
        /// Model used by instances created from now on. Set this once at startup to change the
        /// model for the whole app without touching each call site.
        /// </summary>
        public static string DefaultModel { get; set; } = MODEL_NAME_MUSE_SPARK_1_3;

        /// <summary>
        /// Model used by this instance. Defaults to <see cref="DefaultModel"/> at construction.
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// How much the model may reason before answering. Reasoning tokens are charged against
        /// the completion budget, so raising this may require raising maxTokens as well.
        /// Defaults to the lowest effort: reasoning is most of each request's time, and the app's
        /// requests are short, structured answers that do not need more.
        /// </summary>
        public ReasoningEffort Effort { get; set; } = ReasoningEffort.Minimal;

        // Reasoning tokens count toward the completion budget, so this is far more generous than
        // the response text alone needs. Too small a budget yields an empty completion.
        private const int DEFAULT_MAX_COMPLETION_TOKENS = 4096;

        public static Func<Awaitable<(string apiKey, string endpointOverride)>> GetApiKeyAsync = null;
        private ValueTask m_makeClientTask;

        // HTTP client name + version (UserAgent)
        private static string ClientName => "SpatialLingo"
#if UNITY_EDITOR
            + "-editor";
#elif DEBUG || DEVELOPMENT_BUILD
            + "-dev";
#else
            ;
#endif
        private static string ClientVersion
            => Application.version;



        public MetaModelRestApi()
        {
            Model = DefaultModel;
            m_makeClientTask = MakeClientAsync();

            async ValueTask MakeClientAsync()
            {
                Debug.Assert(GetApiKeyAsync != null, $"{nameof(GetApiKeyAsync)} must be set to use MetaModelRestApi.");

                var (apiKey, endpointOverride) = await GetApiKeyAsync();
                m_client = MakeClientWithKey(apiKey, endpointOverride);
            }
        }

        private static HttpClient MakeClientWithKey(string apiKey, string baseEndpointOverride = null)
        {
            const string PUBLIC_META_MODEL_API = "https://api.meta.ai/v1/";

#if HAS_META_UTILITIES
            if (AndroidHelpers.GetStringIntentExtra("MetaModelApiEndpoint") is { } adbOverride)
            {
                Debug.Log($"ADB String MetaModelApiEndpoint set to: {adbOverride}");
                baseEndpointOverride = adbOverride;
            }
#endif

            Assert.IsNotNull(apiKey, "apiKey");
            Assert.IsTrue(apiKey.Length > 2, "apiKey.Length > 2");

            var client = new HttpClient
            {
                BaseAddress = new Uri(baseEndpointOverride ?? PUBLIC_META_MODEL_API),
            };
            Debug.Log($"Meta Model API Endpoint = {client.BaseAddress}");
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ClientName, ClientVersion));
            // Generous compared to a plain completion: the model reasons before it answers.
            client.Timeout = TimeSpan.FromSeconds(60.0f);
            return client;
        }

        private async Awaitable<HttpClient> GetHttpClient()
        {
            await m_makeClientTask;
            return m_client;
        }

        private string EffortValue => Effort switch
        {
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            _ => "minimal",
        };


        /// <summary>
        /// Chat endpoint. Begin a conversation with AI.
        /// </summary>
        /// <param name="prompt">System prompt for context or behavior</param>
        /// <returns>Chat Response</returns>
        public ModelChat StartNewChat(string prompt)
        {
            var chat = new ModelChat();
            var system = new ModelChatMessage(ModelChatMessage.ROLE_SYSTEM, prompt);
            _ = chat.AddMessage(system);
            return chat;
        }

        /// <summary>
        /// Chat endpoint. Continue conversation with AI.
        /// </summary>
        /// <param name="history">Previous conversation history</param>
        /// <param name="text">Message sent from or in service of user</param>
        /// <returns>Chat Response</returns>
        public async Awaitable<ModelChatResponse> ContinueChat(ModelChat history, string text)
        {
            var user = new ModelChatMessage(ModelChatMessage.ROLE_USER, text);
            _ = history.AddMessage(user);

            var messages = ChatTextRequestMessageDTO.FromChat(history);
            var (response, roundTripMilliseconds) = await ChatInternal(messages);
            if (response == null)
            {
                Debug.LogError($"Received a null result for {nameof(ContinueChat)}");
                return null;
            }

            var completion = GetCompletion(response, nameof(ContinueChat));
            if (completion == null)
            {
                return null;
            }

            var assistant = new ModelChatMessage(completion.Value.role, completion.Value.text);
            _ = history.AddMessage(assistant);

            var chatResponse = new ModelChatResponse(history, assistant, GetUsage(response, roundTripMilliseconds));
            return chatResponse;
        }

        public bool AddChatHistoryAsUser(ModelChat chat, string text)
        {
            return AddChatHistory(chat, ModelChatMessage.ROLE_USER, text);
        }

        public bool AddChatHistoryAsAI(ModelChat chat, string text)
        {
            return AddChatHistory(chat, ModelChatMessage.ROLE_ASSISTANT, text);
        }

        private bool AddChatHistory(ModelChat chat, string role, string text)
        {
            var oldCount = chat.MessageCount;
            var message = new ModelChatMessage(role, text);
            var newCount = chat.AddMessage(message);
            return newCount > oldCount;
        }

        /// <summary>
        /// Pull the completion text out of a response, or log why there isn't one.
        /// </summary>
        private static (string role, string text)? GetCompletion(ChatResponseDTO response, string caller)
        {
            if (response.choices == null || response.choices.Length == 0)
            {
                Debug.LogWarning($"MetaModelRestApi - {caller} - response contained no choices");
                return null;
            }

            var choice = response.choices[0];
            var message = choice.message;
            if (message == null || string.IsNullOrEmpty(message.content))
            {
                // A "length" finish with no content means the reasoning consumed the whole
                // completion budget - raise max_completion_tokens or lower the reasoning effort.
                Debug.LogWarning(
                    $"MetaModelRestApi - {caller} - empty completion (finish_reason: {choice.finish_reason}). " +
                    $"If this is \"length\", the reasoning budget was exhausted before any text was produced.");
                return null;
            }

            return (message.role, message.content);
        }

        /// <summary>
        /// Pull the per-request metrics out of a response. Fields the service omits read as zero
        /// or null rather than failing the call - metrics are reporting, not correctness.
        /// </summary>
        private static ModelUsage GetUsage(ChatResponseDTO response, long roundTripMilliseconds)
        {
            var usage = response.usage;
            var finishReason = response.choices is { Length: > 0 } ? response.choices[0].finish_reason : null;
            return new ModelUsage(
                usage?.prompt_tokens ?? 0,
                usage?.completion_tokens ?? 0,
                usage?.total_tokens ?? 0,
                response.model,
                finishReason,
                roundTripMilliseconds);
        }


        /// <summary>
        /// Work out what a failed request says about the service, so repeated failures can be
        /// distinguished from a single slow one and reported to the player.
        /// </summary>
        private static ModelRequestOutcome ClassifyFailure(Exception e)
        {
            // HttpClient surfaces its own timeout as a cancellation, which is why a plain
            // "task was canceled" is the shape a request that got no reply in time arrives in.
            if (e is TaskCanceledException or OperationCanceledException)
            {
                return ModelRequestOutcome.Timeout;
            }

            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                return ModelRequestOutcome.NoNetwork;
            }

            return e is HttpRequestException ? ModelRequestOutcome.NoNetwork : ModelRequestOutcome.ServerError;
        }

        private async Awaitable<(ChatResponseDTO response, long roundTripMilliseconds)> ChatInternal(ChatTextRequestMessageDTO[] messages, int maxTokens = DEFAULT_MAX_COMPLETION_TOKENS)
        {
            m_client ??= await GetHttpClient();

            var endpoint = "chat/completions";
            var request = new ChatTextRequestDTO(Model, messages, maxTokens, EffortValue);
            var requestJSON = JsonUtility.ToJson(request);
            var content = new StringContent(requestJSON, Encoding.UTF8, "application/json");
            // Started after the client is ready so this measures the request, not the key fetch.
            var roundTrip = Stopwatch.StartNew();
            try
            {
                var response = await m_client.PostAsync(endpoint, content);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Debug.LogError(
                        $"Expected response code: {HttpStatusCode.OK}, got: {response.StatusCode} [{(int)response.StatusCode}]\n" +
                        $"Request Data:\n{requestJSON}");
                    Debug.LogError($"Server Reason:\n{response.ReasonPhrase}\nServer Content:\n{response.Content}");
                    ModelServiceHealth.Report(ModelRequestOutcome.ServerError);
                    return (null, roundTrip.ElapsedMilliseconds);
                }

                if (response.Content == null)
                {
                    Debug.LogError($"Expected response content, found null");
                    ModelServiceHealth.Report(ModelRequestOutcome.ServerError);
                    return (null, roundTrip.ElapsedMilliseconds);
                }

                var value = await response.Content.ReadAsStringAsync();
                var responseObject = JsonUtility.FromJson<ChatResponseDTO>(value);
                ModelServiceHealth.Report(ModelRequestOutcome.Success);
                return (responseObject, roundTrip.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"MetaModelRestApi - ChatInternal - client.PostAsync failed: {e.Message}");
                ModelServiceHealth.Report(ClassifyFailure(e));
            }

            return (null, roundTrip.ElapsedMilliseconds);
        }

        /// <summary>
        /// Image Understanding endpoint. Get information about passed images
        /// </summary>
        /// <param name="text">Message to pass along with image(s)</param>
        /// <param name="images">Images are base64 w/ prefix or web URLs</param>
        /// <returns>Image Undestanding Response</returns>
        public async Awaitable<ModelImageResponse> ImageUnderstanding(string systemText, string userText, string[] images)
        {
            var (response, roundTripMilliseconds) = await ImageUnderstandingInternal(systemText, userText, images);
            if (response == null)
            {
                Debug.LogError($"Received a null result for {nameof(ImageUnderstanding)}");
                return null;
            }

            var completion = GetCompletion(response, nameof(ImageUnderstanding));
            if (completion == null)
            {
                return null;
            }

            var imageResponse = new ModelImageResponse(completion.Value.text, GetUsage(response, roundTripMilliseconds));
            return imageResponse;
        }

        private async Awaitable<(ChatResponseDTO response, long roundTripMilliseconds)> ImageUnderstandingInternal(string systemText, string userText, string[] images)
        {
            m_client ??= await GetHttpClient();

            var endpoint = "chat/completions";
            ChatImageRequestMessageDTO systemMessage = null;
            if (systemText != null)
            {
                var systemContentList = new List<ChatImageRequestContentDTO>();
                var systemContentText = new ChatImageRequestContentDTO("text", systemText, null);
                systemContentList.Add(systemContentText);
                var systemContents = systemContentList.ToArray();
                systemMessage = new ChatImageRequestMessageDTO(ModelChatMessage.ROLE_SYSTEM, systemContents);
            }

            var contentList = new List<ChatImageRequestContentDTO>();
            var contentText = new ChatImageRequestContentDTO("text", userText, null);
            contentList.Add(contentText);

            // Images are only processed on user-role messages.
            foreach (var image in images)
            {
                var contentImageURL = new ChatImageRequestContentImageUrlDTO(image);
                var contentImage = new ChatImageRequestContentDTO("image_url", null, contentImageURL);
                contentList.Add(contentImage);
            }

            var contents = contentList.ToArray();
            var message = new ChatImageRequestMessageDTO(ModelChatMessage.ROLE_USER, contents);
            var messages = systemMessage != null
                ? (new ChatImageRequestMessageDTO[] { systemMessage, message })
                : (new ChatImageRequestMessageDTO[] { message });
            var request = new ChatImageRequestDTO(Model, messages, DEFAULT_MAX_COMPLETION_TOKENS, EffortValue);
            var requestJSON = JsonUtility.ToJson(request);

            var content = new StringContent(requestJSON, Encoding.UTF8, "application/json");
            // Started after the request body is built so image encoding is not counted here.
            var roundTrip = Stopwatch.StartNew();
            try
            {
                var response = await m_client.PostAsync(endpoint, content);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Debug.LogError(
                        $"Expected response code: {HttpStatusCode.OK}, got: {response.StatusCode} [{(int)response.StatusCode}]\n" +
                        $"Request Data:\n{requestJSON}");
                    Debug.LogError($"Server Reason:\n{response.ReasonPhrase}\nServer Content:\n{response.Content}");
                    ModelServiceHealth.Report(ModelRequestOutcome.ServerError);
                    return (null, roundTrip.ElapsedMilliseconds);
                }

                if (response.Content == null)
                {
                    Debug.LogError($"Expected response content, found null");
                    ModelServiceHealth.Report(ModelRequestOutcome.ServerError);
                    return (null, roundTrip.ElapsedMilliseconds);
                }

                var value = await response.Content.ReadAsStringAsync();
                var responseObject = JsonUtility.FromJson<ChatResponseDTO>(value);
                ModelServiceHealth.Report(ModelRequestOutcome.Success);
                return (responseObject, roundTrip.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"MetaModelRestApi - ImageUnderstandingInternal - client.PostAsync failed: {e.Message}");
                ModelServiceHealth.Report(ClassifyFailure(e));
            }

            return (null, roundTrip.ElapsedMilliseconds);
        }

    }
}
