using System;
using System.Security.Cryptography;
using System.Text;

namespace AegisGuard
{
    [Serializable]
    public sealed class GuardCatalog
    {
        public string buildId;
        public GuardCatalogEntry[] entries = Array.Empty<GuardCatalogEntry>();
    }

    [Serializable]
    public sealed class GuardCatalogEntry
    {
        public string id;
        public string kind;
        public string[] dependencies = Array.Empty<string>();
    }

    public static class GuardNames
    {
        public const string CatalogId = "__aegis_catalog";
        public static string FileName(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Resource id is required.");
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", "").ToLowerInvariant() + ".agc";
        }
    }
}
