using System;
using System.IO;
using System.Linq;
using AegisGuard;
using AegisGuard.Editor;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

public static class AegisWeaverChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Weaver check: " + message); }
    public static void Run()
    {
        string root = Path.GetFullPath("Library/AegisWeaverChecks/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] key = GuardCrypto.RandomBytes(32);
        try
        {
            string runtime = Path.Combine(root, "AegisGuard.Runtime.dll"), fixture = Path.Combine(root, "GuardWeaverFixture.dll");
            File.Copy(typeof(BuildKey).Assembly.Location, runtime, true);
            CreateFixture(fixture, "Secret", false, false, false);
            var result = AssemblyProtector.Protect(new[] { runtime, fixture }, new[] { "GuardWeaverFixture" }, new[] { "GuardWeaverFixture/Fixture.Bytes" }, key, "test");
            Check(result.renamedMethods == 1 && result.protectedStrings == 1 && result.protectedResources == 1, "coverage counts");
            using (var module = ModuleDefinition.ReadModule(runtime))
            {
                var getter = module.GetType("AegisGuard.BuildKey").Methods.Single(m => m.Name == "GetCopy");
                var instructions = getter.Body.Instructions;
                Check(!instructions.Any(i => i.OpCode == OpCodes.Ldstr), "no key shares in string literal metadata");
                Check(instructions.Count(i => i.OpCode == OpCodes.Newarr) == 2 && instructions.Any(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "CombineBytes"), "binary share ownership helper used");
                var shares = new byte[2][]; int position = 0;
                for (int part = 0; part < 2; part++)
                {
                    int length = (int)instructions[position++].Operand; position++; shares[part] = new byte[length];
                    for (int i = 0; i < length; i++)
                    {
                        position++; int index = (int)instructions[position++].Operand;
                        shares[part][index] = (byte)(int)instructions[position++].Operand; position++;
                    }
                }
                Check(BuildKey.CombineBytes(shares[0], shares[1]).SequenceEqual(key), "emitted binary constants reconstruct intended build key");
                Array.Clear(shares[0], 0, shares[0].Length);
            }
            using (var module = ModuleDefinition.ReadModule(fixture))
            {
                var type = module.GetType("Fixture");
                var secret = type.Methods.Single(m => m.IsPrivate);
                Check(secret.Name.StartsWith("m_") && secret.Name != "Secret", "method renamed");
                var call = (MethodReference)type.Methods.Single(m => m.Name == "Caller").Body.Instructions.First().Operand;
                Check(call.Name == secret.Name, "MemberRef rewritten");
                string payload = (string)secret.Body.Instructions.First().Operand;
                Check(GuardCrypto.OpenLiteral(payload, key) == "secret fixture literal", "literal envelope");
                Check(secret.Body.Instructions[1].OpCode == OpCodes.Call, "decode injected");
                using (var stream = new ProtectedReadStream(((EmbeddedResource)module.Resources[0]).GetResourceStream(), key, "resource/GuardWeaverFixture/Fixture.Bytes"))
                    Check(stream.ReadByte() == 17 && stream.ReadByte() == 29 && stream.ReadByte() == -1, "embedded resource envelope");
            }
            bool rejected = false;
            try { AssemblyProtector.Protect(new[] { runtime, fixture }, new[] { "GuardWeaverFixture" }, Array.Empty<string>(), key, "twice"); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "double transformation rejected");

            foreach (var scenario in new[] { "public", "callback", "reflection" })
            {
                File.Copy(typeof(BuildKey).Assembly.Location, runtime, true);
                CreateFixture(fixture, scenario == "callback" ? "OnCollisionEnter" : "Secret", scenario == "public", scenario == "reflection", false);
                byte[] original = File.ReadAllBytes(fixture);
                rejected = false;
                try { AssemblyProtector.Protect(new[] { runtime, fixture }, new[] { "GuardWeaverFixture" }, Array.Empty<string>(), key, "bad"); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && original.SequenceEqual(File.ReadAllBytes(fixture)), "unsafe target rejected without writing: " + scenario);
            }
            File.Copy(typeof(BuildKey).Assembly.Location, runtime, true);
            CreateFixture(fixture, "Secret", false, false, true);
            result = AssemblyProtector.Protect(new[] { runtime, fixture }, new[] { "GuardWeaverFixture" }, Array.Empty<string>(), key, "keep");
            Check(result.renamedMethods == 0 && result.protectedStrings == 0, "GuardKeep exclusion");
            Debug.Log("AEGIS_WEAVER_CHECKS_PASS (14 assertions)");
        }
        finally
        {
            Array.Clear(key, 0, key.Length);
            string parent = Path.GetFullPath("Library/AegisWeaverChecks") + Path.DirectorySeparatorChar;
            if (root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static void CreateFixture(string path, string name, bool isPublic, bool reflectionLiteral, bool keep)
    {
        using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("GuardWeaverFixture", new Version(1, 0)), "GuardWeaverFixture", ModuleKind.Dll))
        {
            var module = assembly.MainModule;
            var type = new TypeDefinition("", "Fixture", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(type);
            var secret = new MethodDefinition(name, (isPublic ? MethodAttributes.Public : MethodAttributes.Private) | MethodAttributes.Static, module.TypeSystem.String);
            type.Methods.Add(secret);
            secret.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(GuardRenameAttribute).GetConstructor(Type.EmptyTypes))));
            secret.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(GuardStringsAttribute).GetConstructor(Type.EmptyTypes))));
            if (keep) secret.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(GuardKeepAttribute).GetConstructor(Type.EmptyTypes))));
            var il = secret.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldstr, reflectionLiteral ? name : "secret fixture literal")); il.Append(il.Create(OpCodes.Ret));
            var caller = new MethodDefinition("Caller", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.String);
            type.Methods.Add(caller);
            il = caller.Body.GetILProcessor();
            var reference = new MethodReference(name, module.TypeSystem.String, new TypeReference("", "Fixture", module, module)) { HasThis = false };
            il.Append(il.Create(OpCodes.Call, reference)); il.Append(il.Create(OpCodes.Ret));
            module.Resources.Add(new EmbeddedResource("Fixture.Bytes", ManifestResourceAttributes.Private, new byte[] { 17, 29 }));
            assembly.Write(path);
        }
    }
}
