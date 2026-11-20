using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor.Build;
using UnityEngine;

namespace AegisGuard.Editor
{
    internal static class GuardDistribution
    {
        // Publish this archive, not the Unity build directory that also holds native source backups.
        internal static string CreateWindowsArchive(string player)
        {
            player = Path.GetFullPath(player);
            string root = Path.GetDirectoryName(player), stem = Path.GetFileNameWithoutExtension(player);
            string output = Path.Combine(root,stem+".protected.zip");
            string data = Path.Combine(root,stem+"_Data");
            var files = Directory.GetFiles(root).Where(p=>p==player || p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p)=="UnityCrashHandler64.exe")
                .Concat(Directory.GetFiles(data,"*",SearchOption.AllDirectories));
            string d3d = Path.Combine(root,"D3D12");
            if (Directory.Exists(d3d)) files = files.Concat(Directory.GetFiles(d3d,"*",SearchOption.AllDirectories));
            var accepted = files.Where(p=>!p.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".mdb",StringComparison.OrdinalIgnoreCase) &&
                !p.EndsWith(".sym",StringComparison.OrdinalIgnoreCase) && !p.Contains("BackUpThisFolder") && !p.Contains("DoNotShip")).Distinct().ToArray();
            using (var stream=File.Create(output))
            using (var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                foreach(string file in accepted) zip.CreateEntryFromFile(file,file.Substring(root.Length+1).Replace('\\','/'),System.IO.Compression.CompressionLevel.Optimal);
            using (var zip=ZipFile.OpenRead(output))
            {
                if (!zip.Entries.Any(e=>e.FullName==stem+".exe") || zip.Entries.Any(e=>e.FullName.Contains("BackUpThisFolder")))
                    throw new BuildFailedException("Invalid Windows distribution archive.");
            }
            Debug.Log("AEGIS_DISTRIBUTION_READY "+output);
            return output;
        }
    }
}
