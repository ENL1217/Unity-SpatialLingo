using System.IO;
using UnityEditor.Android;

/// <summary>Meta SDK 81 ships separate AARs with the same com.oculus.Integration manifest namespace.</summary>
public sealed class AIEnglishGradleCompatibility : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => int.MaxValue;
    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var properties = Path.Combine(Directory.GetParent(path).FullName, "gradle.properties");
        var content = File.ReadAllText(properties);
        if (!content.Contains("android.uniquePackageNames="))
            File.AppendAllText(properties,
                "\n# Meta SDK 81 AAR manifest namespaces; keep AGP class/resource conflict checks enabled.\nandroid.uniquePackageNames=false\n");
    }
}
