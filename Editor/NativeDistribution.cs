using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace AegisGuard.Editor
{
    /// <summary>
    /// Creates a project-private, physical copy of Unity's IL2CPP distribution.
    /// UNITY_IL2CPP_PATH selects a development layout in this exact Editor version;
    /// this helper supplies that layout without changing the installed Editor.
    /// It does not set environment variables or patch native source itself.
    /// </summary>
    internal static class NativeDistribution
    {
        private const string Version = "6000.3.13f1";
        private const string MarkerName = ".aegis-native-distribution-v1.json";

        [Serializable]
        private sealed class Marker
        {
            public string source;
            public string version;
            public string loaderHash;
        }

        internal static string Prepare(string destination)
        {
            if (Application.unityVersion != Version)
                throw new BuildFailedException("Native distribution layout is validated only for Unity " + Version + ".");
            if (string.IsNullOrWhiteSpace(destination))
                throw new ArgumentException("A private native distribution destination is required.", nameof(destination));

            string source = Path.GetFullPath(Path.Combine(EditorApplication.applicationContentsPath, "il2cpp"));
            string target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string project = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new BuildFailedException("Cannot determine the current Unity project directory.");
            string allowedRoot = Path.GetFullPath(Path.Combine(project, "Library", "AegisGuardEngine"));
            if (!target.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("Native distribution must be stored under this project's Library/AegisGuardEngine/ directory.");
            if (target.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("Refusing to modify the installed Unity IL2CPP distribution.");

            RejectLinkedAncestors(target, project);
            RequireFile(source, "libil2cpp/vm/MetadataLoader.cpp");
            RequireFile(source, "il2cpp_root");
            string sourceHash = Hash(Path.Combine(source, "libil2cpp", "vm", "MetadataLoader.cpp"));
            string markerPath = Path.Combine(target, MarkerName);
            if (File.Exists(markerPath))
            {
                var marker = JsonUtility.FromJson<Marker>(File.ReadAllText(markerPath));
                if (marker == null || marker.version != Version || marker.loaderHash != sourceHash ||
                    !string.Equals(marker.source, source, StringComparison.OrdinalIgnoreCase))
                    throw new BuildFailedException("The cached native distribution belongs to another Editor source. Choose a new cache directory.");
                // v0.2 also needs the post-build analytics helper. Upgrade older private caches.
                if (!File.Exists(Path.Combine(target,"Analytics/bin/Release/net8.0/Analytics.exe")))
                    CopyTree(Path.Combine(target,"build/deploy"),Path.Combine(target,"Analytics/bin/Release/net8.0"));
                ValidateLayout(target);
                return target;
            }

            try
            {
                // Physical copies are intentional. In particular, native source files must not
                // be hard links or directory links into the shared Unity installation.
                CopyTree(source, target);
                string deploy = Path.Combine(target, "build", "deploy");
                foreach (string tool in new[] { "il2cpp", "UnityLinker", "il2cpp-compile", "Analytics" })
                    CopyTree(deploy, Path.Combine(target, tool, "bin", "Release", "net8.0"));

                CopyApi(deploy, target, "Unity.Options", "netstandard2.0", "repos/UnityOptions");
                CopyApi(deploy, target, "Unity.Linker.Api", "netstandard2.0");
                CopyApi(deploy, target, "Unity.IL2CPP.Api", "netstandard2.0");
                CopyApi(deploy, target, "Unity.Api.Attributes", "netstandard2.0");
                CopyApi(deploy, target, "Unity.IL2CPP.Bee.IL2CPPExeCompileCppBuildProgram.Data", "netstandard2.0");
                CopyApi(deploy, target, "Unity.IL2CPP.Bee.BuildLogic", "net6.0");
                CopyTree(Path.Combine(source, "BCLExtensions"), Path.Combine(target, "build", "tests", "BCLExtensions", "net471"));
                ValidateLayout(target);
                File.WriteAllText(markerPath, JsonUtility.ToJson(new Marker
                {
                    source = source, version = Version, loaderHash = sourceHash
                }, true));
            }
            catch (Exception error)
            {
                // Leave an incomplete private copy for diagnosis/resume; never delete an
                // arbitrary destination or fall back to changing the installed Editor.
                throw new BuildFailedException("Could not prepare the private IL2CPP distribution at " + target + ": " + error.Message);
            }
            return target;
        }

        private static void CopyApi(string deploy, string target, string name, string tfm, string prefix = null)
        {
            string directory = prefix == null ? target : Path.Combine(target, prefix);
            directory = Path.Combine(directory, name, "bin", "Release", tfm);
            Directory.CreateDirectory(directory);
            string source = Path.Combine(deploy, name + ".dll");
            if (!File.Exists(source)) throw new FileNotFoundException("Required Unity build API is missing.", source);
            string output = Path.Combine(directory, name + ".dll");
            File.Copy(source, output, true);
            File.SetAttributes(output, FileAttributes.Normal);
        }

        private static void CopyTree(string source, string destination)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Required Unity distribution directory is missing: " + source);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked source directories are not supported: " + source);
            Directory.CreateDirectory(destination);
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked private distribution directories are not supported: " + destination);
            foreach (string file in Directory.GetFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked source files are not supported: " + file);
                string output = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(output) && (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked destination files are not supported: " + output);
                File.Copy(file, output, true);
                File.SetAttributes(output, FileAttributes.Normal);
            }
            foreach (string directory in Directory.GetDirectories(source))
                CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void ValidateLayout(string root)
        {
            RequireFile(root, "il2cpp_root");
            RequireFile(root, "libil2cpp/vm/MetadataLoader.cpp");
            RequireFile(root, "libil2cpp/vm/GlobalMetadata.cpp");
            foreach (string tool in new[] { "il2cpp", "UnityLinker", "il2cpp-compile", "Analytics" })
            {
                RequireFile(root, tool + "/bin/Release/net8.0/" + tool + ".exe");
                RequireFile(root, tool + "/bin/Release/net8.0/" + tool + ".dll");
            }
            RequireFile(root, "repos/UnityOptions/Unity.Options/bin/Release/netstandard2.0/Unity.Options.dll");
            foreach (string api in new[] { "Unity.Linker.Api", "Unity.IL2CPP.Api", "Unity.Api.Attributes", "Unity.IL2CPP.Bee.IL2CPPExeCompileCppBuildProgram.Data" })
                RequireFile(root, api + "/bin/Release/netstandard2.0/" + api + ".dll");
            RequireFile(root, "Unity.IL2CPP.Bee.BuildLogic/bin/Release/net6.0/Unity.IL2CPP.Bee.BuildLogic.dll");
            RequireFile(root, "build/tests/BCLExtensions/net471/System.Runtime.WindowsRuntime.dll");
        }

        private static void RequireFile(string root, string relative)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new FileNotFoundException("Required Unity native-distribution file is missing.", path);
        }

        private static void RejectLinkedAncestors(string path, string project)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new BuildFailedException("Linked native-distribution paths are unsupported: " + current.FullName);
                if (current.FullName.Equals(project, StringComparison.OrdinalIgnoreCase)) break;
                current = current.Parent;
            }
        }

        private static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
    }
}
