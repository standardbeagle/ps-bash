namespace PsBash.Core.Runtime;

public sealed class PwshNotFoundException : Exception
{
    public PwshNotFoundException(string message) : base(message) { }
}
