using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AegisGuard.Editor
{
    public sealed class ProtectionReport
    {
        public int renamedMethods, protectedStrings, protectedResources;
        public readonly List<string> mappings = new List<string>();
    }

    // Only explicitly annotated private methods are renamed. Type/field identities remain stable.
    public static class AssemblyProtector
    {
        private const string Rename = "AegisGuard.GuardRenameAttribute";
        private const string Strings = "AegisGuard.GuardStringsAttribute";
        private const string Keep = "AegisGuard.GuardKeepAttribute";
        private static bool Has(ICustomAttributeProvider item, string name) => item.CustomAttributes.Any(a => a.AttributeType.FullName == name);
        private static IEnumerable<TypeDefinition> Types(TypeDefinition root)
        {
            yield return root;
            foreach (var nested in root.NestedTypes) foreach (var type in Types(nested)) yield return type;
        }
        private static IEnumerable<TypeDefinition> Types(ModuleDefinition module) => module.Types.SelectMany(Types);

        public static ProtectionReport Protect(string[] paths, string[] selectedAssemblies, string[] resourceNames, byte[] key, string buildId)
        {
            var result = new ProtectionReport();
            var modules = new List<ModuleDefinition>();
            var resolver = new DefaultAssemblyResolver();
            var selected = new HashSet<string>(selectedAssemblies, StringComparer.Ordinal);
            if (selected.Contains("AegisGuard.Runtime")) throw new InvalidOperationException("Do not select AegisGuard.Runtime for user-code protection.");
            foreach (var path in paths) resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            try
            {
                // Read user modules + runtime together before resolving any method references.
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (!selected.Contains(name) && name != "AegisGuard.Runtime") continue;
                    var module = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadSymbols = false });
                    if (module.Assembly.Name.HasPublicKey) throw new InvalidOperationException("Signed assemblies require a signing integration: " + name);
                    if (module.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute" &&
                        a.ConstructorArguments.Count == 2 && (string)a.ConstructorArguments[0].Value == "AegisGuard.Build"))
                        throw new InvalidOperationException("Previously protected intermediate assembly detected. Perform a clean build.");
                    modules.Add(module);
                }
                foreach (var name in selected)
                    if (!modules.Any(m => m.Assembly.Name.Name == name)) throw new InvalidOperationException("Selected player assembly is absent: " + name);
                var runtime = modules.SingleOrDefault(m => m.Assembly.Name.Name == "AegisGuard.Runtime") ?? throw new InvalidOperationException("AegisGuard.Runtime is absent from player assemblies.");
                var decode = runtime.GetType("AegisGuard.ProtectedLiteral").Methods.Single(m => m.Name == "Decode");
                var userModules = modules.Where(m => selected.Contains(m.Assembly.Name.Name)).ToArray();
                var allLiterals = new HashSet<string>(userModules.SelectMany(Types).SelectMany(t => t.Methods)
                    .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand));
                var renamed = new Dictionary<MethodDefinition, string>();
                var originalNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var module in userModules)
                foreach (var type in Types(module))
                foreach (var method in type.Methods)
                {
                    if (Has(method, Keep) || Has(type, Keep)) continue;
                    if (Has(method, Rename))
                    {
                        if (!method.IsPrivate || method.IsVirtual || method.IsConstructor || method.IsSpecialName || method.IsPInvokeImpl ||
                            method.HasGenericParameters || type.HasGenericParameters || method.Overrides.Count != 0 || !method.HasBody ||
                            method.CustomAttributes.Any(a => a.AttributeType.FullName != Rename && a.AttributeType.FullName != Strings) ||
                            allLiterals.Contains(method.Name) || IsUnityMessage(method.Name))
                            throw new InvalidOperationException("Unsafe GuardRename target (callback/reflection/generic/attribute/visibility): " + method.FullName);
                        var newName = "m_" + Guid.NewGuid().ToString("N");
                        renamed.Add(method, newName);
                        originalNames.Add(module.Assembly.Name.Name + "|" + method.FullName, newName);
                        result.mappings.Add(module.Assembly.Name.Name + "\t" + method.FullName + "\t" + newName);
                        result.renamedMethods++;
                    }
                    if (Has(method, Strings))
                    {
                        if (!method.HasBody || method.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute" ||
                            a.AttributeType.FullName == "System.Runtime.CompilerServices.IteratorStateMachineAttribute"))
                            throw new InvalidOperationException("Place GuardStrings on a synchronous helper, not an async/iterator method: " + method.FullName);
                        ExpandBranches(method.Body);
                        var il = method.Body.GetILProcessor();
                        foreach (var instruction in method.Body.Instructions.ToArray())
                        {
                            if (instruction.OpCode != OpCodes.Ldstr || ((string)instruction.Operand).Length == 0) continue;
                            instruction.Operand = GuardCrypto.SealLiteral((string)instruction.Operand, key);
                            il.InsertAfter(instruction, il.Create(OpCodes.Call, module.ImportReference(decode)));
                            result.protectedStrings++;
                        }
                    }
                }

                // Capture references BEFORE mutating definitions. Private methods can be called by nested types.
                // References with constructed generic declaring types are intentionally unsupported targets.
                foreach (var module in modules)
                foreach (var type in Types(module))
                foreach (var method in type.Methods.Where(m => m.HasBody))
                foreach (var instruction in method.Body.Instructions)
                {
                    if (!(instruction.Operand is MethodReference reference) || reference is MethodDefinition) continue;
                    MethodReference element = reference is GenericInstanceMethod generic ? generic.ElementMethod : reference;
                    string assembly = element.DeclaringType.Scope is AssemblyNameReference ar ? ar.Name : module.Assembly.Name.Name;
                    if (originalNames.TryGetValue(assembly + "|" + element.FullName, out var name)) element.Name = name;
                }
                foreach (var pair in renamed) pair.Key.Name = pair.Value;

                foreach (string resource in resourceNames)
                {
                    int separator = resource.IndexOf('/');
                    if (separator <= 0) throw new InvalidOperationException("Embedded resource format: AssemblyName/Resource.Name");
                    string assembly = resource.Substring(0, separator), name = resource.Substring(separator + 1);
                    var module = userModules.SingleOrDefault(m => m.Assembly.Name.Name == assembly) ?? throw new InvalidOperationException("Resource assembly must be selected: " + assembly);
                    var embedded = module.Resources.OfType<EmbeddedResource>().SingleOrDefault(r => r.Name == name) ?? throw new InvalidOperationException("Embedded resource missing: " + resource);
                    using (var input = embedded.GetResourceStream())
                    using (var output = new MemoryStream())
                    {
                        GuardCrypto.Pack(input, output, key, "resource/" + resource);
                        int index = module.Resources.IndexOf(embedded);
                        module.Resources[index] = new EmbeddedResource(name, embedded.Attributes, output.ToArray());
                    }
                    result.protectedResources++;
                }

                PatchKey(runtime, key);
                foreach (var module in modules)
                {
                    var ctor = module.ImportReference(typeof(System.Reflection.AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) }));
                    var marker = new CustomAttribute(ctor);
                    marker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "AegisGuard.Build"));
                    marker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, buildId));
                    module.Assembly.CustomAttributes.Add(marker);
                }
                // Complete all transformations before writing any intermediate assembly.
                foreach (var module in modules)
                {
                    string output = module.FileName;
                    module.Write(output, new WriterParameters { WriteSymbols = false });
                    string pdb = Path.ChangeExtension(output, ".pdb");
                    if (File.Exists(pdb)) File.Delete(pdb);
                }
                return result;
            }
            finally { foreach (var module in modules) module.Dispose(); resolver.Dispose(); }
        }

        private static void PatchKey(ModuleDefinition runtime, byte[] key)
        {
            var type = runtime.GetType("AegisGuard.BuildKey");
            var getter = type.Methods.Single(m => m.Name == "GetCopy");
            var combine = type.Methods.Single(m => m.Name == "CombineBytes");
            var left = GuardCrypto.RandomBytes(32); var right = new byte[32];
            try
            {
                for (int i = 0; i < 32; i++) right[i] = (byte)(left[i] ^ key[i]);
                getter.Body = new MethodBody(getter) { MaxStackSize = 5 };
                var il = getter.Body.GetILProcessor();
                // Avoid two long-lived Base64 key shares in IL2CPP's string literal metadata.
                // Constants remain recoverable from native code; this is reduced residency, not key secrecy.
                EmitByteArray(il, runtime, left); EmitByteArray(il, runtime, right);
                il.Append(il.Create(OpCodes.Call, combine)); il.Append(il.Create(OpCodes.Ret));
            }
            finally { Array.Clear(left, 0, left.Length); Array.Clear(right, 0, right.Length); }
        }

        private static void EmitByteArray(ILProcessor il, ModuleDefinition module, byte[] bytes)
        {
            il.Append(il.Create(OpCodes.Ldc_I4, bytes.Length));
            il.Append(il.Create(OpCodes.Newarr, module.TypeSystem.Byte));
            for (int i = 0; i < bytes.Length; i++)
            {
                il.Append(il.Create(OpCodes.Dup)); il.Append(il.Create(OpCodes.Ldc_I4, i));
                il.Append(il.Create(OpCodes.Ldc_I4, (int)bytes[i])); il.Append(il.Create(OpCodes.Stelem_I1));
            }
        }

        private static bool IsUnityMessage(string name)
        {
            return new[] { "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "Reset", "OnEnable", "OnDisable", "OnDestroy", "OnValidate", "OnGUI", "OnBeforeSerialize", "OnAfterDeserialize" }.Contains(name) || name.StartsWith("On", StringComparison.Ordinal);
        }

        private static void ExpandBranches(MethodBody body)
        {
            foreach (var i in body.Instructions)
            {
                switch (i.OpCode.Code)
                {
                    case Code.Br_S: i.OpCode = OpCodes.Br; break;
                    case Code.Brfalse_S: i.OpCode = OpCodes.Brfalse; break;
                    case Code.Brtrue_S: i.OpCode = OpCodes.Brtrue; break;
                    case Code.Beq_S: i.OpCode = OpCodes.Beq; break;
                    case Code.Bge_S: i.OpCode = OpCodes.Bge; break;
                    case Code.Bgt_S: i.OpCode = OpCodes.Bgt; break;
                    case Code.Ble_S: i.OpCode = OpCodes.Ble; break;
                    case Code.Blt_S: i.OpCode = OpCodes.Blt; break;
                    case Code.Bne_Un_S: i.OpCode = OpCodes.Bne_Un; break;
                    case Code.Bge_Un_S: i.OpCode = OpCodes.Bge_Un; break;
                    case Code.Bgt_Un_S: i.OpCode = OpCodes.Bgt_Un; break;
                    case Code.Ble_Un_S: i.OpCode = OpCodes.Ble_Un; break;
                    case Code.Blt_Un_S: i.OpCode = OpCodes.Blt_Un; break;
                    case Code.Leave_S: i.OpCode = OpCodes.Leave; break;
                }
            }
        }
    }
}
