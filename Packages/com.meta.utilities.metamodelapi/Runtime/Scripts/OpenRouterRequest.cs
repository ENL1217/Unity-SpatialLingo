using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Meta.Utilities.MetaModelAPI
{
    /// <summary>Serializes the OpenAI-compatible request without empty mixed-content DTO fields.</summary>
    public static class OpenRouterRequest
    {
        public const string BaseUrl = "https://openrouter.ai/api/v1/";
        public const string DefaultModel = "nvidia/nemotron-3.5-super-vl:free";

        public static void ValidateModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || !model.EndsWith(":free", StringComparison.Ordinal) ||
                model.IndexOf('/') <= 0 || model.Contains("..") || model.IndexOfAny(new[] { '\\', '?', '#', ' ', '\n', '\r' }) >= 0)
                throw new ArgumentException("Only explicit OpenRouter :free model IDs are allowed.", nameof(model));
        }

        public static void ValidateEndpoint(string endpoint)
        {
            if (!string.IsNullOrEmpty(endpoint) && endpoint != BaseUrl)
                throw new ArgumentException("Only the official OpenRouter endpoint is allowed.", nameof(endpoint));
        }

        [Serializable] private class StringValue { public string value; }
        private static string Quote(string value)
        {
            var json = JsonUtility.ToJson(new StringValue { value = value ?? "" });
            var colon = json.IndexOf(':');
            return json.Substring(colon + 1, json.Length - colon - 2);
        }

        public static string Text(string model, MetaModelRestApi.ModelChat chat)
        {
            var messages = new List<string>();
            for (var i = 0; i < chat.MessageCount; i++)
            {
                var m = chat.GetMessage(i);
                messages.Add("{\"role\":" + Quote(m.Role) + ",\"content\":" + Quote(m.Text) + "}");
            }
            return Wrap(model, string.Join(",", messages));
        }

        public static string Image(string model, string system, string prompt, string[] images)
        {
            var messages = new StringBuilder();
            if (!string.IsNullOrEmpty(system))
                messages.Append("{\"role\":\"system\",\"content\":").Append(Quote(system)).Append("},");
            messages.Append("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":").Append(Quote(prompt)).Append('}');
            foreach (var image in images ?? Array.Empty<string>())
            {
                if (image == null || !image.StartsWith("data:image/", StringComparison.Ordinal))
                    throw new ArgumentException("Images must be inline data URLs.");
                messages.Append(",{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(Quote(image)).Append("}}");
            }
            messages.Append("]}");
            return Wrap(model, messages.ToString());
        }

        private static string Wrap(string model, string messages)
        {
            ValidateModel(model);
            return "{\"model\":" + Quote(model) + ",\"messages\":[" + messages +
                "],\"max_tokens\":2048,\"temperature\":0.2,\"stream\":false," +
                (model == DefaultModel ? "\"reasoning\":{\"enabled\":false}," : "") +
                "\"provider\":{\"allow_fallbacks\":false,\"max_price\":{\"prompt\":0,\"completion\":0,\"request\":0,\"image\":0}}}";
        }
    }
}
