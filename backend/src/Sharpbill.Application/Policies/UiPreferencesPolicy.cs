using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Application.Policies;

public static class UiPreferencesPolicy
{
    public static UiPreferences ApplyPatch(
        UiPreferences? current,
        UiPreferencesContract patch,
        UiPreferencesValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        (validator ?? new UiPreferencesValidator()).Validate(patch).ThrowIfInvalid();
        current ??= new UiPreferences();
        return current with
        {
            BaseTone = Apply(current.BaseTone, patch.BaseTone),
            BackgroundDepth = Apply(current.BackgroundDepth, patch.BackgroundDepth),
            BorderGlow = Apply(current.BorderGlow, patch.BorderGlow),
            GlowIntensity = Apply(current.GlowIntensity, patch.GlowIntensity),
            Scanlines = Apply(current.Scanlines, patch.Scanlines),
            CornerRadius = Apply(current.CornerRadius, patch.CornerRadius),
            Motion = Apply(current.Motion, patch.Motion),
            RainDensity = Apply(current.RainDensity, patch.RainDensity),
            RainSpeed = Apply(current.RainSpeed, patch.RainSpeed),
            RainGlyphs = Apply(current.RainGlyphs, patch.RainGlyphs),
            FontFamily = Apply(current.FontFamily, patch.FontFamily),
            TextScale = Apply(current.TextScale, patch.TextScale),
            Density = Apply(current.Density, patch.Density),
            HighContrastText = Apply(current.HighContrastText, patch.HighContrastText),
            ReduceTransparency = Apply(current.ReduceTransparency, patch.ReduceTransparency),
            FocusRing = Apply(current.FocusRing, patch.FocusRing),
            ZebraRows = Apply(current.ZebraRows, patch.ZebraRows),
            LinkUnderlines = Apply(current.LinkUnderlines, patch.LinkUnderlines),
            Version = Apply(current.Version, patch.Version),
        };
    }

    private static T? Apply<T>(T? current, PatchField<T?> patch) =>
        patch.HasValue ? patch.Value : current;
}
