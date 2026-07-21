namespace Sharpbill.Infrastructure.Configuration;

public static class DevelopmentAuthenticationGuard
{
    public static bool IsStrongIndependentSecret(string? secret, string? sessionSecret) =>
        secret is { Length: >= 32 } &&
        !secret.Contains("replace-me", StringComparison.OrdinalIgnoreCase) &&
        secret.Distinct().Take(8).Count() == 8 &&
        !string.Equals(secret, sessionSecret, StringComparison.Ordinal);

    public static bool IsEnabled(SharpbillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsLocal &&
            options.DevelopmentAuthentication.Enabled &&
            IsStrongIndependentSecret(
                options.DevelopmentAuthentication.Secret,
                options.Session.ActiveSecret);
    }
}
