namespace PsBash.Core.Transpiler;

public interface ITransform
{
    void Apply(ref TranspileContext context);
}
