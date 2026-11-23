using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace PsBash.Testing;

/// <summary>
/// Reads a method's IL for the IL-walking guards (<see cref="SkipUsageGuard"/>, <see cref="ProcessEnvironmentGuard"/>):
/// the methods it calls, constructs or takes a delegate to, and the string literals it loads.
/// </summary>
internal static class IlScan
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

    /// <summary>
    /// Every method the IL of <paramref name="method"/> calls, constructs, or takes a delegate to, plus the
    /// MoveNext of its async/iterator state machine.
    /// </summary>
    public static IEnumerable<MethodBase> Callees(MethodBase method)
    {
        foreach (var attr in method.GetCustomAttributes<StateMachineAttribute>(inherit: false))
        {
            var moveNext = attr.StateMachineType.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext is not null) yield return moveNext;
        }

        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
        Type[]? methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;
        foreach (var token in Operands(method, OperandType.InlineMethod))
        {
            var callee = Resolve(method.Module, token, typeArgs, methodArgs);
            if (callee is not null) yield return callee;
        }
    }

    /// <summary>Every string literal (<c>ldstr</c>) the IL of <paramref name="method"/> loads.</summary>
    public static IEnumerable<string> Strings(MethodBase method)
    {
        foreach (var token in Operands(method, OperandType.InlineString))
        {
            string? s;
            try { s = method.Module.ResolveString(token); }
            catch (Exception) { s = null; }
            if (s is not null) yield return s;
        }
    }

    // The metadata tokens of every instruction whose operand is of `kind`.
    private static IEnumerable<int> Operands(MethodBase method, OperandType kind)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception) { il = null; }
        if (il is null) yield break;

        var pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE && pos < il.Length) value = (short)(0xFE00 | il[pos++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break; // unknown opcode: stop, don't guess

            if (op.OperandType == kind) yield return BitConverter.ToInt32(il, pos);
            pos += OperandSize(op.OperandType, il, pos);
        }
    }

    private static MethodBase? Resolve(Module module, int token, Type[]? typeArgs, Type[]? methodArgs)
    {
        try { return module.ResolveMethod(token, typeArgs, methodArgs); }
        catch (Exception)
        {
            try { return module.ResolveMethod(token); }
            catch (Exception) { return null; }
        }
    }

    private static int OperandSize(OperandType type, byte[] il, int pos) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
        _ => 4,
    };

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var table = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is OpCode op) table[op.Value] = op;
        return table;
    }
}
