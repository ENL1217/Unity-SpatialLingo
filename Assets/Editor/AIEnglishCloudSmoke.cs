using System;
using System.IO;
using Meta.Utilities.MetaModelAPI;
using SpatialLingo.AI;
using UnityEditor;
using UnityEngine;

/// <summary>Opt-in cloud smoke test of the actual Unity adapter and lesson parser, using the sample cup.</summary>
public static class AIEnglishCloudSmoke
{
    [Serializable] private class Configuration { public string apiKey; public string model; }
    public static async void Run()
    {
        try
        {
            var config = JsonUtility.FromJson<Configuration>(File.ReadAllText(".local/AIEnglish.OpenRouter.json"));
            OpenRouterRequest.ValidateModel(config.model);
            MetaModelRestApi.DefaultModel = config.model;
#pragma warning disable CS1998
            MetaModelRestApi.GetApiKeyAsync = async () => (config.apiKey, null);
#pragma warning restore CS1998
            var api = new MetaModelRestApi();
            var assistant = new AssistantAI(api);
            var image = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(
                "Assets/SpatialLingo/Textures/Samples/InferenceEngine/cup.png"));
            var cloud = await assistant.GenerateWordCloudData(AssistantAI.SupportedLanguage.English,
                AssistantAI.SupportedLanguage.Spanish, "cup", image);
            var spanish = cloud.Wordcloud?.WordCloudForLanguage(AssistantAI.SupportedLanguage.Spanish);
            if (spanish == null || spanish.Adjectives?.Length != 3 || spanish.Verbs?.Length != 2)
                throw new Exception("Unity Wordcloud parser did not produce the expected Spanish lesson.");
            Debug.Log($"AIEnglish cloud smoke: wordcloud passed ({cloud.Stats?.RoundTripMilliseconds} ms).");
            var phrases = await assistant.GenerateSpeechExamplesForWordCloud(AssistantAI.SupportedLanguage.Spanish,
                spanish.Word, spanish.Verbs, spanish.Adjectives);
            if (phrases.Phrases == null) throw new Exception("Unity phrase parser returned no data.");
            Debug.Log("AIEnglish cloud smoke: phrases passed.");
            var verdict = await assistant.EvaluateTranscriptionForWordCloud(AssistantAI.SupportedLanguage.English,
                AssistantAI.SupportedLanguage.English, "I see a blue cup.", new[] { "cup" }, new[] { "blue" }, null);
            if (verdict == null || !verdict.HasVerdict || !verdict.IsPassing)
                throw new Exception("Unity verdict parser did not accept the valid sample sentence.");
            Debug.Log($"AIEnglish cloud smoke PASSED: vision, phrases, verdict; {MetaModelRestApi.SessionRequests} free requests.");
            EditorApplication.Exit(0);
        }
        catch (Exception)
        {
            Debug.LogError("AIEnglish cloud smoke FAILED. " + MetaModelRestApi.LastServiceMessage);
            EditorApplication.Exit(1);
        }
    }
}
