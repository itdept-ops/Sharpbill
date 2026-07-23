namespace Sharpbill.Application.Abstractions;

public sealed class PersistenceOperationException : Exception
{
    public PersistenceOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
