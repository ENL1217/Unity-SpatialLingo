// Copyright (c) Meta Platforms, Inc. and affiliates.
using System;
using System.IO;
using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;
using SpatialLingo.Debugging;
using SpatialLingo.SpeechAndText;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace SpatialLingo.Settings
{
    [MetaCodeSample("SpatialLingo")]
    [DefaultExecutionOrder(9999)]
    public class SpatialLingoSettings : ScriptableSettings<SpatialLingoSettings>
    {
        [Tooltip("Start with the testing debug features on: the room scan overlay, the lesson panel under the mic and the activity panel. Can also be toggled at runtime from the debug menu.")]
        public bool DebugModeEnabled;

        [Tooltip("Acquire several objects at once while the room is still being filled. The model takes tens of seconds to answer and the wait is almost all network, so overlapping requests shortens the time to a usable set of lessons. Falls back to one at a time once enough lessons exist.")]
        public bool ParallelAcquisitionEnabled = false;

        [Tooltip("How many objects may be acquired at once while below the lesson target. Each object costs two sequential requests, so 2 here means up to 4 requests in flight.")]
        [Range(1, 4)]
        public int ParallelAcquisitionCount = 1;

        public string OpenRouterModelId = OpenRouterRequest.DefaultModel;

        [Serializable]
        private class DeviceConfiguration
        {
            public string apiKey;
            public string model;
            public string witEnglishToken;
            public string witSpanishToken;
        }

        /// <summary>
        /// Seed the debug toggle from the build default. Runs after SpatialLingoDebug clears its
        /// static state on SubsystemRegistration, so this value wins.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void SetUpDebugMode()
        {
            SpatialLingoDebug.Enabled = Instance.DebugModeEnabled;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void SetUpOpenRouter()
        {
            var config = new DeviceConfiguration();
            var path = Path.Combine(Application.persistentDataPath, "AIEnglish.OpenRouter.json");
            try
            {
                if (File.Exists(path)) config = JsonUtility.FromJson<DeviceConfiguration>(File.ReadAllText(path)) ?? config;
            }
            catch (Exception)
            {
                Debug.LogWarning("Could not read AIEnglish.OpenRouter.json.");
            }
#if UNITY_EDITOR
            config.apiKey = Environment.GetEnvironmentVariable("AIENGLISH_OPENROUTER_API_KEY") ?? config.apiKey;
#endif
            var model = string.IsNullOrEmpty(config.model) ? Instance.OpenRouterModelId : config.model;
            try { OpenRouterRequest.ValidateModel(model); }
            catch (ArgumentException) { model = OpenRouterRequest.DefaultModel; }
            MetaModelRestApi.DefaultModel = model;
            WitaiSettingsHolder.ConfigureRuntimeTokens(config.witEnglishToken, config.witSpanishToken);
            Instance.ParallelAcquisitionEnabled = false;
            Instance.ParallelAcquisitionCount = 1;
#pragma warning disable CS1998
            MetaModelRestApi.GetApiKeyAsync = async () => (config.apiKey, null);
#pragma warning restore CS1998
            Debug.Log($"AIEnglish OpenRouter model: {model}; key configured: {!string.IsNullOrWhiteSpace(config.apiKey)}");
        }
    }
}
