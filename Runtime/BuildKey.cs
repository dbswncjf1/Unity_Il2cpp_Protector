using System;
using System.Runtime.CompilerServices;
using UnityEngine.Scripting;

namespace AegisGuard
{
    // Split constants are obfuscation, NOT a hardware-backed secret.
    // The build callback replaces GetCopy in intermediate player assemblies only.
    [Preserve]
    public static class BuildKey
    {
        [Preserve, MethodImpl(MethodImplOptions.NoInlining)]
        public static byte[] GetCopy() => throw new InvalidOperationException("Use an AegisGuard player build; no key is installed in the Editor.");

        [Preserve]
        public static byte[] Combine(string left, string right)
        {
            byte[] a = null, b = null;
            bool returned = false;
            try
            {
                a = Convert.FromBase64String(left); b = Convert.FromBase64String(right);
                var result = CombineBytes(a, b); returned = true; return result;
            }
            finally
            {
                if (b != null) Array.Clear(b, 0, b.Length);
                if (!returned && a != null) Array.Clear(a, 0, a.Length);
            }
        }

        // Takes ownership of both fresh share arrays. Caller owns the returned key and must clear it.
        // Native constants can still be recovered by code analysis/hooking; this avoids literal-table shares.
        [Preserve]
        public static byte[] CombineBytes(byte[] left, byte[] right)
        {
            bool returned = false;
            try
            {
                if (left == null || right == null || left.Length != 32 || right.Length != 32 || ReferenceEquals(left, right))
                    throw new InvalidOperationException("Invalid build key shares.");
                for (int i = 0; i < left.Length; i++) left[i] ^= right[i];
                returned = true; return left;
            }
            finally
            {
                if (right != null) Array.Clear(right, 0, right.Length);
                if (!returned && left != null) Array.Clear(left, 0, left.Length);
            }
        }
    }

    [Preserve]
    public static class ProtectedLiteral
    {
        [Preserve]
        public static string Decode(string envelope)
        {
            var key = BuildKey.GetCopy();
            try { return GuardCrypto.OpenLiteral(envelope, key); }
            finally { Array.Clear(key, 0, key.Length); }
        }
    }
}
