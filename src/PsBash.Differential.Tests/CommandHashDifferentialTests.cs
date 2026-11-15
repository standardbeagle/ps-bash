using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// The <c>hash</c> builtin keeps bash's table of PATH lookups: running a program found through PATH remembers it
/// with a hit count, <c>hash NAME</c> adds it (0 hits), <c>-t</c> prints the remembered path, <c>-d</c>/<c>-r</c>
/// forget, <c>-p</c> sets a path. A builtin or function is accepted and never hashed. ps-bash printed
/// "hash table empty" whatever had run, and <c>hash -t</c> found nothing. Paths differ by platform, so the
/// listing is compared by its hit column and the path's tail.
/// </summary>
public class CommandHashDifferentialTests
{
    private const string Setup =
        "mkdir -p hb && printf '#!/bin/sh\\necho hi\\n' > hb/hashtool && chmod +x hb/hashtool\n"
        + "PATH=\"$PWD/hb:$PATH\"\n";

    [Fact]
    public Task Hash_RemembersPathLookupsWithHits() => AssertOracle.EqualAsync(Setup
        // Asking what an unhashed name is (type -P, command -v) adds nothing; hash -t and eval'd runs are hits.
        + "hash\ntype -P hashtool >/dev/null; command -v hashtool >/dev/null; hash\n"
        + "hashtool; hashtool\nhash | cut -f1\n"
        + "hash -t hashtool >/dev/null; eval hashtool\nhash | cut -f1\n"
        + "hash -t hashtool | grep -c 'hb.hashtool$'\n"
        + "hash -t hashtool nosuch 2>/dev/null | cut -f1; hash -t hashtool nosuch >/dev/null 2>&1; echo \"t=$?\"\n"
        + "hash -d hashtool; hash; hash -d hashtool 2>/dev/null; echo \"d=$?\"\n"
        + "hash hashtool; echo \"add=$?\"; hash | cut -f1\n"
        + "hash -r; hash; echo \"r=$?\"\n"
        + "hash -p some/where fake; hash -t fake\n",
        // hashtool is a #!/bin/sh script, run three times: on Windows each run starts an sh.
        timeout: TimeSpan.FromSeconds(60));

    [Fact]
    public Task Hash_BuiltinsFunctionsAndMisses() => AssertOracle.EqualAsync(
        "hash -r\nhash cd; echo \"cd=$?\"; myfn() { :; }; hash myfn; echo \"fn=$?\"; hash\n"
        + "hash nosuch_cmd_x 2>/dev/null; echo \"miss=$?\"\n"
        + "hash -t cd 2>/dev/null; echo \"tcd=$?\"\n"
        + "hash -t 2>/dev/null; echo \"tnone=$?\"\n"
        + "hash -z 2>/dev/null; echo \"bad=$?\"\n"
        // A name is data: `;` and `$( )` in it are never run.
        + "hash 'a;echo pwned' '$(echo pwned2)' 2>/dev/null; echo \"inj=$?\"\n");
}
