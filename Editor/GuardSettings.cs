using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AegisGuard.Editor
{
    [Serializable]
    public sealed class GuardSettings
    {
        public bool enabled = true;
        public bool nativeMetadataProtection = true;
        public string[] assemblies = { "Assembly-CSharp" };
        public string[] embeddedResources = Array.Empty<string>(); // AssemblyName/Exact.Resource.Name
        public BundleInput[] bundles = Array.Empty<BundleInput>();
        public DataInput[] dataFiles = Array.Empty<DataInput>();
        public string windowsOutput = "Builds/AegisWindows/Game.exe";
        public string androidOutput = "Builds/AegisAndroid/Game.apk";
        public const string PathName = "ProjectSettings/AegisGuard.json";

        public static GuardSettings Load()
        {
            if (!File.Exists(PathName)) throw new InvalidOperationException("Create ProjectSettings/AegisGuard.json via Tools > AegisGuard > Create Configuration.");
            string json = File.ReadAllText(PathName);
            var settings = JsonUtility.FromJson<GuardSettings>(json) ?? throw new InvalidDataException("Invalid AegisGuard configuration.");
            if (!json.Contains("\"nativeMetadataProtection\"")) settings.nativeMetadataProtection = true;
            return settings;
        }

        [MenuItem("Tools/AegisGuard/Create Configuration")]
        public static void Create()
        {
            if (File.Exists(PathName)) { Debug.Log("AegisGuard configuration already exists: " + PathName); return; }
            File.WriteAllText(PathName, JsonUtility.ToJson(new GuardSettings(), true));
            Directory.CreateDirectory("GuardSource");
            Debug.Log("Created " + PathName + ". Add dataFiles, bundles and assembly names, then use the AegisGuard build menu.");
        }
    }

    [Serializable]
    public sealed class BundleInput
    {
        public string id;
        public string[] assets = Array.Empty<string>();
        public string[] addresses = Array.Empty<string>();
    }

    [Serializable]
    public sealed class DataInput
    {
        public string id;
        public string source; // Relative to project root, outside Assets/Packages.
    }
}
