using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsBash.Host.Shell;

internal sealed record CommandAssistConfig
{
    private const long MaxConfigBytes = 1024 * 1024;

    public string DefaultProvider { get; init; } = "claude";
    public List<CommandAssistProviderConfig> Providers { get; init; } = [CommandAssistProviderConfig.ClaudeDefault()];

    public static CommandAssistConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("PSBASH_AI_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            var home = Environment.GetEnvironmentVariable("PSBASH_HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(home, ".psbash", "ai-providers.json");
        }

        if (!File.Exists(path))
            return new CommandAssistConfig();

        try
        {
            var json = PsBash.Host.Runtime.BoundedTextFile.Read(
                path,
                MaxConfigBytes,
                "config file exceeds the maximum supported size.");
            var config = JsonSerializer.Deserialize(json, CommandAssistJsonContext.Default.CommandAssistConfig);
            return config?.Normalize() ?? new CommandAssistConfig();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new CommandAssistProviderException($"could not read AI provider config '{path}': {ex.Message}");
        }
    }

    internal CommandAssistConfig Normalize()
    {
        var providers = Providers.Count == 0 ? [CommandAssistProviderConfig.ClaudeDefault()] : Providers;
        var defaultProvider = string.IsNullOrWhiteSpace(DefaultProvider) ? providers[0].Name : DefaultProvider;
        return this with { DefaultProvider = defaultProvider, Providers = providers };
    }

    public CommandAssistProviderConfig ResolveDefault()
        => Resolve(null);

    public CommandAssistProviderConfig Resolve(string? providerName)
    {
        var config = Normalize();
        var name = string.IsNullOrWhiteSpace(providerName) ? config.DefaultProvider : providerName;
        var provider = config.Providers.FirstOrDefault(p => p.Name == name);
        if (provider is null)
            throw new CommandAssistProviderException($"AI provider '{name}' is not configured.");
        return provider.Normalize();
    }

    public IReadOnlyList<string> ProviderNames()
        => Normalize().Providers.Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
}

internal sealed record CommandAssistProviderConfig
{
    public string Name { get; init; } = "";
    public string Executable { get; init; } = "";
    public List<string> Args { get; init; } = [];
    public string PromptTemplate { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string?> Environment { get; init; } = new(StringComparer.Ordinal);
    public int TimeoutMs { get; init; } = 30000;
    public int OutputLimit { get; init; } = 8192;

    public static CommandAssistProviderConfig ClaudeDefault() => new()
    {
        Name = "claude",
        Executable = "claude",
        Args = ["-p", "{{prompt}}"],
        PromptTemplate = """
            You are helping inside ps-bash, a bash-like shell backed by PowerShell on Windows.
            Context:
            - current input: {{buffer}}
            - cursor: {{cursor}}
            - working directory: {{cwd}}
            - shell: {{shell}}
            - OS: {{os}}

            Return compact JSON only:
            {"command":"single bash command or empty","explanation":"why this command is appropriate","refusal":"optional reason you cannot help","clarification":"optional question if needed","plan":["optional high-level steps"]}

            Use command only for a single command that can be reviewed before execution. If the user needs a multi-step plan, clarification, or refusal, leave command empty and fill the appropriate field.
            """,
        TimeoutMs = 30000,
        OutputLimit = 8192,
    };

    public CommandAssistProviderConfig Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new CommandAssistProviderException("AI provider name is required.");
        if (string.IsNullOrWhiteSpace(Executable))
            throw new CommandAssistProviderException($"AI provider '{Name}' is missing an executable.");
        var args = Args.Count == 0 ? ["{{prompt}}"] : Args;
        var promptTemplate = string.IsNullOrWhiteSpace(PromptTemplate)
            ? CommandAssistProviderConfig.ClaudeDefault().PromptTemplate
            : PromptTemplate;
        return this with
        {
            Args = args,
            PromptTemplate = promptTemplate,
            TimeoutMs = TimeoutMs <= 0 ? 30000 : TimeoutMs,
            OutputLimit = OutputLimit <= 0 ? 8192 : OutputLimit,
        };
    }
}

internal sealed class CommandAssistProviderException(string message) : Exception(message);

internal sealed record CommandAssistProviderResult(
    string ProviderName,
    string Command,
    string Explanation,
    bool IsExecutable)
{
    public CommandAssistReviewRequest ToReviewRequest(string cwd)
    {
        // Default-deny execution (H3): an LLM-authored command is INSERT-ONLY unless the user
        // has explicitly opted into the [e]xecute action via PSBASH_AI_ALLOW_EXEC. The
        // destructive-pattern classifier (CommandAssistSafety) is a best-effort denylist WARNING,
        // not a safe-execute gate — anything it doesn't list (Remove-Item bypasses, Format-Volume,
        // diskpart, [io.file]::Delete, obfuscation) would otherwise run on one keystroke. So the
        // safe default is: the user reviews/edits the command in the buffer and runs it themselves.
        bool executable = IsExecutable && PsBash.Core.Runtime.EnvFlags.IsTruthy("PSBASH_AI_ALLOW_EXEC");
        return new(ProviderName, Command, Explanation, cwd, executable, CommandAssistSafety.Classify(Command));
    }
}

internal sealed class CommandAssistProviderRunner(CommandAssistConfig config)
{
    public IReadOnlyList<string> ProviderNames => config.ProviderNames();

    public async Task<CommandAssistResponse> RunAsync(CommandAssistRequest request, string cwd, CancellationToken ct)
    {
        var result = await GenerateAsync(request, cwd, ct).ConfigureAwait(false);
        return CommandAssistResponse.Insert(result.ToReviewRequest(cwd).Command);
    }

    public async Task<CommandAssistProviderResult> GenerateAsync(CommandAssistRequest request, string cwd, CancellationToken ct)
        => await GenerateAsync(request, cwd, providerName: null, ct).ConfigureAwait(false);

    public async Task<CommandAssistProviderResult> GenerateAsync(
        CommandAssistRequest request,
        string cwd,
        string? providerName,
        CancellationToken ct)
    {
        var provider = config.Resolve(providerName);
        var prompt = RenderTemplate(provider.PromptTemplate, request, cwd);
        var psi = new ProcessStartInfo
        {
            FileName = provider.Executable,
            WorkingDirectory = string.IsNullOrWhiteSpace(provider.WorkingDirectory) ? cwd : provider.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in provider.Args)
            psi.ArgumentList.Add(arg == "{{prompt}}" ? prompt : RenderTemplate(arg, request, cwd));
        foreach (var (key, value) in provider.Environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = RenderTemplate(value, request, cwd);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new CommandAssistProviderException(
                $"AI provider '{provider.Name}' executable '{provider.Executable}' was not found or could not start: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            throw new CommandAssistProviderException(
                $"AI provider '{provider.Name}' could not start '{provider.Executable}': {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(provider.TimeoutMs);

        var stdoutTask = ReadWithLimitAsync(process.StandardOutput, provider.OutputLimit, timeoutCts.Token);
        var stderrTask = ReadWithLimitAsync(process.StandardError, provider.OutputLimit, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw new CommandAssistProviderException(
                $"AI provider '{provider.Name}' timed out after {provider.TimeoutMs}ms.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new CommandAssistProviderException(
                $"AI provider '{provider.Name}' exited {process.ExitCode}: {OneLine(detail)}");
        }

        var result = ParseProviderOutput(provider.Name, stdout);
        if (result.Command.Length == 0)
            throw new CommandAssistProviderException($"AI provider '{provider.Name}' returned no command.");
        return result;
    }

    internal static string RenderTemplate(string template, CommandAssistRequest request, string cwd)
        => template
            .Replace("{{buffer}}", Truncate(Redact(request.Buffer), 2000), StringComparison.Ordinal)
            .Replace("{{cursor}}", request.Cursor.ToString(), StringComparison.Ordinal)
            .Replace("{{cwd}}", Truncate(Redact(cwd), 1000), StringComparison.Ordinal)
            .Replace("{{shell}}", "ps-bash", StringComparison.Ordinal)
            .Replace("{{os}}", Environment.OSVersion.VersionString, StringComparison.Ordinal);

    internal static CommandAssistProviderResult ParseProviderOutput(string providerName, string output)
    {
        var normalized = NormalizeProviderOutput(output);
        if (normalized.Length == 0)
            return new CommandAssistProviderResult(providerName, "", "", IsExecutable: false);

        if (normalized[0] is '{' or '[')
            return ParseStructuredProviderOutput(providerName, normalized);

        var singleLine = !normalized.Contains('\n') && !normalized.Contains('\r');
        return new CommandAssistProviderResult(
            providerName,
            normalized,
            singleLine ? "" : "Provider returned explanatory text rather than a single executable command.",
            IsExecutable: singleLine);
    }

    private static CommandAssistProviderResult ParseStructuredProviderOutput(string providerName, string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return NonExecutable(providerName, output, "Provider returned structured output that was not an object.");

            var root = document.RootElement;
            var command = GetString(root, "command")?.Trim() ?? "";
            var explanation = GetString(root, "explanation")?.Trim() ?? "";
            var refusal = GetString(root, "refusal")?.Trim();
            var clarification = GetString(root, "clarification")?.Trim();
            var plan = GetStringArray(root, "plan");

            if (command.Length > 0)
                return new CommandAssistProviderResult(providerName, command, explanation, IsExecutable: true);

            var text = FirstNonEmpty(refusal, clarification, plan.Count > 0 ? string.Join(Environment.NewLine, plan) : null, explanation);
            return NonExecutable(
                providerName,
                string.IsNullOrWhiteSpace(text) ? output : text,
                "Provider did not return an executable command.");
        }
        catch (JsonException)
        {
            return NonExecutable(providerName, output, "Provider returned malformed JSON; review it before inserting.");
        }
    }

    private static CommandAssistProviderResult NonExecutable(string providerName, string text, string explanation)
        => new(providerName, text.Trim(), explanation, IsExecutable: false);

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                items.Add(text);
        return items;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    internal static string NormalizeProviderOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = trimmed.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))
                .ToArray();
            trimmed = string.Join('\n', lines).Trim();
        }
        return trimmed;
    }

    // Best-effort scrub of obvious secrets before the buffer leaves the machine. This is
    // defense-in-depth, NOT a guarantee — the user opted in (PSBASH_AI_ENABLE) knowing the line
    // is sent to an external model. It covers key=value / key: value (key name embedded anywhere,
    // so AWS_SECRET_ACCESS_KEY matches), quoted values, `Bearer <tok>`, JWTs, and AWS key ids.
    private static string Redact(string value)
    {
        // Specific token shapes FIRST: an assignment key like `Authorization:` would otherwise
        // consume only the bare `Bearer` word as its value and leave the token itself exposed.
        value = BearerTokenPattern.Replace(value, "$1<redacted>");
        value = JwtPattern.Replace(value, "<redacted-jwt>");
        value = AwsAccessKeyPattern.Replace(value, "<redacted-aws-key>");
        value = SensitiveAssignmentPattern.Replace(value, m => m.Groups[1].Value + m.Groups[2].Value + "<redacted>");
        // Credentials embedded in a URL's userinfo (scheme://user:PASSWORD@host).
        value = UrlUserInfoPattern.Replace(value, "$1<redacted>$2");
        // Password / secret command flags: --password X / --token=X / -pSECRET (joined).
        value = SecretFlagPattern.Replace(value, m => m.Groups[1].Value + "<redacted>");
        return value;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static readonly System.Text.RegularExpressions.Regex SensitiveAssignmentPattern = new(
        // group 1 = the key (with any surrounding word chars, e.g. AWS_SECRET_ACCESS_KEY),
        // group 2 = the = / : separator; the value (quoted or bare) is dropped.
        @"(?i)\b([\w.-]*(?:token|secret|password|passwd|pwd|api[_-]?key|access[_-]?key|session[_-]?token|client[_-]?secret|private[_-]?key|credential|auth)[\w.-]*)(\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s;,]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // scheme://user:PASSWORD@host — redact only the password portion (group 1 keeps
    // scheme://user:, group 2 keeps the @). Username is preserved (it is not the secret).
    private static readonly System.Text.RegularExpressions.Regex UrlUserInfoPattern = new(
        @"(?i)\b([a-z][a-z0-9+.\-]*://[^\s:/@]+:)[^\s@/]+(@)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Password / secret command flags. Group 1 (flag + separator) is kept; the value
    // is dropped. Covers the long forms with =, space, or quotes, plus the classic
    // short -p in its JOINED form (mysql -phunter2). Bare spaced `-p <arg>` is
    // deliberately NOT matched — it is far more often a non-secret (cp/mkdir/ps/docker
    // -p), and over-redacting it would strip useful context from the assist prompt.
    private static readonly System.Text.RegularExpressions.Regex SecretFlagPattern = new(
        @"(?i)((?:--(?:password|passwd|pass|token|secret|api[-_]?key|access[-_]?key|client[-_]?secret)(?:\s*=\s*|\s+)|(?<![\w-])-p(?=\S)))(?:""[^""]*""|'[^']*'|\S+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BearerTokenPattern = new(
        @"(?i)\b(bearer\s+)[A-Za-z0-9._\-+/=]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex AwsAccessKeyPattern = new(
        @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<string> ReadWithLimitAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var buffer = new char[Math.Min(4096, Math.Max(1, limit))];
        var sb = new StringBuilder(Math.Min(1024, limit));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = limit - sb.Length;
            if (remaining > 0)
                sb.Append(buffer, 0, Math.Min(read, remaining));
        }
        return sb.ToString();
    }

    private static string OneLine(string value)
    {
        var line = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 300 ? line : line[..300] + "...";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
    }

    private static async Task KillAndDrainAsync(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        TryKill(process);

        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (Exception) { }

        try { await stdoutTask.ConfigureAwait(false); } catch { }
        try { await stderrTask.ConfigureAwait(false); } catch { }
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CommandAssistConfig))]
[JsonSerializable(typeof(CommandAssistProviderConfig))]
internal partial class CommandAssistJsonContext : JsonSerializerContext
{
}
