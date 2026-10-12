// Copyright (c) Meta Platforms, Inc. and affiliates.
using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;
using SpatialLingo.Debugging;
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

        public string MetaModelApiKey;

        [Tooltip("Model id to request. Leave empty to use the package default. Which models a key can reach varies by key - query GET /v1/models to list them.")]
        public string MetaModelId;

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
        public static void SetUpMetaModelApiKey()
        {
            if (!string.IsNullOrEmpty(Instance.MetaModelId))
            {
                MetaModelRestApi.DefaultModel = Instance.MetaModelId;
            }

            if (!string.IsNullOrEmpty(Instance.MetaModelApiKey) && MetaModelRestApi.GetApiKeyAsync == null)
            {
#pragma warning disable CS1998
                MetaModelRestApi.GetApiKeyAsync = async () =>
                {
                    Debug.Log($"MetaModelRestApi set to use SpatialLingoSettings.MetaModelApiKey (model: {MetaModelRestApi.DefaultModel})");
                    return (Instance.MetaModelApiKey, null);
                };
#pragma warning restore CS1998
            }
        }
    }
}
