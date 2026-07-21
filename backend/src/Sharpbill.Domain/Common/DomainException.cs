namespace Sharpbill.Domain.Common;

/// <summary>Represents a deterministic violation of a domain invariant.</summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }

    public string Code { get; }
}
