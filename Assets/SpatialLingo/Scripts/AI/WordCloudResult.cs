// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public struct WordCloudResult
    {
        public string ContextID;
        public readonly AssistantAI.SupportedLanguage UserLanguage;
        public readonly AssistantAI.SupportedLanguage TargetLanguage;
        public readonly string Classification;
        public readonly WordCloudData Wordcloud;

        /// <summary>
        /// Metrics for the model request behind this result: tokens, round trip, model and
        /// finish reason. Null when the request failed and there was no response to report on.
        /// </summary>
        public MetaModelRestApi.ModelUsage Stats;

        public WordCloudResult(AssistantAI.SupportedLanguage userLanguage, AssistantAI.SupportedLanguage targetLanguage, string classification, WordCloudData wordcloud, string contextID)
        {
            UserLanguage = userLanguage;
            TargetLanguage = targetLanguage;
            Classification = classification;
            Wordcloud = wordcloud;
            ContextID = contextID;
            Stats = null;
        }
    }
}