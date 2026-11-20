using System.IO;
using System.Linq;
using Mono.Cecil;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

// TEST ONLY: supplies an EmbeddedResource in Assembly-CSharp before the real protection callback.
// A global csc.rsp would embed duplicate plaintext into unrelated assemblies and invalidate the test.
public sealed class ResourceFixtureInjector : IPostBuildPlayerScriptDLLs
{
    public int callbackOrder => -10000;
    public void OnPostBuildPlayerScriptDLLs(BuildReport report)
    {
        string path = report.GetFiles().Select(f => f.path).Single(p => Path.GetFileName(p) == "Assembly-CSharp.dll");
        using (var module = ModuleDefinition.ReadModule(path, new ReaderParameters { InMemory = true }))
        {
            module.Resources.Add(new EmbeddedResource("Aegis.Test.Data", ManifestResourceAttributes.Private, File.ReadAllBytes("GuardSource/embedded.txt")));
            module.Write(path);
        }
    }
}
