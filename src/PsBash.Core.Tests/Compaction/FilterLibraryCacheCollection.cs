using Xunit;

namespace PsBash.Core.Tests.Compaction;

/// <summary>
/// Serializes every test class that reaches <c>FilterLibrary.Load</c>.
///
/// <para>
/// <c>FilterLibrary</c> keeps a SINGLE-entry static cache (<c>_cacheKey</c> /
/// <c>_cache</c>) — correct for the product, where a process has one filter directory,
/// but shared mutable state as far as the test assembly is concerned. xUnit runs test
/// CLASSES in parallel, so a <c>Load</c> from another class (notably
/// <c>BuiltinFiltersTests</c>, which calls it from a static field initializer) evicts
/// the entry between two adjacent <c>Load</c> calls here, and
/// <c>Load_CachesUntilFileMtimeChanges</c>'s <c>Assert.Same</c> fails.
/// </para>
///
/// <para>
/// That surfaced as a ~1-in-5 flake with a confusing diff: the two lists have EQUAL
/// content, so the failure reads as a data mismatch when it is really an identity one.
/// Sharing a collection is the xUnit-sanctioned way to say "these classes touch the same
/// global"; it is preferable to weakening the assertion to <c>Assert.Equal</c>, which
/// would stop testing the caching behavior the test exists for.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FilterLibraryCacheCollection
{
    public const string Name = "FilterLibrary cache (shared static state)";
}
