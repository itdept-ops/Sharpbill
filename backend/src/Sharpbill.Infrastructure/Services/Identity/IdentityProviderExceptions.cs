namespace Sharpbill.Infrastructure.Services.Identity;

/// <summary>A provider token was malformed, untrusted, expired, or replayed.</summary>
public sealed class IdentityTokenException : Exception
{
    public IdentityTokenException(string message)
        : base(message)
    {
    }

    public IdentityTokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Provider verification could not complete within its bounded availability budget.</summary>
public sealed class IdentityProviderUnavailableException : Exception
{
    public IdentityProviderUnavailableException(string message)
        : base(message)
    {
    }

    public IdentityProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
