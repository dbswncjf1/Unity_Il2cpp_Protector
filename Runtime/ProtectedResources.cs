using System;
using System.IO;
using System.Reflection;

namespace AegisGuard
{
    public static class ProtectedResources
    {
        public static ProtectedReadStream Open(Assembly assembly, string name)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            var input = assembly.GetManifestResourceStream(name);
            if (input == null) throw new FileNotFoundException("Embedded resource was not found.", name);
            try { return new ProtectedReadStream(input, BuildKey.GetCopy, "resource/" + assembly.GetName().Name + "/" + name); }
            catch { input.Dispose(); throw; }
        }
    }
}
