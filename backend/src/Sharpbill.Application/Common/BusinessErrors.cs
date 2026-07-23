namespace Sharpbill.Application.Common;

public static class BusinessErrors
{
    public static ApiException SettingsNotInitialized() =>
        new(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized");
}
