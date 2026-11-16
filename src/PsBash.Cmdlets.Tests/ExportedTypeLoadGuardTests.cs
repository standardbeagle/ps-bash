using System.Reflection;
using Xunit;

namespace PsBash.Cmdlets.Tests;

/// <summary>
/// Importing the module must not load PsBash.Transpiler. PowerShell lists the binary module's
/// exported types at import (<c>Assembly.GetExportedTypes</c>), and loading a type loads its base
/// type, its interfaces and the type of every value-type field. So an exported Cmdlets type may
/// name a Transpiler type in method signatures and bodies, but not in any of those three places.
/// The Pester case "Module in a fresh PowerShell session" observes the same rule end to end; this
/// guard names the offending member.
/// </summary>
public class ExportedTypeLoadGuardTests
{
    internal static List<string> Violations(Assembly cmdlets, Assembly transpiler)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<string>();
        foreach (var type in cmdlets.GetExportedTypes())
        {
            if (type.BaseType?.Assembly == transpiler)
                found.Add($"{type.FullName} derives from {type.BaseType.Name}");
            foreach (var iface in type.GetInterfaces())
                if (iface.Assembly == transpiler) found.Add($"{type.FullName} implements {iface.Name}");
            foreach (var field in type.GetFields(all))
                if (NeedsTranspilerAtLoad(field.FieldType, transpiler))
                    found.Add($"{type.FullName}.{field.Name} is a value-type field of {field.FieldType.Name}");
        }
        return found;
    }

    private static bool NeedsTranspilerAtLoad(Type fieldType, Assembly transpiler)
    {
        if (!fieldType.IsValueType) return false;
        if (fieldType.Assembly == transpiler) return true;
        if (!fieldType.IsGenericType) return false;
        foreach (var arg in fieldType.GetGenericArguments())
            if (NeedsTranspilerAtLoad(arg, transpiler)) return true;
        return false;
    }

    [Fact]
    public void ExportedCmdletTypes_DoNotLoadTheTranspilerAtImport()
    {
        var cmdlets = typeof(BashRuntime).Assembly;
        var transpiler = typeof(PsBash.Core.DevicePath).Assembly;
        Assert.NotEqual(cmdlets, transpiler);

        var found = Violations(cmdlets, transpiler);

        Assert.True(found.Count == 0,
            "Exported PsBash.Cmdlets types that load PsBash.Transpiler when the module is imported "
            + "(hold the value in a private nested class instead):\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void Violations_SeesAValueTypeFieldFromTheOtherAssembly()
    {
        // The guard's own types stand in for the two assemblies: Probe is exported from this test
        // assembly and holds a DeviceKind (an enum from PsBash.Transpiler) as a field.
        var found = Violations(typeof(Probe).Assembly, typeof(PsBash.Core.DevicePath).Assembly);

        Assert.Contains(found, v => v.Contains(nameof(Probe)) && v.Contains(nameof(Probe.Kind)));
    }

    public sealed class Probe
    {
        public PsBash.Core.DeviceKind Kind;
    }
}
