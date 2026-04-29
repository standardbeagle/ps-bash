// PsBash.Host entry point — daemon mode coming in T05a/T08a.
// Currently a stub so the project builds.
namespace PsBash.Host;

internal sealed class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.Error.WriteLine("ps-bash-host: daemon mode not yet implemented (T05a)");
        await Task.CompletedTask;
        return 1;
    }
}
