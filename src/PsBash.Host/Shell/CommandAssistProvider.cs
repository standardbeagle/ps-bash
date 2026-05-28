using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsBash.Host.Shell;

internal sealed record CommandAssistConfig
{
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
            var json = File.ReadAllText(path);
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
            Generate a safe bash command for ps-bash on Windows.
            Current line: {{buffer}}
            Cursor: {{cursor}}
            Working directory: {{cwd}}
            Return only the command to place at the prompt.
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

internal sealed record CommandAssistProviderResult(string ProviderName, string Command, string Explanation)
{
    public CommandAssistReviewRequest ToReviewRequest(string cwd)
        => new(ProviderName, Command, Explanation, cwd, CommandAssistSafety.Classify(Command));
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
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

        var command = NormalizeProviderOutput(stdout);
        if (command.Length == 0)
            throw new CommandAssistProviderException($"AI provider '{provider.Name}' returned no command.");
        return new CommandAssistProviderResult(provider.Name, command, "");
    }

    internal static string RenderTemplate(string template, CommandAssistRequest request, string cwd)
        => template
            .Replace("{{buffer}}", request.Buffer, StringComparison.Ordinal)
            .Replace("{{cursor}}", request.Cursor.ToString(), StringComparison.Ordinal)
            .Replace("{{cwd}}", cwd, StringComparison.Ordinal);

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
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CommandAssistConfig))]
[JsonSerializable(typeof(CommandAssistProviderConfig))]
internal partial class CommandAssistJsonContext : JsonSerializerContext
{
}
