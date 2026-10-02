using System.Management.Automation;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PsBash.Host.Runtime;

/// <summary>
/// Finds the <c>[Cmdlet]</c>-attributed types of an assembly WITHOUT loading every type in it.
/// <para>
/// <c>Assembly.GetTypes()</c> materialises every type, and creating a type resolves its base class
/// and interfaces. PsBash.Cmdlets contains Strata-derived helper types (styled-output nodes, the
/// interactive session), so a plain <c>GetTypes()</c> at host startup dragged
/// <c>Strata.Abstractions</c> / <c>Strata.Interaction</c> (and their dependency closure) into every
/// runspace, even for <c>echo hi</c>. Reading the PE metadata instead names the cmdlet types first;
/// only those are then loaded, and a cmdlet class's own load does not touch the Strata types it uses
/// in method bodies and fields. Strata assemblies now load when a Strata-backed cmdlet actually runs.
/// </para>
/// </summary>
internal static class CmdletTypeScanner
{
    /// <summary>Cmdlet types of <paramref name="assembly"/>, each with its <see cref="CmdletAttribute"/>.</summary>
    public static IReadOnlyList<(Type Type, CmdletAttribute Attribute)> FindCmdletTypes(Assembly assembly)
    {
#pragma warning disable IL3000 // empty in a single-file bundle: TryReadCmdletTypeNames then returns null and FromAllTypes is used
        var names = TryReadCmdletTypeNames(assembly.Location);
#pragma warning restore IL3000
        return names is null
            ? FromAllTypes(assembly)
            : FromNames(assembly, names);
    }

    private static List<(Type, CmdletAttribute)> FromNames(Assembly assembly, List<string> names)
    {
        var result = new List<(Type, CmdletAttribute)>(names.Count);
        foreach (var name in names)
        {
            Type? type;
            try { type = assembly.GetType(name, throwOnError: false); }
            catch { continue; }
            if (Accept(type) is { } attr) result.Add((type!, attr));
        }
        return result;
    }

    /// <summary>
    /// Fallback (no readable file, unexpected metadata): the original eager scan. Internal so the
    /// regression test can prove its harness really detects the Strata load this class avoids.
    /// </summary>
    internal static List<(Type, CmdletAttribute)> FromAllTypes(Assembly assembly)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        var result = new List<(Type, CmdletAttribute)>();
        foreach (var type in types)
            if (Accept(type) is { } attr) result.Add((type, attr));
        return result;
    }

    private static CmdletAttribute? Accept(Type? type)
    {
        if (type is null || type.IsAbstract) return null;
        if (!typeof(Cmdlet).IsAssignableFrom(type)) return null;
        return type.GetCustomAttribute<CmdletAttribute>();
    }

    /// <summary>
    /// Full names of top-level types carrying <c>System.Management.Automation.CmdletAttribute</c>,
    /// read from the PE metadata. Null when the metadata cannot be read (caller falls back).
    /// </summary>
    private static List<string>? TryReadCmdletTypeNames(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();

            var names = new List<string>();
            foreach (var handle in md.TypeDefinitions)
            {
                var def = md.GetTypeDefinition(handle);
                if (def.GetDeclaringType().IsNil is false) continue; // nested types are never cmdlets here
                if (!HasCmdletAttribute(md, def)) continue;

                var ns = md.GetString(def.Namespace);
                var name = md.GetString(def.Name);
                names.Add(ns.Length == 0 ? name : ns + "." + name);
            }
            return names;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasCmdletAttribute(MetadataReader md, TypeDefinition def)
    {
        foreach (var attrHandle in def.GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(attrHandle);
            if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var typeRef = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (md.StringComparer.Equals(typeRef.Name, "CmdletAttribute")
                && md.StringComparer.Equals(typeRef.Namespace, "System.Management.Automation"))
                return true;
        }
        return false;
    }
}
