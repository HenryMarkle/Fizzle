namespace Fizzle.Data;

public interface ILingoVector
{
    int CountElems { get; }
    object? this[int index] { get; }
}