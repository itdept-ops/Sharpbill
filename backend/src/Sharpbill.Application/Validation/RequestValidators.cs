using System.Net.Mail;
using System.Text.RegularExpressions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Application.Validation;

public sealed partial class TokenLoginRequestValidator : IValidator<TokenLoginRequest>
{
    public ValidationResult Validate(TokenLoginRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        Validators.Length(errors, value.IdToken, 1, 16_384, "id_token");
        Validators.Length(errors, value.LegalBundleVersion, 1, 64, "legal_bundle_version");
        return Validators.Result(errors);
    }
}

public sealed class DevLoginRequestValidator : IValidator<DevLoginRequest>
{
    public ValidationResult Validate(DevLoginRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        try
        {
            _ = new MailAddress(value.Email);
        }
        catch (FormatException)
        {
            errors.Add(new("email", "INVALID_EMAIL", "email must be a valid address"));
        }

        Validators.Length(errors, value.Email, 1, 255, "email");
        Validators.OptionalLength(errors, value.Role, 1, 49, "role");
        Validators.OptionalLength(errors, value.DisplayName, 0, 255, "display_name");
        Validators.Length(errors, value.LegalBundleVersion, 1, 64, "legal_bundle_version");
        return Validators.Result(errors);
    }
}

public sealed class LocationUpdateRequestValidator : IValidator<LocationUpdateRequest>
{
    public ValidationResult Validate(LocationUpdateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        Validators.FiniteRange(errors, value.Latitude, -90, 90, "latitude");
        Validators.FiniteRange(errors, value.Longitude, -180, 180, "longitude");
        if (value.Accuracy is { } accuracy)
        {
            Validators.FiniteRange(errors, accuracy, 0, 100_000, "accuracy");
        }

        return Validators.Result(errors);
    }
}

public sealed partial class PermissionCreateRequestValidator : IValidator<PermissionCreateRequest>
{
    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionKeyRegex();

    public ValidationResult Validate(PermissionCreateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        var normalized = value.Key.Trim().ToLowerInvariant();
        if (normalized.Length > 100 || !PermissionKeyRegex().IsMatch(normalized))
        {
            errors.Add(new(
                "key",
                "INVALID_PERMISSION_KEY",
                "key must look like 'area.action' (lowercase, dot-separated)"));
        }

        Validators.OptionalLength(errors, value.Description, 0, 255, "description");
        return Validators.Result(errors);
    }
}

public sealed partial class RoleCreateRequestValidator : IValidator<RoleCreateRequest>
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _-]{0,48}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNameRegex();

    public ValidationResult Validate(RoleCreateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        if (!RoleNameRegex().IsMatch(value.Name.Trim()))
        {
            errors.Add(new(
                "name",
                "INVALID_ROLE_NAME",
                "name must be 1-49 chars: letters, digits, space, _ or -"));
        }

        Validators.OptionalLength(errors, value.Description, 0, 255, "description");
        Validators.PermissionKeys(errors, value.PermissionKeys);
        return Validators.Result(errors);
    }

    internal static bool IsValidName(string name) => RoleNameRegex().IsMatch(name.Trim());
}

public sealed class RoleUpdateRequestValidator : IValidator<RoleUpdateRequest>
{
    public ValidationResult Validate(RoleUpdateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        if (value.Name.HasValue &&
            value.Name.Value is not null &&
            !RoleCreateRequestValidator.IsValidName(value.Name.Value))
        {
            errors.Add(new(
                "name",
                "INVALID_ROLE_NAME",
                "name must be 1-49 chars: letters, digits, space, _ or -"));
        }

        if (value.Description.HasValue)
        {
            Validators.OptionalLength(errors, value.Description.Value, 0, 255, "description");
        }

        if (value.PermissionKeys.HasValue && value.PermissionKeys.Value is not null)
        {
            Validators.PermissionKeys(errors, value.PermissionKeys.Value);
        }

        if (value.ExpectedVersion.HasValue)
        {
            Validators.OptionalPositive(errors, value.ExpectedVersion.Value, "expected_version");
        }
        return Validators.Result(errors);
    }
}

public sealed class ProfileUpdateRequestValidator : IValidator<ProfileUpdateRequest>
{
    public ValidationResult Validate(ProfileUpdateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        OptionalLength(errors, value.DisplayName, 0, 255, "display_name");
        OptionalLength(errors, value.Title, 0, 120, "title");
        OptionalLength(errors, value.Department, 0, 120, "department");
        OptionalLength(errors, value.Phone, 0, 40, "phone");
        OptionalLength(errors, value.Location, 0, 120, "location");
        OptionalLength(errors, value.Timezone, 0, 60, "timezone");
        OptionalLength(errors, value.Bio, 0, 500, "bio");
        if (value.AccentColor.HasValue &&
            value.AccentColor.Value is not null &&
            !Regex.IsMatch(
                value.AccentColor.Value,
                "^#[0-9a-fA-F]{6}$",
                RegexOptions.CultureInvariant))
        {
            errors.Add(new(
                "accent_color",
                "INVALID_COLOR",
                "accent_color must be a six-digit hexadecimal color"));
        }

        if (value.UiPreferences.HasValue && value.UiPreferences.Value is not null)
        {
            errors.AddRange(new UiPreferencesValidator().Validate(value.UiPreferences.Value).Errors);
        }

        return Validators.Result(errors);
    }

    private static void OptionalLength(
        ICollection<ValidationFailure> errors,
        Contracts.Common.PatchField<string?> field,
        int minimum,
        int maximum,
        string name)
    {
        if (field.HasValue)
        {
            Validators.OptionalLength(errors, field.Value, minimum, maximum, name);
        }
    }
}

public sealed class BulkActionRequestValidator : IValidator<BulkActionRequest>
{
    public ValidationResult Validate(BulkActionRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        if (value.Ids.Count is < 1 or > DomainLimits.MaxBulkUsers)
        {
            errors.Add(new("ids", "INVALID_COUNT", "ids must contain between 1 and 500 users"));
        }

        if (value.Ids.Any(static id => id < 1))
        {
            errors.Add(new("ids", "INVALID_ID", "ids must contain positive user identifiers"));
        }

        if (value.Ids.Distinct().Count() != value.Ids.Count)
        {
            errors.Add(new("ids", "DUPLICATE_ID", "ids must not contain duplicates"));
        }

        if (value.Action == Contracts.Common.BulkUserActionContract.AssignRole && value.RoleId is null)
        {
            errors.Add(new("role_id", "REQUIRED", "role_id is required for assign_role"));
        }

        if (value.Action != Contracts.Common.BulkUserActionContract.AssignRole && value.RoleId is not null)
        {
            errors.Add(new("role_id", "UNEXPECTED", "role_id is only valid for assign_role"));
        }

        return Validators.Result(errors);
    }
}

public sealed partial class RetentionHoldUpdateRequestValidator : IValidator<RetentionHoldUpdateRequest>
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{2,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    public ValidationResult Validate(RetentionHoldUpdateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var reference = value.Reference?.Trim();
        if (value.Enabled && reference is null)
        {
            return ValidationResult.Failure(new ValidationFailure(
                "reference",
                "REQUIRED",
                "reference is required when enabling a retention hold"));
        }

        if (!value.Enabled && reference is not null)
        {
            return ValidationResult.Failure(new ValidationFailure(
                "reference",
                "UNEXPECTED",
                "reference must be omitted when releasing a retention hold"));
        }

        if (reference is not null && !ReferenceRegex().IsMatch(reference))
        {
            return ValidationResult.Failure(new ValidationFailure(
                "reference",
                "INVALID_REFERENCE",
                "reference must be a 3-255 character external case or ticket key"));
        }

        return ValidationResult.Success;
    }
}

internal static class Validators
{
    public static ValidationResult Result(IReadOnlyCollection<ValidationFailure> errors) =>
        errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors);

    public static void Length(
        ICollection<ValidationFailure> errors,
        string? value,
        int minimum,
        int maximum,
        string field)
    {
        if (value is null || value.Length < minimum || value.Length > maximum)
        {
            errors.Add(new(field, "INVALID_LENGTH", $"{field} has an invalid length"));
        }
    }

    public static void OptionalLength(
        ICollection<ValidationFailure> errors,
        string? value,
        int minimum,
        int maximum,
        string field)
    {
        if (value is not null && (value.Length < minimum || value.Length > maximum))
        {
            errors.Add(new(field, "INVALID_LENGTH", $"{field} has an invalid length"));
        }
    }

    public static void OptionalPositive(
        ICollection<ValidationFailure> errors,
        int? value,
        string field)
    {
        if (value is < 1)
        {
            errors.Add(new(field, "OUT_OF_RANGE", $"{field} must be positive"));
        }
    }

    public static void FiniteRange(
        ICollection<ValidationFailure> errors,
        double value,
        double minimum,
        double maximum,
        string field)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            errors.Add(new(field, "OUT_OF_RANGE", $"{field} is outside its allowed range"));
        }
    }

    public static void PermissionKeys(
        ICollection<ValidationFailure> errors,
        IReadOnlyList<string> keys)
    {
        if (keys.Count > DomainLimits.MaxPermissionKeysPerMutation)
        {
            errors.Add(new(
                "permission_keys",
                "INVALID_COUNT",
                "permission_keys cannot contain more than 100 values"));
        }

        if (keys.Any(static key => string.IsNullOrWhiteSpace(key) || key.Length > 100))
        {
            errors.Add(new(
                "permission_keys",
                "INVALID_PERMISSION_KEY",
                "permission keys must be non-empty and no longer than 100 characters"));
        }
    }
}
