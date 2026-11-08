using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>`$_` is the previous command's last argument (the `mkdir -p x && cd $_` idiom).</summary>
public class LastArgInListTests
{
    [Fact] public Task MkdirThenCd() => AssertOracle.EqualAsync("cd \"$(mktemp -d)\"; mkdir -p a/b && cd $_ && pwd | sed 's|.*/a/b|OK a/b|'");
    [Fact] public Task QuotedAndSemicolon() => AssertOracle.EqualAsync("cd \"$(mktemp -d)\"; mkdir -p d; cd \"$_\"; pwd | sed 's|.*/d$|OK d|'");
    [Fact] public Task AfterEcho() => AssertOracle.EqualAsync("echo one two three; echo \"last=$_\"");
    [Fact] public Task BracedAndEmbedded() => AssertOracle.EqualAsync("_var=value; : 42; echo $_ $_var ${_}var; : 'foo'\"bar\"; echo $_");
}
