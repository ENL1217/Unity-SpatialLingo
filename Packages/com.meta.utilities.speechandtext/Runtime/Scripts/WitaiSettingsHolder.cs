// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections.Generic;
using Meta.WitAi.Data.Configuration;
using Meta.WitAi.TTS.Integrations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SpatialLingo.SpeechAndText
{
    /// <summary>
    /// Provides access to multiple wit.ai configurations by language.
    /// </summary>
    public class WitaiSettingsHolder : ScriptableObject
    {
        public enum Language
        {
            English,
            Spanish
        }

        [SerializeField] private List<LanguageSettings> m_languageSettings;
        private Dictionary<Language, WitConfiguration> m_langSettingsDictionary;
        private static string s_englishToken;
        private static string s_spanishToken;
        private static int s_tokenVersion;
        private int m_tokenVersion = -1;

        public static void ConfigureRuntimeTokens(string english, string spanish)
        {
            s_englishToken = english;
            s_spanishToken = spanish;
            s_tokenVersion++;
        }

        [Serializable]
        private class ClientToken { public string _clientAccessToken; }

        private static WitConfiguration WithToken(WitConfiguration original, string token)
        {
            if (original == null || string.IsNullOrWhiteSpace(token)) return original;
            var configuration = Instantiate(original);
            configuration.hideFlags = HideFlags.HideAndDontSave;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(new ClientToken { _clientAccessToken = token }), configuration);
            return configuration;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterTtsConfiguration()
        {
            SceneManager.sceneLoaded -= ConfigureTts;
            SceneManager.sceneLoaded += ConfigureTts;
        }

        private static void ConfigureTts(Scene scene, LoadSceneMode mode)
        {
            foreach (var service in FindObjectsByType<TTSWit>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (service.gameObject.scene != scene) continue;
                // Upstream TTS is always configured with its English app, including Spanish text.
                // Spanish Wit apps are used for STT; Wit currently rejects synthesize on es apps.
                service.Configuration = WithToken(service.Configuration, s_englishToken);
            }
        }

        /// <summary>
        /// Gets wit.at settings for a particular language.
        /// </summary>
        /// <param name="language">Language to fetch settings for.</param>
        /// <returns>Matching WitConfiguration.</returns>
        public WitConfiguration GetSettingsForLanguage(Language language)
        {
            if (m_langSettingsDictionary == null || m_tokenVersion != s_tokenVersion)
            {
                m_langSettingsDictionary = SerializeDictionary();
                m_tokenVersion = s_tokenVersion;
            }
            return m_langSettingsDictionary[language];
        }

        private Dictionary<Language, WitConfiguration> SerializeDictionary()
        {
            Dictionary<Language, WitConfiguration> settingsDictionary = new();
            m_languageSettings.ForEach(settings =>
            {
                var token = settings.Language == Language.English ? s_englishToken : s_spanishToken;
                // Runtime clone: never save a personal token back into the source asset.
                var configuration = WithToken(settings.WitConfiguration, token);
                settingsDictionary.Add(settings.Language, configuration);
            });
            return settingsDictionary;
        }

        [Serializable]
        private struct LanguageSettings
        {
            public WitConfiguration WitConfiguration;
            public Language Language;
        }
    }
}
