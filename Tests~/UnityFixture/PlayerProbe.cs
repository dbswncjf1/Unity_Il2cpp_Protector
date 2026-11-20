using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AegisGuard;
using UnityEngine;

public sealed class PlayerProbe : MonoBehaviour
{
    public string expectedPhraseHash;
    public string expectedEmbeddedHash;
    public string expectedDataHash;
    public string expectedBundleHash;
    public string expectedTextureHash;
    public string expectedMeshHash;
    public string expectedPlainTextureHash;
    public string expectedPlainMeshHash;
    // Intentional unencrypted extraction controls. Never point these at protected assets.
    public Texture2D plainControlTexture;
    public Mesh plainControlMesh;
    private GuardStore store;

    [GuardRename, GuardStrings]
    private static string ProtectedPhrase(int value)
    {
        if (value == 7) return "AEGIS_PRIVATE_한글_7348";
        return "AEGIS_OTHER_9320";
    }
    private static class NestedCaller { public static string Invoke() => ProtectedPhrase(7); }
    private IEnumerator Start()
    {
        // Flatten nested iterators so any loader exception is observed and converted to a failed smoke test.
        var iterators = new Stack<IEnumerator>(); iterators.Push(Probe());
        while (iterators.Count > 0)
        {
            object next = null;
            bool failed = false;
            try
            {
                var current = iterators.Peek();
                if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); iterators.Pop(); continue; }
                next = current.Current;
                if (next is IEnumerator child) { iterators.Push(child); continue; }
            }
            catch (Exception error) { Debug.LogError("AEGIS_PLAYER_FAIL: " + error); failed = true; }
            if (failed)
            {
                foreach (var iterator in iterators) (iterator as IDisposable)?.Dispose();
                store?.Dispose(); Application.Quit(1); yield break;
            }
            yield return next;
        }
        Debug.Log("AEGIS_PLAYER_PASS");
        Application.Quit(0);
    }
    private IEnumerator Probe()
    {
        if (Hash(NestedCaller.Invoke()) != expectedPhraseHash) throw new Exception("Protected method/literal failed.");
        if (plainControlTexture == null || HashTexture(plainControlTexture) != expectedPlainTextureHash)
            throw new Exception("Plain texture positive control failed.");
        if (plainControlMesh == null || HashMesh(plainControlMesh) != expectedPlainMeshHash)
            throw new Exception("Plain mesh positive control failed.");
        store = new GuardStore();
        using (var reader = new StreamReader(ProtectedResources.Open(typeof(PlayerProbe).Assembly, "Aegis.Test.Data")))
            if (Hash(reader.ReadToEnd()) != expectedEmbeddedHash) throw new Exception("Embedded resource roundtrip failed.");
        yield return store.Initialize();
        yield return store.PrepareData("balance");
        using (var reader = new StreamReader(store.OpenData("balance")))
            if (Hash(reader.ReadToEnd()) != expectedDataHash) throw new Exception("Data file roundtrip failed.");
        yield return store.LoadBundle("texts");
        if (Hash(store.LoadAsset<TextAsset>("texts", "message").text) != expectedBundleHash) throw new Exception("AssetBundle roundtrip failed.");
        yield return store.LoadBundle("visuals");
        var texture = store.LoadAsset<Texture2D>("visuals", "image");
        if (texture.width != 37 || texture.height != 23 || HashTexture(texture) != expectedTextureHash)
            throw new Exception("Protected texture pixel roundtrip failed.");
        var mesh = store.LoadAsset<Mesh>("visuals", "geometry");
        if (mesh.vertexCount != 63 || mesh.triangles.Length != 288 || HashMesh(mesh) != expectedMeshHash)
            throw new Exception("Protected mesh geometry roundtrip failed.");
        var prefab = store.LoadAsset<GameObject>("visuals", "model");
        var instance = Instantiate(prefab);
        try
        {
            var filter = instance.GetComponentInChildren<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || HashMesh(filter.sharedMesh) != expectedMeshHash)
                throw new Exception("Protected prefab dependency roundtrip failed.");
        }
        finally { Destroy(instance); }
        yield return null; // Destroy the instance before unloading its bundle dependencies.
        Debug.Log("AEGIS_PROTECTED_VISUALS_PASS texture=37x23 meshVertices=63 meshIndices=288 prefab=loaded");
        store.Dispose(); store = null;
    }
    private static string Hash(string text)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }

    public static string HashTexture(Texture2D texture)
    {
        var pixels = texture.GetPixels32();
        var bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].r; bytes[i * 4 + 1] = pixels[i].g;
            bytes[i * 4 + 2] = pixels[i].b; bytes[i * 4 + 3] = pixels[i].a;
        }
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    public static string HashMesh(Mesh mesh)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            var vertices = mesh.vertices; var indices = mesh.triangles;
            writer.Write(vertices.Length);
            foreach (var vertex in vertices) { writer.Write(vertex.x); writer.Write(vertex.y); writer.Write(vertex.z); }
            writer.Write(indices.Length);
            foreach (var index in indices) writer.Write(index);
            writer.Flush();
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        }
    }
}
