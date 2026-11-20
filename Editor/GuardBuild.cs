using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AegisGuard.Editor
{
    public static class GuardBuild
    {
        internal sealed class Session
        {
            public GuardSettings settings;
            public byte[] key;
            public string id, staging, work;
            public bool assembliesProtected;
            public ProtectionReport protection;
            public byte[] metadataKey;
            public string nativeRoot, metadataTool, previousIl2cppPath;
            public int metadataSealedCount;
        }
        internal static Session Active;

        [MenuItem("Tools/AegisGuard/Build Windows x64")]
        public static void Windows() => Build(BuildTarget.StandaloneWindows64, GuardSettings.Load().windowsOutput);
        [MenuItem("Tools/AegisGuard/Build Android")]
        public static void Android() => Build(BuildTarget.Android, GuardSettings.Load().androidOutput);

        public static BuildReport Build(BuildTarget target, string output)
        {
            if (Active != null) throw new BuildFailedException("AegisGuard build is already running.");
            var settings = GuardSettings.Load();
            if (!settings.enabled) throw new BuildFailedException("Enable AegisGuard in ProjectSettings/AegisGuard.json.");
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.Android) throw new BuildFailedException("Supported targets: Windows x64 and Android.");
            if (Application.unityVersion != "6000.3.13f1") throw new BuildFailedException("This release targets Unity 6000.3.13f1. Validate a port before changing this version gate.");
            if (EditorUserBuildSettings.activeBuildTarget != target) throw new BuildFailedException("Switch the active platform in Build Profiles first (CI: -buildTarget Win64 or Android).");
            if (string.IsNullOrWhiteSpace(output)) throw new BuildFailedException("Output path is required.");
            if (settings.assemblies == null || settings.bundles == null || settings.dataFiles == null || settings.embeddedResources == null)
                throw new BuildFailedException("Configuration arrays cannot be null.");
            if (settings.assemblies.Distinct(StringComparer.Ordinal).Count() != settings.assemblies.Length ||
                settings.embeddedResources.Distinct(StringComparer.Ordinal).Count() != settings.embeddedResources.Length)
                throw new BuildFailedException("Duplicate assembly or embedded-resource configuration.");
            foreach (var name in settings.assemblies)
                if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("AegisGuard", StringComparison.Ordinal))
                    throw new BuildFailedException("Only explicit user assembly names without .dll are allowed: " + name);
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new BuildFailedException("Add at least one enabled scene to Build Profiles.");
            ValidateInputs(settings, scenes);
            GuardAssetAudit.CheckRawStreamingCopies(settings);
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var named = NamedBuildTarget.FromBuildTargetGroup(group);
            var previousBackend = PlayerSettings.GetScriptingBackend(named);
            bool previousExport = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            bool previousAppBundle = EditorUserBuildSettings.buildAppBundle;
            var session = new Session { settings = settings, key = GuardCrypto.RandomBytes(32), id = Guid.NewGuid().ToString("N") };
            session.work = Path.GetFullPath(Path.Combine("Library", "AegisGuard", session.id));
            session.staging = Path.Combine(session.work, "encrypted");
            Directory.CreateDirectory(session.staging);
            File.WriteAllText(Path.Combine(session.staging, "CONTENT_AUTHORIZATION_NOTICE.txt"), GuardNotice.Text, new UTF8Encoding(false));
            Active = session;
            try
            {
                PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.IL2CPP);
                if (settings.nativeMetadataProtection) GuardNativeProtection.Begin(session);
                if (target == BuildTarget.Android)
                {
                    EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
                    EditorUserBuildSettings.buildAppBundle = output.EndsWith(".aab", StringComparison.OrdinalIgnoreCase);
                    if (!output.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) && !EditorUserBuildSettings.buildAppBundle)
                        throw new BuildFailedException("Android output must end in .apk or .aab.");
                }
                string manifest = PackContent(session, target);
                var options = new BuildPlayerOptions
                {
                    scenes = scenes, target = target, targetGroup = group, locationPathName = output,
                    options = BuildOptions.CleanBuildCache | BuildOptions.DetailedBuildReport,
                    assetBundleManifestPath = manifest
                };
                var report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("AegisGuard player build failed: " + report.summary.result);
                if (!session.assembliesProtected) throw new BuildFailedException("Protection callback did not run. Do not distribute this build.");
                GuardAssetAudit.CheckPackedPlayer(report,settings);
                if (settings.nativeMetadataProtection) GuardNativeProtection.FinishPlayer(session,target,output);
                string distribution = target == BuildTarget.StandaloneWindows64 ? GuardDistribution.CreateWindowsArchive(output) : Path.GetFullPath(output);
                string reportFolder = Path.GetFullPath(Path.Combine("GuardBuildReports", session.id));
                Directory.CreateDirectory(reportFolder);
                File.WriteAllLines(Path.Combine(reportFolder, "rename-map.tsv"), session.protection.mappings);
                File.WriteAllText(Path.Combine(reportFolder, "summary.txt"),
                    "Unity: " + Application.unityVersion + "\nTarget: " + target + "\nBuild: " + session.id +
                    "\nRenamed methods: " + session.protection.renamedMethods + "\nProtected literals: " + session.protection.protectedStrings +
                    "\nProtected embedded resources: " + session.protection.protectedResources + "\nOutput: " + Path.GetFullPath(output) + "\nDistribution: " + distribution + "\nNative metadata protected: " + settings.nativeMetadataProtection + "\n");
                Debug.Log("AegisGuard build succeeded. Private report: " + reportFolder);
                return report;
            }
            finally
            {
                Active = null;
                if (settings.nativeMetadataProtection) GuardNativeProtection.End(session);
                Array.Clear(session.key, 0, session.key.Length);
                PlayerSettings.SetScriptingBackend(named, previousBackend);
                if (target == BuildTarget.Android)
                {
                    EditorUserBuildSettings.exportAsGoogleAndroidProject = previousExport;
                    EditorUserBuildSettings.buildAppBundle = previousAppBundle;
                }
                // Only the GUID directory allocated by this build is removed.
                string root = Path.GetFullPath(Path.Combine("Library", "AegisGuard")) + Path.DirectorySeparatorChar;
                if (session.work.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(session.work))
                    Directory.Delete(session.work, true);
            }
        }

        private static string PackContent(Session session, BuildTarget target)
        {
            var entries = new List<GuardCatalogEntry>();
            string manifestPath = null;
            if (session.settings.bundles.Length > 0)
            {
                string bundleDirectory = Path.Combine(session.work, "bundles");
                Directory.CreateDirectory(bundleDirectory);
                var inputs = session.settings.bundles;
                var names = inputs.ToDictionary(b => BundleName(b.id), b => b.id, StringComparer.Ordinal);
                var builds = inputs.Select(b => new AssetBundleBuild
                {
                    assetBundleName = BundleName(b.id), assetNames = b.assets,
                    addressableNames = b.addresses.Length == 0 ? b.assets : b.addresses
                }).ToArray();
                var manifest = BuildPipeline.BuildAssetBundles(bundleDirectory, builds,
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode, target);
                if (manifest == null) throw new BuildFailedException("AssetBundle build failed.");
                manifestPath = Path.Combine(bundleDirectory, "bundles.manifest");
                foreach (var bundle in inputs)
                {
                    string name = BundleName(bundle.id);
                    PackFile(Path.Combine(bundleDirectory, name), session, bundle.id);
                    entries.Add(new GuardCatalogEntry { id = bundle.id, kind = "bundle", dependencies = manifest.GetDirectDependencies(name).Select(n => names[n]).ToArray() });
                }
                UnityEngine.Object.DestroyImmediate(manifest);
            }
            foreach (var data in session.settings.dataFiles)
            {
                PackFile(Path.GetFullPath(data.source), session, data.id);
                entries.Add(new GuardCatalogEntry { id = data.id, kind = "data" });
            }
            byte[] json = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new GuardCatalog { buildId = session.id, entries = entries.ToArray() }));
            using (var input = new MemoryStream(json))
            using (var output = File.Create(Path.Combine(session.staging, GuardNames.FileName(GuardNames.CatalogId))))
                GuardCrypto.Pack(input, output, session.key, "file/" + GuardNames.CatalogId);
            Array.Clear(json, 0, json.Length);
            return manifestPath;
        }

        private static string BundleName(string id) => GuardNames.FileName(id).Replace(".agc", ".bundle");
        private static void PackFile(string input, Session session, string id)
        {
            using (var source = File.OpenRead(input))
            using (var destination = File.Create(Path.Combine(session.staging, GuardNames.FileName(id))))
                GuardCrypto.Pack(source, destination, session.key, "file/" + id);
        }

        private static void ValidateInputs(GuardSettings settings, string[] scenes)
        {
            if (Directory.Exists("Assets/StreamingAssets/AegisGuard")) throw new BuildFailedException("Remove the conflicting Assets/StreamingAssets/AegisGuard directory.");
            var ids = new HashSet<string>(StringComparer.Ordinal) { GuardNames.CatalogId };
            var protectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bundle in settings.bundles)
            {
                if (bundle == null || string.IsNullOrWhiteSpace(bundle.id) || !ids.Add(bundle.id) || bundle.assets == null || bundle.assets.Length == 0 || bundle.addresses == null ||
                    (bundle.addresses.Length != 0 && bundle.addresses.Length != bundle.assets.Length)) throw new BuildFailedException("Invalid/duplicate bundle or address configuration.");
                if (bundle.addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count() != bundle.addresses.Length) throw new BuildFailedException("Duplicate asset addresses: " + bundle.id);
                foreach (var asset in bundle.assets)
                {
                    if (!asset.StartsWith("Assets/", StringComparison.Ordinal) || !File.Exists(asset) || AssetDatabase.IsValidFolder(asset) ||
                        asset.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || !protectedRoots.Add(asset))
                        throw new BuildFailedException("Bundle assets must be unique existing non-scene asset paths: " + asset);
                }
            }
            foreach (var file in settings.dataFiles)
            {
                if (file == null || string.IsNullOrWhiteSpace(file.id) || !ids.Add(file.id) || string.IsNullOrWhiteSpace(file.source)) throw new BuildFailedException("Invalid/duplicate data input.");
                string root = Path.GetFullPath("GuardSource") + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(file.source);
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new BuildFailedException("Data inputs must be existing files under GuardSource/: " + file.source);
                for (var info = new FileInfo(path) as FileSystemInfo; info != null; info = info is FileInfo f ? f.Directory : ((DirectoryInfo)info).Parent)
                {
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new BuildFailedException("Linked data input paths are unsupported: " + file.source);
                    if (info.FullName.TrimEnd('\\', '/') == Path.GetFullPath("GuardSource").TrimEnd('\\', '/')) break;
                }
            }
            if (protectedRoots.Count == 0) return;
            var plainRoots = new List<string>(scenes);
            plainRoots.AddRange(AssetDatabase.GetAllAssetPaths().Where(p => p.Contains("/Resources/") && !AssetDatabase.IsValidFolder(p)));
            plainRoots.AddRange(PlayerSettings.GetPreloadedAssets().Where(a => a != null).Select(AssetDatabase.GetAssetPath));
            var plain = new HashSet<string>(AssetDatabase.GetDependencies(plainRoots.ToArray(), true), StringComparer.OrdinalIgnoreCase);
            var leaks = AssetDatabase.GetDependencies(protectedRoots.ToArray(), true)
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && plain.Contains(p)).ToArray();
            if (leaks.Length > 0) throw new BuildFailedException("Protected bundle content also referenced by scenes/Resources/preloaded assets:\n" + string.Join("\n", leaks));
        }
    }

    public sealed class GuardStreamingProcessor : BuildPlayerProcessor
    {
        public override int callbackOrder => 10000;
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            if (GuardBuild.Active != null) context.AddAdditionalPathToStreamingAssets(GuardBuild.Active.staging, "AegisGuard");
        }
    }

    public sealed class GuardCallbacks : IPreprocessBuildWithReport, IPostBuildPlayerScriptDLLs
    {
        public int callbackOrder => 10000;
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!File.Exists(GuardSettings.PathName) || !GuardSettings.Load().enabled) return;
            if (GuardBuild.Active == null) throw new BuildFailedException("Use Tools > AegisGuard > Build, or AegisGuard.Editor.GuardBuild.Windows/Android in CI.");
            var forbidden = BuildOptions.Development | BuildOptions.AllowDebugging | BuildOptions.ConnectWithProfiler | BuildOptions.EnableDeepProfilingSupport;
            if ((report.summary.options & forbidden) != 0) throw new BuildFailedException("Protected builds must be release builds.");
        }
        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            var session = GuardBuild.Active;
            if (session == null) return;
            var assemblies = report.GetFiles().Select(f => f.path)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            session.protection = AssemblyProtector.Protect(assemblies, session.settings.assemblies, session.settings.embeddedResources, session.key, session.id);
            session.assembliesProtected = true;
            Debug.Log("AegisGuard: renamed " + session.protection.renamedMethods + " methods, encrypted " + session.protection.protectedStrings + " literals and " + session.protection.protectedResources + " embedded resources.");
        }
    }
}
