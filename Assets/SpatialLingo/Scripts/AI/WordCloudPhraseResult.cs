// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public struct WordCloudPhraseResult
    {
        public string ContextID;
        public readonly AssistantAI.SupportedLanguage TargetLanguage;
        public readonly WordCloudPhraseData Phrases;

        /// <summary>
        /// Metrics for the model request behind this result: tokens, round trip, model and
        /// finish reason. Null when the request failed and there was no response to report on.
        /// </summary>
        public MetaModelRestApi.ModelUsage Stats;

        public WordCloudPhraseResult(AssistantAI.SupportedLanguage language, WordCloudPhraseData phrases, string contextID)
        {
            TargetLanguage = language;
            Phrases = phrases;
            ContextID = contextID;
            Stats = null;
        }
    }
}