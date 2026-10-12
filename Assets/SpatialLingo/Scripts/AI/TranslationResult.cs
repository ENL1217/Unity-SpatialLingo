// Copyright (c) Meta Platforms, Inc. and affiliates.

using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public struct TranslationResult
    {
        public string ContextID;
        public AssistantAI.SupportedLanguage LanguageFrom { get; }
        public AssistantAI.SupportedLanguage LanguageTo { get; }
        public string StatementFrom { get; }
        public string StatementTo { get; }

        /// <summary>
        /// Metrics for the model request behind this result: tokens, round trip, model and
        /// finish reason. Null when the request failed and there was no response to report on.
        /// </summary>
        public MetaModelRestApi.ModelUsage Stats;

        public TranslationResult(AssistantAI.SupportedLanguage languageFrom, AssistantAI.SupportedLanguage languageTo, string statementFrom, string statementTo, string contextID)
        {
            LanguageFrom = languageFrom;
            LanguageTo = languageTo;
            StatementFrom = statementFrom;
            StatementTo = statementTo;
            ContextID = contextID;
            Stats = null;
        }
    }
}