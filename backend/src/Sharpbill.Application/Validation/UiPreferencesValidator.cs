using Sharpbill.Application.Common;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Application.Validation;

public sealed class UiPreferencesValidator : IValidator<UiPreferencesContract>
{
    private static readonly IReadOnlySet<string> BaseTones = Set(
        "abyss", "ink", "graphite", "midnight", "warm-black");
    private static readonly IReadOnlySet<string> BackgroundDepths = Set(
        "pure-black", "standard", "elevated");
    private static readonly IReadOnlySet<string> BorderGlows = Set(
        "hairline", "standard", "neon");
    private static readonly IReadOnlySet<string> GlowIntensities = Set(
        "off", "subtle", "normal", "intense");
    private static readonly IReadOnlySet<string> Scanlines = Set(
        "off", "subtle", "standard", "heavy");
    private static readonly IReadOnlySet<string> CornerRadii = Set("sharp", "soft", "round");
    private static readonly IReadOnlySet<string> Motions = Set("full", "calm", "reduced");
    private static readonly IReadOnlySet<string> RainSpeeds = Set("still", "slow", "normal", "fast");
    private static readonly IReadOnlySet<string> RainGlyphs = Set("katakana", "ascii", "binary", "hex");
    private static readonly IReadOnlySet<string> FontFamilies = Set(
        "system", "high-legibility", "cascadia", "jetbrains", "consolas", "menlo");
    private static readonly IReadOnlySet<string> TextScales = Set("90", "100", "112", "125");
    private static readonly IReadOnlySet<string> Densities = Set("compact", "comfortable", "spacious");
    private static readonly IReadOnlySet<string> FocusRings = Set(
        "standard", "bold", "high-contrast");

    public ValidationResult Validate(UiPreferencesContract value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        Allowed(errors, value.BaseTone.Value, BaseTones, "base_tone");
        Allowed(errors, value.BackgroundDepth.Value, BackgroundDepths, "background_depth");
        Allowed(errors, value.BorderGlow.Value, BorderGlows, "border_glow");
        Allowed(errors, value.GlowIntensity.Value, GlowIntensities, "glow_intensity");
        Allowed(errors, value.Scanlines.Value, Scanlines, "scanlines");
        Allowed(errors, value.CornerRadius.Value, CornerRadii, "corner_radius");
        Allowed(errors, value.Motion.Value, Motions, "motion");
        Allowed(errors, value.RainSpeed.Value, RainSpeeds, "rain_speed");
        Allowed(errors, value.RainGlyphs.Value, RainGlyphs, "rain_glyphs");
        Allowed(errors, value.FontFamily.Value, FontFamilies, "font_family");
        Allowed(errors, value.TextScale.Value, TextScales, "text_scale");
        Allowed(errors, value.Density.Value, Densities, "density");
        Allowed(errors, value.FocusRing.Value, FocusRings, "focus_ring");
        if (value.RainDensity.Value is { } density &&
            (!double.IsFinite(density) || density is < 0 or > 0.8))
        {
            errors.Add(new(
                "rain_density",
                "OUT_OF_RANGE",
                "rain_density must be a finite number between 0 and 0.8"));
        }

        return errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors);
    }

    private static HashSet<string> Set(params string[] values) =>
        new(values, StringComparer.Ordinal);

    private static void Allowed(
        List<ValidationFailure> errors,
        string? value,
        IReadOnlySet<string> allowed,
        string field)
    {
        if (value is not null && !allowed.Contains(value))
        {
            errors.Add(new(field, "INVALID_VALUE", $"{field} contains an unsupported value"));
        }
    }
}
