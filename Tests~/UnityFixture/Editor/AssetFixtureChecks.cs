using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AegisGuard.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// All original fixture data is generated in Editor code, never in the player.
// Only expected SHA256 digests are serialized into the player scene.
public static class AssetFixtureChecks
{
    private const string TexturePath = "Assets/Protected/AegisProtectedTexture_7F20.asset";
    private const string MeshPath = "Assets/Protected/AegisProtectedMesh_8C31.asset";
    private const string PrefabPath = "Assets/Protected/AegisProtectedPrefab_9D42.prefab";
    private const string PlainTexturePath = "Assets/PlainControls/AegisPlainTexture_2A16.asset";
    private const string PlainMeshPath = "Assets/PlainControls/AegisPlainMesh_3B27.asset";

    [Serializable]
    private sealed class Evidence
    {
        public string protectedTextureName, protectedMeshName, protectedPrefabName;
        public string plainTextureName, plainMeshName;
        public string protectedTextureRgbaSha256, protectedMeshGeometrySha256;
        public string plainTextureRgbaSha256, plainMeshGeometrySha256;
        public int protectedWidth = 37, protectedHeight = 23, protectedVertices = 63, protectedIndices = 288;
        public int plainWidth = 19, plainHeight = 13, plainVertices = 35, plainIndices = 144;
        public string pixelHashFormat = "bottom-left row order, contiguous RGBA8 bytes, no dimensions or header";
        public string meshHashFormat = "little endian int32 vertex count; float32 xyz per vertex; int32 index count; int32 per triangle index";
    }

    public static BundleInput Create(PlayerProbe probe)
    {
        Directory.CreateDirectory("Assets/Protected"); Directory.CreateDirectory("Assets/PlainControls");
        AssetDatabase.Refresh();
        var protectedTexture = CreateTexture(TexturePath, 37, 23, 0x7F20u);
        var plainTexture = CreateTexture(PlainTexturePath, 19, 13, 0x2A16u);
        var protectedMesh = CreateMesh(MeshPath, 9, 7, 19);
        var plainMesh = CreateMesh(PlainMeshPath, 7, 5, 5);
        var model = new GameObject("AegisProtectedPrefab_9D42");
        try
        {
            var child = new GameObject("AegisProtectedMeshNode"); child.transform.SetParent(model.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = protectedMesh;
            var saved = PrefabUtility.SaveAsPrefabAsset(model, PrefabPath, out bool success);
            if (!success || saved == null) throw new Exception("Could not create protected prefab fixture.");
        }
        finally { UnityEngine.Object.DestroyImmediate(model); }
        probe.plainControlTexture = plainTexture; probe.plainControlMesh = plainMesh;
        probe.expectedTextureHash = PlayerProbe.HashTexture(protectedTexture);
        probe.expectedMeshHash = PlayerProbe.HashMesh(protectedMesh);
        probe.expectedPlainTextureHash = PlayerProbe.HashTexture(plainTexture);
        probe.expectedPlainMeshHash = PlayerProbe.HashMesh(plainMesh);
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("GuardBuildReports");
        File.WriteAllText("GuardBuildReports/fixture-assets.json", JsonUtility.ToJson(new Evidence
        {
            protectedTextureName = protectedTexture.name, protectedMeshName = protectedMesh.name,
            protectedPrefabName = "AegisProtectedPrefab_9D42", plainTextureName = plainTexture.name, plainMeshName = plainMesh.name,
            protectedTextureRgbaSha256 = probe.expectedTextureHash, protectedMeshGeometrySha256 = probe.expectedMeshHash,
            plainTextureRgbaSha256 = probe.expectedPlainTextureHash, plainMeshGeometrySha256 = probe.expectedPlainMeshHash
        }, true));
        return new BundleInput
        {
            id = "visuals", assets = new[] { TexturePath, MeshPath, PrefabPath },
            addresses = new[] { "image", "geometry", "model" }
        };
    }

    private static Texture2D CreateTexture(string path, int width, int height, uint state)
    {
        // A serialized Texture2D asset bypasses platform import compression, preserving exact RGBA bytes.
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        texture.name = Path.GetFileNameWithoutExtension(path);
        var pixels = new Color32[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            pixels[i] = new Color32((byte)state, (byte)(state >> 8), (byte)(state >> 16), 255);
        }
        texture.SetPixels32(pixels); texture.Apply(false, false);
        ReplaceGeneratedAsset(texture, path);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Mesh CreateMesh(string path, int width, int height, int seed)
    {
        var vertices = new Vector3[width * height];
        var uv = new Vector2[vertices.Length];
        for (int row = 0; row < height; row++)
        for (int column = 0; column < width; column++)
        {
            int index = row * width + column;
            vertices[index] = new Vector3((column * 3 + seed) / 16f, ((row * 7 + column * 11 + seed) % 23) / 32f, (row * 5 - seed) / 16f);
            uv[index] = new Vector2(column / (float)(width - 1), row / (float)(height - 1));
        }
        var indices = new List<int>();
        for (int row = 0; row < height - 1; row++)
        for (int column = 0; column < width - 1; column++)
        {
            int a = row * width + column, b = a + 1, c = a + width, d = c + 1;
            indices.AddRange(new[] { a, c, b, b, c, d });
        }
        var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path), vertices = vertices, triangles = indices.ToArray(), uv = uv };
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        ReplaceGeneratedAsset(mesh, path);
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    private static void ReplaceGeneratedAsset(UnityEngine.Object value, string path)
    {
        var existing = AssetDatabase.LoadMainAssetAtPath(path);
        if (existing != null)
        {
            if (existing.GetType() != value.GetType()) throw new InvalidOperationException("Unexpected fixture asset type: " + path);
            EditorUtility.CopySerialized(value, existing); UnityEngine.Object.DestroyImmediate(value); EditorUtility.SetDirty(existing);
        }
        else AssetDatabase.CreateAsset(value, path);
    }

    public static void ValidateLeakRejection(GuardSettings settings, Scene scene, PlayerProbe probe)
    {
        // Test the actual build gate without starting additional player builds.
        var method = typeof(GuardBuild).GetMethod("ValidateInputs", BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null) throw new MissingMethodException("GuardBuild.ValidateInputs test integration must be updated.");
        Action validate = () => method.Invoke(null, new object[] { settings, new[] { scene.path } });
        validate();
        var plainTexture = probe.plainControlTexture; var plainMesh = probe.plainControlMesh;
        var previousPreloaded = PlayerSettings.GetPreloadedAssets();
        int rejected = 0;
        try
        {
            probe.plainControlTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            ExpectRejection(validate, "scene texture"); rejected++;
            probe.plainControlTexture = plainTexture;
            probe.plainControlMesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            ExpectRejection(validate, "scene mesh"); rejected++;
            probe.plainControlMesh = plainMesh;
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            PlayerSettings.SetPreloadedAssets(previousPreloaded.Concat(new UnityEngine.Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath) }).ToArray());
            ExpectRejection(validate, "preloaded texture"); rejected++;
        }
        finally
        {
            probe.plainControlTexture = plainTexture; probe.plainControlMesh = plainMesh;
            PlayerSettings.SetPreloadedAssets(previousPreloaded);
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        }
        validate();
        Debug.Log("AEGIS_ASSET_LEAK_CHECKS_PASS rejected=" + rejected + " clean=2");
    }

    private static void ExpectRejection(Action action, string scenario)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException is BuildFailedException)
        {
            if (!error.InnerException.Message.Contains("Protected bundle content also referenced")) throw;
            return;
        }
        throw new Exception("Protected asset leakage was accepted: " + scenario);
    }
}
