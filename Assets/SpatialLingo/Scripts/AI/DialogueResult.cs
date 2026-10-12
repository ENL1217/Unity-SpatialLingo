// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public struct DialogueResult
    {
        public string ContextID;
        public AssistantAI.SupportedLanguage TargetLanguage { get; }
        public string Dialogue { get; }

        /// <summary>
        /// Metrics for the model request behind this result: tokens, round trip, model and
        /// finish reason. Null when the request failed and there was no response to report on.
        /// </summary>
        public MetaModelRestApi.ModelUsage Stats;

        public DialogueResult(AssistantAI.SupportedLanguage targetLanguage, string dialogue, string contextID)
        {
            TargetLanguage = targetLanguage;
            Dialogue = dialogue;
            ContextID = contextID;
            Stats = null;
        }
    }
}