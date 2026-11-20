using System.Collections;
using System.IO;
using AegisGuard;
using UnityEngine;

public sealed class GuardExample : MonoBehaviour
{
    private GuardStore store;

    [GuardRename, GuardStrings]
    private static string ConfigId() => "balance";

    private IEnumerator Start()
    {
        store = new GuardStore();
        yield return store.Initialize();
        yield return store.PrepareData(ConfigId());
        using (var reader = new StreamReader(store.OpenData(ConfigId())))
        {
            // Feed the text to your parser. Do not log sensitive plaintext.
            var json = reader.ReadToEnd();
            Debug.Log("Protected configuration loaded, characters: " + json.Length);
        }
        yield return store.LoadBundle("characters");
        var prefab = store.LoadAsset<GameObject>("characters", "hero");
        Instantiate(prefab);
    }

    private void OnDestroy() => store?.Dispose();
}
