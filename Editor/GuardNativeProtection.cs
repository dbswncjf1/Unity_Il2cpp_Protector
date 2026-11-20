using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using Debug = UnityEngine.Debug;

namespace AegisGuard.Editor
{
    internal static class GuardNativeProtection
    {
        internal const string OriginalLoaderHash = "851dc9ba32dbfad0dd7eb9c5a57aac4d30cfe3b948b5c7b42ffc149eebb95bec";
        internal const string Marker = "AEGIS_NATIVE_METADATA_READY";
        [Serializable] private sealed class NativeFileRecord
        {
            public string relativePath, absolutePath, sha256;
        }
        [Serializable] private sealed class NativePatchRecord
        {
            public int formatVersion = 1;
            public string buildId, unityVersion, overrideRoot, installedLoaderPath, installedLoaderSha256Before, installedLoaderSha256After;
            public bool installedLoaderUnchanged, privateGlobalMetadataUnchanged;
            public NativeFileRecord patchedLoader, installedGlobalMetadata, unchangedGlobalMetadata;
            public NativeFileRecord[] addedHelpers;
            public string generatedKeyHeaderPath, generatedKeyHeaderSha256, clearedKeyHeaderSha256, outputPath, outputHashPath, outputSha256;
            public string lifecycle = "One metadata initialization per OS process. Native key shares are wiped after reconstruction; metadata remains plaintext until shutdown. Reinitialization is rejected.";
            public string mitigations = "Dedicated VirtualAlloc/mmap allocation; read-only after authentication; best-effort VirtualLock/mlock; Android MADV_DONTDUMP where supported. Runtime AEGIS_NATIVE_MEMORY log reports actual status (-1=unsupported, 0=failed, 1=enabled).";
            public string limitations = "These changes do not block authorized ReadProcessMemory, in-process readers, privileged tracing, or extraction of key shares from the original PE/ELF file. MADV_DONTDUMP concerns kernel core dumps only; locking concerns paging only. GlobalMetadata.cpp and the metadata header/layout are unchanged.";
        }
        private sealed class DiffRow
        {
            public char kind;
            public string text;
            public int oldLine, newLine;
        }
        private static NativePatchRecord activeRecord;
        private static string PackageRoot => Path.GetFullPath(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GuardNativeProtection).Assembly).resolvedPath);
        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

        internal static void Begin(GuardBuild.Session session)
        {
            File.Copy(Path.Combine(PackageRoot,"Native~/LICENSE.txt"),Path.Combine(session.staging,"THIRD_PARTY_MONOCYPHER.txt"),true);
            session.metadataKey = GuardCrypto.RandomBytes(32);
            session.previousIl2cppPath = Environment.GetEnvironmentVariable("UNITY_IL2CPP_PATH");
            session.nativeRoot = NativeDistribution.Prepare(Path.GetFullPath("Library/AegisGuardEngine/6000.3.13f1"));
            string installed = Path.Combine(EditorApplication.applicationContentsPath,"il2cpp/libil2cpp/vm/MetadataLoader.cpp");
            if (Hash(installed) != OriginalLoaderHash) throw new BuildFailedException("Unexpected installed MetadataLoader.cpp; refusing an unverified engine patch.");
            string native = Path.Combine(session.nativeRoot,"libil2cpp/vm");
            foreach (string name in new[] { "AegisMetadata.h", "monocypher.c", "monocypher.h" })
                File.Copy(Path.Combine(PackageRoot,"Native~",name),Path.Combine(native,name),true);
            var left = GuardCrypto.RandomBytes(32); var right = new byte[32];
            for (int i = 0; i < 32; i++) right[i] = (byte)(left[i] ^ session.metadataKey[i]);
            File.WriteAllText(Path.Combine(native,"AegisBuildKey.h"),
                "#pragma once\n#include <stdint.h>\nstatic uint8_t AegisKeyLeft[32]={" + string.Join(",",left) + "};\n" +
                "static uint8_t AegisKeyRight[32]={" + string.Join(",",right) + "};\n");
            Array.Clear(left,0,left.Length); Array.Clear(right,0,right.Length);
            string originalLoader = File.ReadAllText(installed);
            string loader = originalLoader.Replace("#include \"MetadataLoader.h\"", "#include \"MetadataLoader.h\"\n#include \"AegisMetadata.h\"");
            loader = loader.Replace("void* fileBuffer = utils::MemoryMappedFile::Map(handle);",
                "int64_t aegisLength = os::File::GetLength(handle, &error);\n" +
                "    if (error != 0 || aegisLength < 56 || aegisLength > 1024LL*1024*1024) { os::File::Close(handle, &error); return NULL; }\n" +
                "    void* fileBuffer = utils::MemoryMappedFile::Map(handle);");
            loader = loader.Replace("    return fileBuffer;", "    void* aegisPlain = AegisMetadata::Open(fileBuffer, aegisLength);\n" +
                "    if (fileBuffer) utils::MemoryMappedFile::Unmap(fileBuffer);\n" +
                "    if (!aegisPlain) { utils::Logging::Write(\"AEGIS_NATIVE_METADATA_REJECTED\"); exit(86); }\n" +
                "    utils::Logging::Write(\"" + Marker + "\");\n    return aegisPlain;");
            // Unity can show an early initialization dialog on a NULL metadata result,
            // even in batch mode. Fail deterministically before managed code starts.
            loader = loader.Replace("return NULL;", "utils::Logging::Write(\"AEGIS_NATIVE_METADATA_REJECTED\"); exit(86);");
            int unload = loader.IndexOf("void il2cpp::vm::MetadataLoader::UnloadMetadataFile",StringComparison.Ordinal);
            if (unload < 0 || !loader.Contains("AegisMetadata::Open")) throw new BuildFailedException("Native loader patch anchors missing.");
            loader = loader.Substring(0,unload) + "void il2cpp::vm::MetadataLoader::UnloadMetadataFile(void* fileBuffer)\n{\n    AegisMetadata::Close(fileBuffer);\n}\n";
            File.WriteAllText(Path.Combine(native,"MetadataLoader.cpp"),loader);
            activeRecord = new NativePatchRecord
            {
                buildId = session.id, unityVersion = UnityEngine.Application.unityVersion, overrideRoot = session.nativeRoot,
                installedLoaderPath = Path.GetFullPath(installed), installedLoaderSha256Before = Hash(installed),
                patchedLoader = Describe(session.nativeRoot,"libil2cpp/vm/MetadataLoader.cpp"),
                installedGlobalMetadata = Describe(Path.Combine(EditorApplication.applicationContentsPath,"il2cpp"),"libil2cpp/vm/GlobalMetadata.cpp"),
                unchangedGlobalMetadata = Describe(session.nativeRoot,"libil2cpp/vm/GlobalMetadata.cpp"),
                addedHelpers = new[] { "AegisMetadata.h", "monocypher.c", "monocypher.h" }.Select(name => Describe(session.nativeRoot,"libil2cpp/vm/" + name)).ToArray(),
                generatedKeyHeaderPath = Path.Combine(native,"AegisBuildKey.h"), generatedKeyHeaderSha256 = Hash(Path.Combine(native,"AegisBuildKey.h"))
            };
            activeRecord.privateGlobalMetadataUnchanged = activeRecord.installedGlobalMetadata.sha256 == activeRecord.unchangedGlobalMetadata.sha256;
            if (!activeRecord.privateGlobalMetadataUnchanged) throw new BuildFailedException("Private GlobalMetadata.cpp differs from the installed engine; refusing an unverified modification.");
            WriteRecord(session);
            // Private report only: the diff contains loader edits, never key-header bytes.
            File.WriteAllText(Path.Combine(ReportDirectory(session),"MetadataLoader.patch"), UnifiedDiff(originalLoader,loader),new UTF8Encoding(false));
            string toolRoot = Path.GetFullPath("Library/AegisGuardMetadataTool");
            string source = Path.Combine(toolRoot,"src"); Directory.CreateDirectory(source);
            foreach (string name in new[] { "MetadataPacker.csproj", "Program.cs" })
                File.Copy(Path.Combine(PackageRoot,"Tools~/MetadataPacker",name),Path.Combine(source,name),true);
            session.metadataTool = Path.Combine(toolRoot,"bin/AegisGuard.MetadataPacker.dll");
            Run("build " + Quote(Path.Combine(source,"MetadataPacker.csproj")) + " -c Release --nologo -o " + Quote(Path.GetDirectoryName(session.metadataTool)), null);
            Run(Quote(session.metadataTool) + " self-test",null);
            Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH",session.nativeRoot);
            Debug.Log("AEGIS_NATIVE_LOADER_PREPARED " + session.nativeRoot);
        }

        internal static void Seal(string path)
        {
            var session = GuardBuild.Active;
            if (session == null || !session.settings.nativeMetadataProtection) return;
            if (!File.Exists(path)) throw new BuildFailedException("Expected native metadata path is absent: " + path);
            Run(Quote(session.metadataTool) + " seal " + Quote(Path.GetFullPath(path)),Convert.ToBase64String(session.metadataKey));
            session.metadataSealedCount++;
        }

        internal static void FinishPlayer(GuardBuild.Session session, BuildTarget target, string output)
        {
            if (target == BuildTarget.StandaloneWindows64)
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(output));
                Seal(Path.Combine(root,Path.GetFileNameWithoutExtension(output)+"_Data/il2cpp_data/Metadata/global-metadata.dat"));
                if (!Contains(File.ReadAllBytes(Path.Combine(root,"GameAssembly.dll")),Marker)) throw new BuildFailedException("Custom native loader marker is absent in GameAssembly.dll.");
            }
            else
            {
                using (var zip = ZipFile.OpenRead(Path.GetFullPath(output)))
                {
                    var metadata = zip.Entries.Where(e=>e.FullName.EndsWith("/global-metadata.dat",StringComparison.Ordinal)).ToArray();
                    if (metadata.Length != 1) throw new BuildFailedException("Expected exactly one sealed metadata file in Android output.");
                    using (var stream = metadata[0].Open())
                    {
                        var magic = new byte[8]; if (stream.Read(magic,0,8)!=8 || Encoding.ASCII.GetString(magic)!="AGMETA02") throw new BuildFailedException("Android metadata was not sealed before signing.");
                    }
                    var libraries = zip.Entries.Where(e=>e.FullName.EndsWith("/libil2cpp.so",StringComparison.Ordinal)).ToArray();
                    if (libraries.Length == 0) throw new BuildFailedException("Android IL2CPP library absent.");
                    foreach (var library in libraries)
                    using (var input = library.Open())
                    using (var memory = new MemoryStream())
                    {
                        input.CopyTo(memory);
                        if (!Contains(memory.ToArray(),Marker)) throw new BuildFailedException("Custom native loader absent in " + library.FullName);
                    }
                }
            }
            if (session.metadataSealedCount != 1) throw new BuildFailedException("Expected exactly one native metadata sealing callback.");
            if (activeRecord != null)
            {
                activeRecord.outputPath = Path.GetFullPath(output);
                activeRecord.outputHashPath = target == BuildTarget.StandaloneWindows64 ? Path.Combine(Path.GetDirectoryName(activeRecord.outputPath),"GameAssembly.dll") : activeRecord.outputPath;
                activeRecord.outputSha256 = Hash(activeRecord.outputHashPath);
                WriteRecord(session);
            }
            Debug.Log("AEGIS_NATIVE_OUTPUT_VERIFIED target=" + target);
        }

        internal static bool Contains(byte[] bytes, string text)
        {
            byte[] needle = Encoding.ASCII.GetBytes(text);
            for (int i=0;i<=bytes.Length-needle.Length;i++)
            {
                int j=0; while(j<needle.Length && bytes[i+j]==needle[j]) j++;
                if (j==needle.Length) return true;
            }
            return false;
        }
        internal static void End(GuardBuild.Session session)
        {
            if (session.metadataKey != null) Array.Clear(session.metadataKey,0,session.metadataKey.Length);
            if (session.nativeRoot != null)
            {
                Environment.SetEnvironmentVariable("UNITY_IL2CPP_PATH",session.previousIl2cppPath);
                string header = Path.Combine(session.nativeRoot,"libil2cpp/vm/AegisBuildKey.h");
                if (File.Exists(header)) File.WriteAllText(header,"#error AegisGuard build key cleared; run a new protected build.\n");
                if (activeRecord != null)
                {
                    if (File.Exists(header)) activeRecord.clearedKeyHeaderSha256 = Hash(header);
                    WriteRecord(session);
                }
            }
            activeRecord = null;
        }
        private static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        private static NativeFileRecord Describe(string root, string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root,relative));
            return new NativeFileRecord { relativePath = relative, absolutePath = path, sha256 = Hash(path) };
        }
        private static string ReportDirectory(GuardBuild.Session session)
        {
            string folder = Path.GetFullPath(Path.Combine("GuardBuildReports",session.id));
            Directory.CreateDirectory(folder); return folder;
        }
        private static void WriteRecord(GuardBuild.Session session)
        {
            activeRecord.installedLoaderSha256After = Hash(activeRecord.installedLoaderPath);
            activeRecord.installedLoaderUnchanged = activeRecord.installedLoaderSha256Before == activeRecord.installedLoaderSha256After;
            File.WriteAllText(Path.Combine(ReportDirectory(session),"engine-modifications.json"),UnityEngine.JsonUtility.ToJson(activeRecord,true),new UTF8Encoding(false));
        }
        private static string UnifiedDiff(string before, string after)
        {
            string[] a = before.Replace("\r\n","\n").TrimEnd('\n').Split('\n');
            string[] b = after.Replace("\r\n","\n").TrimEnd('\n').Split('\n');
            var longest = new int[a.Length + 1,b.Length + 1];
            for (int i=a.Length-1;i>=0;i--)
            for (int j=b.Length-1;j>=0;j--)
                longest[i,j] = a[i] == b[j] ? 1 + longest[i+1,j+1] : Math.Max(longest[i+1,j],longest[i,j+1]);
            var rows = new List<DiffRow>(); int x=0,y=0;
            while (x<a.Length || y<b.Length)
            {
                var row = new DiffRow { oldLine=x+1,newLine=y+1 };
                if (x<a.Length && y<b.Length && a[x]==b[y]) { row.kind=' '; row.text=a[x]; x++; y++; }
                else if (y<b.Length && (x==a.Length || longest[x,y+1]>=longest[x+1,y])) { row.kind='+'; row.text=b[y++]; }
                else { row.kind='-'; row.text=a[x++]; }
                rows.Add(row);
            }
            var result = new StringBuilder("--- installed/libil2cpp/vm/MetadataLoader.cpp\n+++ private/libil2cpp/vm/MetadataLoader.cpp\n");
            int position=0;
            while (position<rows.Count)
            {
                while (position<rows.Count && rows[position].kind==' ') position++;
                if (position==rows.Count) break;
                int start=Math.Max(0,position-2),lastChange=position,end=position+1;
                while (end<rows.Count && end<=lastChange+4)
                {
                    if (rows[end].kind!=' ') lastChange=end;
                    end++;
                }
                end=Math.Min(rows.Count,lastChange+3);
                int oldCount=0,newCount=0;
                for (int i=start;i<end;i++) { if(rows[i].kind!='+')oldCount++; if(rows[i].kind!='-')newCount++; }
                result.Append("@@ -").Append(rows[start].oldLine).Append(',').Append(oldCount).Append(" +").Append(rows[start].newLine).Append(',').Append(newCount).Append(" @@\n");
                for (int i=start;i<end;i++) result.Append(rows[i].kind).Append(rows[i].text).Append('\n');
                position=end;
            }
            return result.ToString();
        }
        private static void Run(string arguments, string secretInput)
        {
            var info = new ProcessStartInfo("dotnet",arguments) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true, RedirectStandardInput=true, StandardInputEncoding=new UTF8Encoding(false) };
            using (var process = Process.Start(info))
            {
                var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
                if (secretInput != null) process.StandardInput.WriteLine(secretInput);
                process.StandardInput.Close();
                if (!process.WaitForExit(180000)) { process.Kill(); throw new BuildFailedException("Metadata tool timed out."); }
                string output=stdout.GetAwaiter().GetResult(), error=stderr.GetAwaiter().GetResult();
                if (process.ExitCode!=0) throw new BuildFailedException("Metadata tool failed: " + output + error);
                if (output.Contains("AEGIS_")) Debug.Log(output.Trim());
            }
        }
    }

#if UNITY_ANDROID
    public sealed class GuardAndroidMetadata : UnityEditor.Android.IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 10000;
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            if (GuardBuild.Active == null || !GuardBuild.Active.settings.nativeMetadataProtection) return;
            GuardNativeProtection.Seal(Path.Combine(path,"src/main/assets/bin/Data/Managed/Metadata/global-metadata.dat"));
        }
    }
#endif
}
