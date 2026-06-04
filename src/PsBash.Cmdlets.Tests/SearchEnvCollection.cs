using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Serializes the grep and rg cmdlet test classes. Both exercise recursive
/// directory pruning, and the <c>PSBASH_SEARCH_NO_IGNORE</c> config tests mutate
/// that process-global environment variable. xunit runs distinct test classes
/// (collections) in parallel by default, so without this an env mutation in one
/// class could corrupt a default-pruning assertion in the other (qa-rubric
/// Directive 11: environment leak). Sharing one collection makes their tests run
/// sequentially relative to each other.
/// </summary>
[CollectionDefinition("PsBashSearchEnv")]
public sealed class PsBashSearchEnvCollection { }
