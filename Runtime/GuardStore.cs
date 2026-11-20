using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace AegisGuard
{
    /// <summary>Main-thread API. Keep the store alive while its bundles/assets are in use.</summary>
    public sealed class GuardStore : IDisposable
    {
        private readonly Dictionary<string, GuardCatalogEntry> entries = new Dictionary<string, GuardCatalogEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, LoadedBundle> bundles = new Dictionary<string, LoadedBundle>(StringComparer.Ordinal);
        private readonly List<string> loadOrder = new List<string>();
        private string cacheDirectory;
        private bool ready, disposed, busy;
        private sealed class LoadedBundle { public AssetBundle bundle; public ProtectedReadStream stream; }

        public IEnumerator Initialize()
        {
            CheckAlive();
            if (ready || busy) throw new InvalidOperationException("Store is already initialized or busy.");
            busy = true;
            try
            {
                // A per-build key fingerprint separates caches even if application version is unchanged.
                // The root key is released before the first coroutine suspension.
                var fingerprintKey = BuildKey.GetCopy();
                try
                {
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        cacheDirectory = Path.Combine(Application.persistentDataPath, "AegisGuard", BitConverter.ToString(sha.ComputeHash(fingerprintKey)).Replace("-", ""));
                }
                finally { Array.Clear(fingerprintKey, 0, fingerprintKey.Length); }
                Directory.CreateDirectory(cacheDirectory);
                yield return EnsureLocal(GuardNames.CatalogId, true);
                string json;
                using (var stream = OpenLocal(GuardNames.CatalogId))
                {
                    if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Catalog is too large.");
                    using (var reader = new StreamReader(stream, Encoding.UTF8)) json = reader.ReadToEnd();
                }
                var catalog = JsonUtility.FromJson<GuardCatalog>(json);
                if (catalog == null || catalog.entries == null) throw new InvalidDataException("Invalid catalog.");
                foreach (var entry in catalog.entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.id) || entries.ContainsKey(entry.id) ||
                        (entry.kind != "bundle" && entry.kind != "data") || entry.dependencies == null)
                        throw new InvalidDataException("Invalid catalog entry.");
                    entries.Add(entry.id, entry);
                }
                ready = true;
            }
            finally
            {
                busy = false;
                if (!ready)
                {
                    entries.Clear();
                }
            }
        }

        // On Android the source is inside an APK/JAR. Copy CIPHERTEXT through UnityWebRequest.
        // DownloadHandlerFile avoids a full-size managed byte[] and no plaintext file is created.
        private IEnumerator EnsureLocal(string id, bool replace = false)
        {
            CheckAlive();
            string file = GuardNames.FileName(id), destination = Path.Combine(cacheDirectory, file);
            if (!replace && File.Exists(destination)) yield break;
            string source = Application.streamingAssetsPath.TrimEnd('/', '\\') + "/AegisGuard/" + file;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (source.Contains("://"))
                {
                    using (var request = UnityWebRequest.Get(source))
                    {
                        request.downloadHandler = new DownloadHandlerFile(temporary) { removeFileOnAbort = true };
                        request.timeout = 120;
                        yield return request.SendWebRequest();
                        CheckAlive();
                        if (request.result != UnityWebRequest.Result.Success) throw new IOException("Protected content transfer failed: " + request.error);
                    }
                }
                else File.Copy(source, temporary, true);
                // Validate header + dimensions before committing. Individual chunks are checked on read.
                using (new ProtectedReadStream(File.OpenRead(temporary), BuildKey.GetCopy, "file/" + id)) { }
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private ProtectedReadStream OpenLocal(string id) => new ProtectedReadStream(
            new FileStream(Path.Combine(cacheDirectory, GuardNames.FileName(id)), FileMode.Open, FileAccess.Read, FileShare.Read), BuildKey.GetCopy, "file/" + id);

        public IEnumerator PrepareData(string id)
        {
            CheckReady();
            Require(id, "data");
            yield return EnsureLocal(id);
        }

        // Caller owns and must Dispose the returned stream. Call PrepareData first.
        public ProtectedReadStream OpenData(string id)
        {
            CheckReady(); Require(id, "data"); return OpenLocal(id);
        }

        public IEnumerator LoadBundle(string id)
        {
            CheckReady();
            if (busy) throw new InvalidOperationException("Serialize bundle loads on this store.");
            busy = true;
            try { yield return LoadRecursive(id, new HashSet<string>(StringComparer.Ordinal)); }
            finally { busy = false; }
        }

        private IEnumerator LoadRecursive(string id, HashSet<string> visiting)
        {
            CheckReady();
            if (bundles.ContainsKey(id)) yield break;
            var entry = Require(id, "bundle");
            if (!visiting.Add(id)) throw new InvalidDataException("Cyclic bundle dependencies.");
            foreach (var dependency in entry.dependencies) yield return LoadRecursive(dependency, visiting);
            yield return EnsureLocal(id);
            CheckReady();
            var stream = OpenLocal(id);
            AssetBundle bundle = null;
            bool transferred = false;
            try
            {
                // Synchronous mount avoids disposing a stream while a native async load still uses it.
                bundle = AssetBundle.LoadFromStream(stream, 0, 32768);
                if (bundle == null) throw new InvalidDataException("Protected AssetBundle could not be loaded.");
                bundles.Add(id, new LoadedBundle { bundle = bundle, stream = stream });
                try { loadOrder.Add(id); }
                catch { bundles.Remove(id); throw; }
                transferred = true;
            }
            finally
            {
                try
                {
                    if (!transferred)
                    {
                        try { if (bundle != null) bundle.Unload(true); }
                        finally { stream.Dispose(); }
                    }
                }
                finally { visiting.Remove(id); }
            }
        }

        public T LoadAsset<T>(string bundleId, string assetName) where T : UnityEngine.Object
        {
            CheckReady();
            if (!bundles.TryGetValue(bundleId, out var item)) throw new InvalidOperationException("Call LoadBundle first.");
            var asset = item.bundle.LoadAsset<T>(assetName);
            if (asset == null) throw new FileNotFoundException("Asset was not found in the protected bundle.", assetName);
            return asset;
        }

        private GuardCatalogEntry Require(string id, string kind)
        {
            if (!entries.TryGetValue(id, out var entry) || entry.kind != kind) throw new KeyNotFoundException("Unknown protected resource: " + id);
            return entry;
        }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException(nameof(GuardStore)); }
        private void CheckReady() { CheckAlive(); if (!ready) throw new InvalidOperationException("Initialize the store first."); }

        // All objects loaded through this store must no longer be in use. Explicitly unload dependents first.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; ready = false;
            List<Exception> errors = null;
            try
            {
                for (int i = loadOrder.Count - 1; i >= 0; i--)
                {
                    if (!bundles.TryGetValue(loadOrder[i], out var item)) continue;
                    try { item.bundle.Unload(true); }
                    catch (Exception error) { (errors ?? (errors = new List<Exception>())).Add(error); }
                    finally
                    {
                        try { item.stream.Dispose(); }
                        catch (Exception error) { (errors ?? (errors = new List<Exception>())).Add(error); }
                    }
                }
            }
            finally { bundles.Clear(); loadOrder.Clear(); entries.Clear(); }
            if (errors != null) throw new AggregateException("Protected resource cleanup failed after all owned streams were disposed.", errors);
        }
    }
}
