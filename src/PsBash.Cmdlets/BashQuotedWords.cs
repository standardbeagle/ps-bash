namespace PsBash.Cmdlets;

/// <summary>Run-time word building the transpiler emits for quoted array expansions.</summary>
public static class BashQuotedWords
{
    /// <summary>
    /// <c>-"${a[@]}"</c> / <c>"p${a[@]}q"</c> / <c>k"$@"</c>: a quoted all-elements expansion with text glued on. Each element is
    /// its own field (never split or globbed); the prefix joins the first, the suffix the last. No elements leaves one field,
    /// the prefix and suffix (bash 5.2: <c>a=(); printf '[%s]' -"${a[@]}"</c> prints <c>[-]</c>).
    /// </summary>
    public static string[] Affix(string? prefix, string? suffix, object?[]? values)
    {
        prefix ??= "";
        suffix ??= "";
        if (values is null || values.Length == 0) return [prefix + suffix];
        var fields = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
            fields[i] = values[i] switch
            {
                null => "",
                System.Management.Automation.PSObject p => p.BaseObject?.ToString() ?? "",
                var v => v.ToString() ?? "",
            };
        fields[0] = prefix + fields[0];
        fields[^1] += suffix;
        return fields;
    }
}
