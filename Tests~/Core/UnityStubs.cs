// Only for the standalone .NET tests; Unity supplies the real attribute in player builds.
namespace UnityEngine.Scripting
{
    [System.AttributeUsage(System.AttributeTargets.All)]
    public sealed class PreserveAttribute : System.Attribute { }
}
