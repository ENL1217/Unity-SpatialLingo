using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Unity.InferenceEngine;

public static class AIEnglishBuild
{
    public static void Android()
    {
        var sdk = Environment.GetEnvironmentVariable("AIENGLISH_ANDROID_SDK");
        var ndk = Environment.GetEnvironmentVariable("AIENGLISH_ANDROID_NDK");
        var jdk = Environment.GetEnvironmentVariable("AIENGLISH_JAVA_HOME");
        if (string.IsNullOrEmpty(sdk) || string.IsNullOrEmpty(ndk) || string.IsNullOrEmpty(jdk))
            throw new BuildFailedException("Set AIENGLISH_ANDROID_SDK, AIENGLISH_ANDROID_NDK and AIENGLISH_JAVA_HOME.");
        UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = sdk;
        UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = ndk;
        UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = jdk;
        PlayerSettings.companyName = "ENL1217";
        PlayerSettings.productName = "AIEnglish";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.enl1217.aienglish");
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        EditorUserBuildSettings.buildAppBundle = false;
        var oculusSettings = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/Resources/OculusRuntimeSettings.asset");
        PlayerSettings.SetPreloadedAssets(PlayerSettings.GetPreloadedAssets().Where(a => a != null)
            .Append(oculusSettings).Distinct().ToArray());
        const string detector = "Assets/SpatialLingo/Data/InferenceEngine/ObjectClassifier/yolov9onnx_min_0.2score_0.5iou.sentis";
        AssetDatabase.ImportAsset(detector, ImportAssetOptions.ForceUpdate);
        var modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(detector);
        if (modelAsset == null) throw new BuildFailedException("YOLO model did not import.");
        var model = ModelLoader.Load(modelAsset);
        if (model == null || model.outputs.Count == 0) throw new BuildFailedException("YOLO model has no outputs.");
        var output = Environment.GetEnvironmentVariable("AIENGLISH_APK") ?? "Builds/AIEnglish-OpenRouter.apk";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Android build failed: {report.summary.totalErrors} errors.");
        Debug.Log($"AIEnglish APK: {Path.GetFullPath(output)} ({report.summary.totalSize} bytes)");
    }
}
