// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;

namespace Meta.Utilities.MetaModelAPI
{
    public partial class MetaModelRestApi
    {
        #region "Exposed Data Objects"

        /// <summary>
        /// How much the model is allowed to reason before answering. Muse Spark always reasons,
        /// and reasoning tokens count against the completion token budget, so a higher effort
        /// needs a correspondingly larger budget. "none" is rejected by the service, which makes
        /// Minimal the lowest effort available.
        /// </summary>
        public enum ReasoningEffort
        {
            Minimal,
            Low,
            Medium,
            High,
        }

        /// <summary>
        /// Per-request metrics the service reports alongside the completion.
        /// </summary>
        public class ModelUsage
        {
            /// <summary>Tokens consumed by the prompt: system context, history and the new message.</summary>
            public int PromptTokens { get; }

            /// <summary>Tokens produced by the model. Reasoning tokens are counted here too.</summary>
            public int CompletionTokens { get; }

            /// <summary>Total tokens charged for the request.</summary>
            public int TotalTokens { get; }

            /// <summary>Model that served the request, which need not be the one asked for.</summary>
            public string Model { get; }

            /// <summary>Why the model stopped, e.g. "stop" or "length".</summary>
            public string FinishReason { get; }

            /// <summary>
            /// Wall-clock time for the HTTP request, from POST to parsed response. Covers the
            /// network and the model's thinking time, but not any local work the caller did to
            /// build the request (encoding an image, for instance).
            /// </summary>
            public long RoundTripMilliseconds { get; }

            /// <summary>Completion tokens per second, or 0 when the request was too fast to measure.</summary>
            public double CompletionTokensPerSecond =>
                RoundTripMilliseconds > 0 ? CompletionTokens / (RoundTripMilliseconds / 1000.0) : 0.0;

            internal ModelUsage(int promptTokens, int completionTokens, int totalTokens, string model, string finishReason, long roundTripMilliseconds)
            {
                PromptTokens = promptTokens;
                CompletionTokens = completionTokens;
                TotalTokens = totalTokens;
                Model = model;
                FinishReason = finishReason;
                RoundTripMilliseconds = roundTripMilliseconds;
            }
        }

        public class ModelImageResponse
        {
            public string Message { get; }

            /// <summary>Metrics for the request that produced this response.</summary>
            public ModelUsage Usage { get; }

            internal ModelImageResponse(string message, ModelUsage usage)
            {
                Message = message;
                Usage = usage;
            }
        }


        public class ModelChat
        {
            private List<ModelChatMessage> m_messages;

            internal ModelChat() => m_messages = new List<ModelChatMessage>();

            public int MessageCount => m_messages.Count;

            /// <summary>
            /// Add a message to the conversation list
            /// </summary>
            /// <param name="message">Message to add</param>
            /// <returns>New count of total messages in the conversation</returns>
            public int AddMessage(ModelChatMessage message)
            {
                m_messages.Add(message);
                return m_messages.Count;
            }
            /// <summary>
            /// Get a specific message in the conversation
            /// </summary>
            /// <param name="index">Index of message requested</param>
            /// <returns>Message if found, null otherwise</returns>
            public ModelChatMessage GetMessage(int index)
            {
                if (index < 0 || index >= m_messages.Count)
                {
                    return null;
                }

                return m_messages[index];
            }
        }

        public class ModelChatMessage
        {
            public const string ROLE_SYSTEM = "system"; // system context
            public const string ROLE_USER = "user"; // content sent to the model
            public const string ROLE_ASSISTANT = "assistant"; // content sent from the model
            public const string ROLE_TOOL = "tool"; // tool
            public string Role { get; }
            public string Text { get; }

            internal ModelChatMessage(string role, string text)
            {
                Role = role;
                Text = text;
            }
        }

        public class ModelChatResponse
        {
            public ModelChat History { get; }
            public ModelChatMessage Message { get; }

            /// <summary>Metrics for the request that produced this response.</summary>
            public ModelUsage Usage { get; }

            internal ModelChatResponse(ModelChat history, ModelChatMessage message, ModelUsage usage)
            {
                History = history;
                Message = message;
                Usage = usage;
            }
        }

        #endregion
    }
}
