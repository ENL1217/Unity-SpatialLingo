// Copyright (c) Meta Platforms, Inc. and affiliates.
using Meta.Utilities.MetaModelAPI;
using Meta.XR.Samples;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace SpatialLingo.Settings
{
    [MetaCodeSample("SpatialLingo")]
    [DefaultExecutionOrder(9999)]
    public class SpatialLingoSettings : ScriptableSettings<SpatialLingoSettings>
    {
        public string MetaModelApiKey;

        [Tooltip("Model id to request. Leave empty to use the package default. Which models a key can reach varies by key - query GET /v1/models to list them.")]
        public string MetaModelId;

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
