using System;

namespace AegisGuard
{
    /// <summary>Opt in a private, directly called method. Do not use for callbacks or reflection.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class GuardRenameAttribute : Attribute { }

    /// <summary>Encrypt ldstr operands in this method at player build time.</summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
    public sealed class GuardStringsAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class GuardKeepAttribute : Attribute { }
}
