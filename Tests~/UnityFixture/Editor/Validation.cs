using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AegisGuard;
using AegisGuard.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AegisValidation
{
    public static void Run()
    {
        AegisWeaverChecks.Run();
        Directory.CreateDirectory("Assets/Protected"); Directory.CreateDirectory("GuardSource");
        File.WriteAllText("Assets/Protected/message.txt", "fixture bundle 5329");
        File.WriteAllText("GuardSource/balance.json", "{\"balance\":421}");
        File.WriteAllText("GuardSource/embedded.txt", "embedded fixture 1249");
        AssetDatabase.Refresh();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Camera").AddComponent<Camera>();
        new GameObject("Light").AddComponent<Light>().type = LightType.Directional;
        var probe = new GameObject("ProtectionProbe").AddComponent<PlayerProbe>();
        probe.expectedPhraseHash = Hash("AEGIS_PRIVATE_한글_7348");
        probe.expectedEmbeddedHash = Hash("embedded fixture 1249");
        probe.expectedDataHash = Hash("{\"balance\":421}");
        probe.expectedBundleHash = Hash("fixture bundle 5329");
        var visualBundle = AssetFixtureChecks.Create(probe);
        EditorSceneManager.SaveScene(scene, "Assets/Probe.unity");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Probe.unity", true) };
        var settings = new GuardSettings
        {
            embeddedResources = new[] { "Assembly-CSharp/Aegis.Test.Data" },
            bundles = new[] { new BundleInput { id = "texts", assets = new[] { "Assets/Protected/message.txt" }, addresses = new[] { "message" } }, visualBundle },
            dataFiles = new[] { new DataInput { id = "balance", source = "GuardSource/balance.json" } }
        };
        File.WriteAllText(GuardSettings.PathName, JsonUtility.ToJson(settings, true));
        AssetFixtureChecks.ValidateLeakRejection(settings, scene, probe);
        PlayerSettings.companyName = "AegisGuardValidation"; PlayerSettings.productName = "AegisGuardProbe";
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.yun.aegisguard.probe");
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.Standalone, ManagedStrippingLevel.Low);
        PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.Android, ManagedStrippingLevel.Low);
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android) GuardBuild.Android();
        else GuardBuild.Windows();
        Debug.Log("AEGIS_BUILD_PASS");
    }
    private static string Hash(string text)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }
}
