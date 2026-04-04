namespace PsBash.Core.Transpiler;

public ref struct TranspileContext
{
    public string Result;
    public bool Modified;

    public TranspileContext(string input)
    {
        Result = input;
        Modified = false;
    }
}
