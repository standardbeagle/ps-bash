using System.Text.RegularExpressions;

namespace PsBash.Core.Transpiler.Transforms;

public sealed partial class EnvVarTransform : ITransform
{
    public void Apply(ref TranspileContext context)
    {
        var input = context.Result;
        var result = BashEnvVar().Replace(input, EnvVarReplacer);
        if (!ReferenceEquals(result, input))
        {
            context.Result = result;
            context.Modified = true;
        }
    }

    private static string EnvVarReplacer(Match m)
    {
        var name = m.Groups["name"].Value;

        // Skip PowerShell built-in variables and already-transformed env: vars
        if (name is "null" or "true" or "false" or "HOME" or "LASTEXITCODE"
            or "_" or "PSVersionTable" or "ErrorActionPreference")
            return m.Value;

        // Skip $env: prefix (already transformed)
        var prefix = m.Value;
        if (prefix.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
            return m.Value;

        return $"$env:{name}";
    }

    [GeneratedRegex(@"(?<!\w)\$(?!env:)(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BashEnvVar();
}
