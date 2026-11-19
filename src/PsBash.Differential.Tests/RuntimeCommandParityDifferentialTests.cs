using PsBash.Differential.Tests.Oracle;
using Xunit;

namespace PsBash.Differential.Tests;

/// <summary>
/// Runtime-command bugs found while documenting v0.15.0, each against the GNU coreutils 9.4 / bash 5.2 oracle
/// (WSL Ubuntu-24.04). One case per bug; the class and its primitive are named on each case. Mutating cases use
/// <see cref="FsStateOracle"/> (stdout + status + the resulting tree).
/// </summary>
public class RuntimeCommandParityDifferentialTests
{
    private static Task Eq(string script) => AssertOracle.EqualAsync(script, timeout: TimeSpan.FromSeconds(30));

    // printf — class: a conversion with no argument was skipped (width lost). Primitive: Arg/Conv accessors
    // (a missing argument is the empty argument).
    [SkippableFact]
    public Task Printf_MissingArguments_AreEmpty()
        => Eq("printf '[%5s][%3d][%.2f][%e][%g][%x][%-4s|][%q][%b]\\n'; printf '[%s][%s]\\n' a b c; printf '%s=%d\\n' x");

    // tr — class: the stage order (delete/translate, then squeeze) and -t's target. Primitive: TransformLine's
    // two stages over one squeeze set.
    [SkippableFact]
    public Task Tr_TruncateDeleteSqueezeComplement()
        => Eq("echo abcdef | tr -t abc xy; echo aabbbcc | tr -ds a b; echo 'aa  bb' | tr -cs a x; "
            + "echo hello123 | tr -c '[:alpha:]\\n' _; echo aabbcc | tr -s ab xy; echo abc | tr -d -s b c");

    // tee — class: a status set before the pipeline drains is reset by the upstream stage. Primitive: a failure
    // latch re-asserted in EndProcessing (guard UpstreamStatusResetGuardTests).
    [SkippableFact]
    public Task Tee_UnopenableFile_Exits1()
        => Eq("echo x | tee /nonexistent-dir/f; echo rc=$?; mkdir -p d; echo y | tee d; echo rc=$?");

    // dirname / basename / … — class: "missing operand" spelled per command (or not at all). Primitive:
    // FileSystemHelpers.WriteMissingOperand / MissingOperandMessage (guard MissingOperandGuardTests).
    [SkippableFact]
    public Task MissingOperand_UsageErrorEverywhere()
        => Eq("for c in dirname basename mkdir rmdir rm readlink realpath sleep stat chmod comm join; do $c; echo \"$c rc=$?\"; done; "
            + "chmod 755; echo rc=$?; comm a; echo rc=$?; nohup; echo rc=$?; expr; echo rc=$?");

    // comm / base64 / uniq / split / basename / date / cp / sort / wc / diff — class: "extra operand" spelled per
    // command (ASCII quotes where GNU uses the locale's, no hint, basename without the operand). Primitive:
    // FileSystemHelpers.ExtraOperandMessage / WriteExtraOperand, quote style per tool (guard ExtraOperandGuardTests).
    [SkippableFact]
    public Task ExtraOperand_OneSpellingPerTool()
        => Eq("cd \"$(mktemp -d)\"; echo a > a; echo b > b; mkdir d; "
            + "comm a b c 2>&1; echo rc=$?; base64 a b 2>&1; echo rc=$?; uniq a b c 2>&1; echo rc=$?; split a x y 2>&1; echo rc=$?; "
            + "basename a b c 2>&1; echo rc=$?; date +%Y extra 2>&1; echo rc=$?; seq 1 2 3 4 2>&1; echo rc=$?; "
            + "cp -T a b c 2>&1; echo rc=$?; mv -T a b c 2>&1; echo rc=$?; ln -T a b c 2>&1; echo rc=$?; join a b c 2>&1; echo rc=$?; "
            + "sort -c a b 2>&1; echo rc=$?; diff a b c 2>&1; echo rc=$?");

    [SkippableFact]
    public Task Dirname_GnuMdirName()
        => Eq("dirname a//b ''; dirname // /usr/bin/ sort; dirname -- a/b");

    // nl — class: an unnumbered line's prefix. Primitive: one blank prefix (width + separator).
    [SkippableFact]
    public Task Nl_UnnumberedLines_PadWidthAndSeparator()
        => Eq("printf 'a\\n\\nb\\n' | nl | od -c; printf 'a\\nb\\n' | nl -b n -s: | od -c; printf 'x\\n\\n' | nl -w3 | od -c");

    // tac -s — class: records joined/split on newlines instead of separator-terminated. Primitive:
    // InvokeBashTacCommand.ReverseRecords (exact bytes, per source).
    [SkippableFact]
    public Task Tac_Separator_RecordsEndWithIt()
        => Eq("printf 'a,b,c' | tac -s ,; echo; printf 'a,b,c,' | tac -s ,; echo; printf '1xy2xy3' | tac -s xy; echo; "
            + "printf 'a,b' > f1; printf 'c,d,' > f2; tac -s , f1 f2; echo; printf 'a\\nb\\n' | tac -s ''; printf 'a,b\\n' | tac -s ,; echo '[end]'");

    // shuf — class: a read error swallowed. Primitive: FileSystemHelpers.ReadErrorMessage + status 1.
    [SkippableFact]
    public Task Shuf_MissingFile_ErrorsAndExits1()
        => Eq("shuf nosuchfile; echo rc=$?");

    // mkdir -pv / install -v / rmdir -pv — class: verbose lines and the -p walk derived from resolved paths
    // instead of the operand text. Primitive: OperandChain (guard OperandChainTests).
    [SkippableFact]
    public Task Mkdir_ParentsVerbose_NamesEachCreatedPrefix()
        => FsStateOracle.EqualAsync("mkdir -p a", "mkdir -pv a/b/c; mkdir -pv ./a/x//y/; mkdir -pv a/b/c/d", compareStderr: true);

    [SkippableFact]
    public Task Rmdir_Parents_StopsAtTheOperandsOwnComponents()
        => FsStateOracle.EqualAsync("mkdir -p outer/a/b", "cd outer && rmdir -p a/b; echo rc=$?", compareStderr: true);

    [SkippableFact]
    public Task Rmdir_ParentsVerbose_NamesBeforeEachAttemptAndFailsOnNonEmpty()
        => FsStateOracle.EqualAsync("mkdir -p a/x/y a/b; mkdir -p z/w",
            "rmdir -pv a/x//y/; echo rc=$?; rmdir -pv ./z/w; echo rc=$?", compareStderr: true);

    // env / printenv — class: a command's identity read from InvocationName (`&` under `env … printenv`), and env's
    // own option scan. Primitive: one cmdlet per identity (guard InvocationIdentityGuardTests); GNU's getopt surface
    // with 125 / 126 / 127.
    [SkippableFact]
    public Task Env_RunsCommandWithModifiedEnvironment()
        => Eq("export PE_U=set; env PE_A=1 PE_B=2 printenv PE_A PE_B; env -u PE_U printenv PE_U; echo rc=$?; "
            + "env -uPE_U printenv PE_U; echo rc=$?; env --unset=PE_U printenv PE_U; echo rc=$?; env --unset PE_U printenv PE_U; echo rc=$?; "
            + "env -i PE_I=1 printenv PE_I PE_U; echo rc=$?; env - PE_Z=3 printenv PE_Z; env -iu PE_X PE_Y=2 printenv PE_Y; "
            + "env -- printenv PE_U; env 1=x printenv 1; env -i; echo rc=$?; env -i PE_P=1 PE_Q=2; env PE_U=over sh -c 'echo $PE_U'");

    [SkippableFact]
    public Task Env_FailureStatuses_125_126_127()
        => Eq("env nosuchcmd; echo rc=$?; mkdir -p dd; env ./dd; echo rc=$?; env ./nosuch; echo rc=$?; env -Q; echo rc=$?; "
            + "env -u; echo rc=$?; env --bogus; echo rc=$?; printenv -Q; echo rc=$?; printenv --bogus; echo rc=$?; "
            + "printenv PE_NOPE; echo rc=$?; env false; echo rc=$?; env true; echo rc=$?");

    // xargs / timeout running `true` / `false` — class: a builtin-only name run as a command by another
    // command. Primitive: Invoke-BashTrue / Invoke-BashFalse behind the true / false aliases.
    [SkippableFact]
    public Task TrueFalse_RunByAnotherCommand()
        => Eq("echo a | xargs false; echo rc=$?; echo a | xargs true; echo rc=$?; timeout 5 false; echo rc=$?");

    // gzip — class: GNU's status ladder (1 error, 2 warning unless an error) and names shown as resolved Windows
    // paths. Primitive: InvokeBashGzipCommand.Error / Warn, the operand-as-typed NAME per member. Members are fixed
    // GNU bytes (`gzip -k a` with mtime 0, `gzip -n`) so sizes and ratios do not depend on the compressor.
    private const string GzNamed = "printf '\\x1f\\x8b\\x08\\x08\\x00\\x00\\x00\\x00\\x00\\x03\\x61\\x00\\xcb\\x48\\xcd\\xc9\\xc9\\xe7\\x02\\x00\\x20\\x30\\x3a\\x36\\x06\\x00\\x00\\x00'";

    [SkippableFact]
    public Task Gzip_ListAndVerboseDecompress_GnuFormatAndNames()
        => FsStateOracle.EqualAsync("mkdir -p r",
            GzNamed + " > n.gz; " + GzNamed + " > r/m.gz; gzip -l n.gz; gzip -l n.gz r/m.gz; gzip -l n.gz nosuch; echo rc=$?; "
            + "gzip -tv n.gz; gzip -dv n.gz; gzip -drv r; echo rc=$?; cat n r/m", compareStderr: true);

    [SkippableFact]
    public Task Gzip_WarningsAndErrors_StatusLadder()
        => FsStateOracle.EqualAsync("mkdir d; echo x > b.txt; echo y > c; echo z > e",
            "gzip d; echo rc=$?; gzip -q d; echo rc=$?; gzip -d b.txt; echo rc=$?; gzip -dq b.txt; echo rc=$?; "
            + GzNamed + " > a.gz; gzip a.gz; echo rc=$?; " + GzNamed + " > c.gz; gzip -d c.gz; echo rc=$?; gzip -dq c.gz; echo rc=$?; "
            + ": > z.gz; gzip -d z.gz; echo rc=$?; gzip -t z.gz; echo rc=$?; gzip -d nosuch; echo rc=$?; gzip -d d nosuch; echo rc=$?; "
            + GzNamed + " > e.gz; gunzip e; echo rc=$?; " + GzNamed + " > f.gz; zcat f; echo rc=$?; rm -f *.gz", compareStderr: true);

    // mktemp — class: the target directory and name built from a fixed place instead of GNU's rules ($TMPDIR, -t,
    // -p, a template relative to the cwd, the X run replaced). Primitive: InvokeBashMktempCommand.Plan. Random
    // characters are reduced to their count.
    [SkippableFact]
    public Task Mktemp_TmpdirTemplateAndDirectory()
        => Eq("mkdir -p mk/t mk/p && cd mk; s() { f=$1; echo \"${f%%.*}.${#f} $(test -f \"$f\" && echo f)$(test -d \"$f\" && echo d)\"; }; "
            + "s \"$(TMPDIR=t mktemp)\"; s \"$(TMPDIR=t mktemp -d)\"; s \"$(TMPDIR=t mktemp ab.XXXX)\"; s \"$(TMPDIR=t mktemp -t ab.XXXX)\"; "
            + "s \"$(TMPDIR=t mktemp -p p ab.XXXX)\"; s \"$(TMPDIR=t mktemp -t -p p ab.XXXX)\"; s \"$(mktemp p/q.XXX.txt)\"; "
            + "f=$(mktemp --suffix=.s p/zXXX); echo \"${f:0:3}.${#f}.${f##*.}\"; mktemp abc; echo rc=$?; mktemp -t a/bXXX; echo rc=$?; "
            + "mktemp -d nosuch/aXXX; echo rc=$?; cd .. && rm -rf mk");

    [SkippableFact]
    public Task Install_Verbose_NamesOperandsAsTyped()
        => FsStateOracle.EqualAsync("echo s > s; mkdir -p ip",
            "install -v s s2; install -v -d id1/id2 id3; install -v -D s dd/ee/ff; install -v s id3; install -v -t ip s");

    // ---- usage-error batch (each a REGRESSION: the wrong case printed output, or nothing, at status 0) ----

    // seq / touch / install — class: a usage error (missing / extra operand) without GNU's wording or hint line.
    // Primitive: FileSystemHelpers.WriteMissingOperand / WriteMissingFileOperand / WriteExtraOperand.
    [SkippableFact]
    public Task Seq_Touch_Install_OperandCountErrors()
        => Eq("seq; echo rc=$?; seq 1 2 3 4; echo rc=$?; touch; echo rc=$?; install; echo rc=$?; install a; echo rc=$?");

    // tr — class: the SET count was never checked, so `tr a` copied its input. Primitive: OperandBounds (GNU
    // validate()), shared by the cmdlet and the fused TrStage, reported via WriteMissingOperand(why:) /
    // WriteExtraOperand.
    [SkippableFact]
    public Task Tr_SetCount_IsValidated()
        => Eq("tr; echo rc=$?; echo ab | tr a; echo rc=$?; echo ab | tr -ds a; echo rc=$?; echo ab | tr -d a b; echo rc=$?; "
            + "echo ab | tr a b c; echo rc=$?; echo ab | tr -s a b c; echo rc=$?; echo aab | tr -s a; echo ab | tr -d a; echo ab | tr -s a b");

    // shuf / uname / id / ping — class: a hand argv scan (options after -e were lines, -un a user name, unknown
    // options ignored, -o / -W / -p / -i crashing the binder). Primitive: ArgParser + PsEmitter.OrderedArgCommands.
    [SkippableFact]
    public Task Shuf_OptionsAnywhere_RepeatOutputAndErrors()
        => Eq("cd \"$(mktemp -d)\"; shuf -r -n 2 -e a; shuf -n 2 -o out -e x y; sort out; shuf -e a -i 1-2; echo rc=$?; "
            + "shuf a b; echo rc=$?; shuf -i 5-1; echo rc=$?; shuf -i 3-3; shuf -i 2-1; echo rc=$?; shuf -n x -e a; echo rc=$?; "
            + "shuf -r -e a | head -3; shuf -n 1 -n 3 -e a b c d | wc -l; shuf -r -n 2 < /dev/null; echo rc=$?; shuf -i 1-3 extra; echo rc=$?");

    // The id usage errors keep only their status here: off Windows ps-bash runs the host's id, whose wording is the
    // implementation's (uutils on Ubuntu 25.10, BSD on macOS), not the recorded GNU text. InvokeBashIdCommandTests
    // pins the wording against the host's own id. Short options only: BSD id (macOS) has no --group/--name.
    [SkippableFact]
    public Task Uname_Id_Ping_ParseLikeGnu()
        => Eq("uname -x; echo rc=$?; uname foo; echo rc=$?; for o in -o -p -i -v '-s -o' --operating-system; do uname $o > /dev/null; echo \"$o rc=$?\"; done; "
            + "[ \"$(id -un)\" = \"$(id -u -n)\" ] && echo same; [ \"$(id -gn)\" = \"$(id -g -n)\" ] && echo same; "
            + "id -n 2>/dev/null; echo rc=$?; id -ug 2>/dev/null; echo rc=$?; id -x 2>/dev/null; echo rc=$?; "
            + "ping -W 1 -c x 127.0.0.1; echo rc=$?; ping; echo rc=$?; ping -z > /dev/null 2>&1; echo rc=$?");

    // tee -i / -p — class: GNU options refused as unsupported. Now accepted (FsState: the file is written).
    [SkippableFact]
    public Task Tee_IgnoreInterruptsAndOutputError_AreAccepted()
        => FsStateOracle.EqualAsync("true",
            "echo x | tee -i -p f; echo rc=$?; echo y | tee --output-error=warn g; echo rc=$?; echo z | tee --output-error=bogus h; echo rc=$?",
            compareStderr: true);

    // mkdir — class: the parent of a trailing-slash operand was the operand itself. Primitive:
    // FileSystemHelpers.ParentDirectory.
    [SkippableFact]
    public Task Mkdir_TrailingSlash_CreatesTheDirectory()
        => FsStateOracle.EqualAsync("true",
            "mkdir two/; echo rc=$?; mkdir -v three//; mkdir -pv four/five/; mkdir two/; echo rc=$?; mkdir no/such/; echo rc=$?",
            compareStderr: true);
}
