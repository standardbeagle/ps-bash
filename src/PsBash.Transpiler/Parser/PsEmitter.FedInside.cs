using PsBash.Core.Parser.Ast;

namespace PsBash.Core.Parser;

public static partial class PsEmitter
{
    /// <summary>
    /// The command inside a block <see cref="EmitSimple"/> builds around it for descriptors it opens for this command only
    /// (<c>cat 7&lt; f</c>, <c>cat {fd}&lt; f</c>): fed from the enclosing stdin scope when it reads stdin
    /// (<see cref="EmitSimpleFedCore"/>, the one decision). The block is no pipe element, so EmitSimpleFed leaves it as it is
    /// (as it leaves an env prefix's save/restore, which feeds its command the same way), and a reader inside it took
    /// nothing. REGRESSION: <c>echo stdin-text | cat 7&lt; d</c> printed nothing (ExecFdTests); bash's cat reads the pipe and
    /// fd 7 is only open. Outside a stdin scope this is the plain emission. Guard: UnpipeableFedStageGuardTests.
    /// </summary>
    private static string HighFdCommandFed(Command.Simple inner) => EmitSimpleFedCore(inner);
}
