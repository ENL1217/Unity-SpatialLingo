using Meta.WitAi.Data.Configuration;
using NUnit.Framework;
using SpatialLingo.SpeechAndText;
using UnityEditor;
using UnityEngine;

public class WitRuntimeConfigurationTests
{
    [Test]
    public void PrivateClientTokensOnlyChangeRuntimeClones()
    {
        var holder = AssetDatabase.LoadAssetAtPath<WitaiSettingsHolder>("Assets/SpatialLingo/Data/Witai/WitaiSettingsHolder.asset");
        var original = AssetDatabase.LoadAssetAtPath<WitConfiguration>("Assets/SpatialLingo/Data/Witai/WitaiSettings_EN.asset");
        var originalToken = original.GetClientAccessToken();
        WitConfiguration english = null;
        WitConfiguration spanish = null;
        try
        {
            WitaiSettingsHolder.ConfigureRuntimeTokens("TEST_EN_NOT_A_KEY", "TEST_ES_NOT_A_KEY");
            english = holder.GetSettingsForLanguage(WitaiSettingsHolder.Language.English);
            spanish = holder.GetSettingsForLanguage(WitaiSettingsHolder.Language.Spanish);
            Assert.That(english, Is.Not.SameAs(original));
            Assert.That(english.GetClientAccessToken(), Is.EqualTo("TEST_EN_NOT_A_KEY"));
            Assert.That(spanish.GetClientAccessToken(), Is.EqualTo("TEST_ES_NOT_A_KEY"));
            Assert.That(original.GetClientAccessToken(), Is.EqualTo(originalToken));
            Assert.That(AssetDatabase.GetAssetPath(english), Is.Empty);
        }
        finally
        {
            WitaiSettingsHolder.ConfigureRuntimeTokens(null, null);
            if (english != null) Object.DestroyImmediate(english);
            if (spanish != null) Object.DestroyImmediate(spanish);
        }
    }
}
