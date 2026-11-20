using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AegisGuard.Editor
{
    internal static class GuardAssetAudit
    {
        internal static void CheckPackedPlayer(BuildReport report, GuardSettings settings)
        {
            string[] roots = settings.bundles.SelectMany(b=>b.assets).ToArray();
            var protectedPaths = new HashSet<string>(AssetDatabase.GetDependencies(roots,true)
                .Where(p=>p.StartsWith("Assets/",StringComparison.Ordinal) && !p.EndsWith(".cs",StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            var leaks = report.packedAssets.SelectMany(p=>p.contents).Select(p=>p.sourceAssetPath)
                .Where(p=>!string.IsNullOrEmpty(p) && protectedPaths.Contains(p)).Distinct().ToArray();
            if (leaks.Length != 0) throw new BuildFailedException("Protected assets found in actual plaintext player archives:\n"+string.Join("\n",leaks));
            Debug.Log("AEGIS_PACKED_ASSET_AUDIT_PASS protectedDependencies="+protectedPaths.Count);
        }

        internal static void CheckRawStreamingCopies(GuardSettings settings)
        {
            const string streaming = "Assets/StreamingAssets";
            if (!Directory.Exists(streaming) || settings.dataFiles.Length==0) return;
            var hashes = new Dictionary<long,HashSet<string>>();
            using (var sha = SHA256.Create())
            {
                foreach (var file in settings.dataFiles)
                {
                    long length = new FileInfo(file.source).Length;
                    if (!hashes.TryGetValue(length,out var values)) hashes[length]=values=new HashSet<string>();
                    using (var input=File.OpenRead(file.source)) values.Add(Convert.ToBase64String(sha.ComputeHash(input)));
                }
                foreach (string file in Directory.GetFiles(streaming,"*",SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta",StringComparison.OrdinalIgnoreCase) || !hashes.TryGetValue(new FileInfo(file).Length,out var values)) continue;
                    using (var input=File.OpenRead(file))
                        if (values.Contains(Convert.ToBase64String(sha.ComputeHash(input)))) throw new BuildFailedException("Protected data has an identical plaintext StreamingAssets copy: "+file);
                }
            }
        }
    }
}
