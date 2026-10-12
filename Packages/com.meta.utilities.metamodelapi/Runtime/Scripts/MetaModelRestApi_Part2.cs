// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

// ReSharper disable InconsistentNaming
#pragma warning disable IDE1006

namespace Meta.Utilities.MetaModelAPI
{
    public partial class MetaModelRestApi
    {
        #region "Internal Data Transfer Objects"

        // Requests come in two families because JsonUtility cannot express a field that is
        // either a string or an array. Text-only chats send "content" as a plain string;
        // requests carrying images send "content" as an array of content parts.

        [DataContract, Serializable]
        private class ChatTextRequestDTO
        {
            [DataMember(Name = "model")] public string model;
            [DataMember(Name = "messages")] public ChatTextRequestMessageDTO[] messages;
            [DataMember(Name = "max_completion_tokens")] public int max_completion_tokens;
            [DataMember(Name = "reasoning_effort")] public string reasoning_effort;

            public ChatTextRequestDTO(string model, ChatTextRequestMessageDTO[] messages, int max_completion_tokens, string reasoning_effort)
            {
                this.model = model;
                this.messages = messages;
                this.max_completion_tokens = max_completion_tokens;
                this.reasoning_effort = reasoning_effort;
            }
        }

        [DataContract, Serializable]
        private class ChatTextRequestMessageDTO
        {
            [DataMember(Name = "role")] public string role;
            [DataMember(Name = "content")] public string content;

            public ChatTextRequestMessageDTO(string role, string content)
            {
                this.role = role;
                this.content = content;
            }

            public static ChatTextRequestMessageDTO[] FromChat(ModelChat chat)
            {
                var list = new List<ChatTextRequestMessageDTO>();
                var messageCount = chat.MessageCount;
                for (var i = 0; i < messageCount; ++i)
                {
                    var message = chat.GetMessage(i);
                    var requestMessage = new ChatTextRequestMessageDTO(message.Role, message.Text);
                    list.Add(requestMessage);
                }

                return list.ToArray();
            }
        }

        [DataContract, Serializable]
        private class ChatImageRequestDTO
        {
            [DataMember(Name = "model")]
            public string model;
            [DataMember(Name = "messages")]
            public ChatImageRequestMessageDTO[] messages;
            [DataMember(Name = "max_completion_tokens")]
            public int max_completion_tokens;
            [DataMember(Name = "reasoning_effort")]
            public string reasoning_effort;

            public ChatImageRequestDTO(string model, ChatImageRequestMessageDTO[] messages, int max_completion_tokens, string reasoning_effort)
            {
                this.model = model;
                this.messages = messages;
                this.max_completion_tokens = max_completion_tokens;
                this.reasoning_effort = reasoning_effort;
            }
        }

        [DataContract, Serializable]
        private class ChatImageRequestMessageDTO
        {
            [DataMember(Name = "role")]
            public string role;
            [DataMember(Name = "content")]
            public ChatImageRequestContentDTO[] content;

            public ChatImageRequestMessageDTO(string role, ChatImageRequestContentDTO[] content)
            {
                this.role = role;
                this.content = content;
            }
        }

        // JsonUtility writes an empty stub rather than null for unset nested objects, so a text
        // part serializes with an empty "image_url" and vice versa. The service ignores both.
        [DataContract, Serializable]
        private class ChatImageRequestContentDTO
        {
            [DataMember(Name = "type")]
            public string type;
            [DataMember(Name = "text")]
            public string text;
            [DataMember(Name = "image_url")]
            public ChatImageRequestContentImageUrlDTO image_url;

            public ChatImageRequestContentDTO(string type, string text, ChatImageRequestContentImageUrlDTO image_url)
            {
                this.type = type;
                this.text = text;
                this.image_url = image_url;
            }
        }

        [DataContract, Serializable]
        private class ChatImageRequestContentImageUrlDTO
        {
            [DataMember(Name = "url")] public string url;

            public ChatImageRequestContentImageUrlDTO(string url) => this.url = url;
        }

        // Responses are OpenAI-shaped: the completion text lives at choices[0].message.content.
        // The "object" key is intentionally unmapped - it is a C# keyword and JsonUtility
        // silently ignores JSON keys with no matching field.

        [DataContract, Serializable]
        private class ChatResponseDTO
        {
            [DataMember(Name = "id")] public string id;
            [DataMember(Name = "created")] public long created;
            [DataMember(Name = "model")] public string model;
            [DataMember(Name = "choices")] public ChatResponseChoiceDTO[] choices;
            [DataMember(Name = "usage")] public ChatResponseUsageDTO usage;
        }

        [DataContract, Serializable]
        private class ChatResponseChoiceDTO
        {
            [DataMember(Name = "index")] public int index;
            [DataMember(Name = "finish_reason")] public string finish_reason;
            [DataMember(Name = "message")] public ChatResponseMessageDTO message;
        }

        [DataContract, Serializable]
        private class ChatResponseMessageDTO
        {
            [DataMember(Name = "role")] public string role;
            // Null when the model spent its whole budget reasoning (finish_reason == "length").
            [DataMember(Name = "content")] public string content;
        }

        [DataContract, Serializable]
        private class ChatResponseUsageDTO
        {
            [DataMember(Name = "prompt_tokens")] public int prompt_tokens;
            [DataMember(Name = "completion_tokens")] public int completion_tokens;
            [DataMember(Name = "total_tokens")] public int total_tokens;
        }

        #endregion
    }
}

#pragma warning restore IDE1006
