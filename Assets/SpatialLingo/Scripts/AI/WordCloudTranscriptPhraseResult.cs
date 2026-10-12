// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public class WordCloudTranscriptPhraseResult
    {
        public string ContextID;

        /// <summary>
        /// Metrics for the model request behind this result: tokens, round trip, model and
        /// finish reason. Null when the request failed and there was no response to report on.
        /// </summary>
        public MetaModelRestApi.ModelUsage Stats;

        /// <summary>
        /// True when the model answered. False when the request failed, in which case IsPassing
        /// carries no meaning and the attempt must not count against the player.
        /// </summary>
        public bool HasVerdict;

        public readonly AssistantAI.SupportedLanguage TargetLanguage;
        public readonly string Transcript;
        public readonly string[] Nouns;
        public readonly string[] Adjectives;
        public readonly string[] Verbs;
        public readonly bool IsPassing;
        public readonly string FailReason;
        public WordCloudTranscriptPhraseResult(AssistantAI.SupportedLanguage targetLanguage, string transcript, string[] nouns, string[] adjectives, string[] verbs, bool isPassing, string failReason, string contextID)
        {
            TargetLanguage = targetLanguage;
            Transcript = transcript;
            Nouns = nouns;
            Adjectives = adjectives;
            Verbs = verbs;
            IsPassing = isPassing;
            FailReason = failReason;
            ContextID = contextID;
        }
    }
}