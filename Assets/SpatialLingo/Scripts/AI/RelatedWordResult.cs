// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public struct RelatedWordResult
    {
        public string ContextID;
        public AssistantAI.SupportedLanguage TargetLanguage { get; }
        public string Word { get; }
        public string[] Nouns { get; }
        public string[] Adjectives { get; }
        public string[] Verbs { get; }

        /// <summary>
        /// Metrics for the model request behind this result: tokens, round trip, model and
        /// finish reason. Null when the request failed and there was no response to report on.
        /// </summary>
        public MetaModelRestApi.ModelUsage Stats;

        public RelatedWordResult(AssistantAI.SupportedLanguage targetLanguage, string word, string[] nouns, string[] adjectives, string[] verbs, string contextID)
        {
            TargetLanguage = targetLanguage;
            Word = word;
            Nouns = nouns;
            Adjectives = adjectives;
            Verbs = verbs;
            ContextID = contextID;
            Stats = null;
        }
    }
}