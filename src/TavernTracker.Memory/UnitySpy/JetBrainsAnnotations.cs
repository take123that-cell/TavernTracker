// Minimal stand-ins for the JetBrains.Annotations attributes UnitySpy uses (they only guide IDE analysis).
namespace JetBrains.Annotations
{
    using System;

    [AttributeUsage(AttributeTargets.All)]
    internal sealed class NotNullAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All)]
    internal sealed class PublicAPIAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All)]
    internal sealed class UsedImplicitlyAttribute : Attribute { }
}
