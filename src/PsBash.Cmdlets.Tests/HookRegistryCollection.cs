using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// xUnit collection for every test class that registers a hook in, or fires, the process-global
/// <see cref="HookRegistry.Instance"/>. A prompt tick with a changed directory runs EVERY registered chpwd hook, each in
/// the runspace that registered it, so a class firing a tick beside another class's registered hook ran that hook too:
/// <c>ChpwdHook_DoesNotFire_WhenPathUnchanged</c> saw its flag set by a parallel class's <c>cd</c> (Windows fleet s23e).
/// Unique hook names keep registrations apart; only serializing the classes keeps the firings apart. Membership is
/// pinned by <see cref="HookRegistryCollectionGuardTests"/>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HookRegistryCollection
{
    public const string Name = "hook-registry";
}
