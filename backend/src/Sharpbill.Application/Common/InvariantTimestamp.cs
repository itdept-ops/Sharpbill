using System.Globalization;

namespace Sharpbill.Application.Common;

public static class InvariantTimestamp
{
    public static string Format(DateTime? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        string rendered = value.Value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffff",
            CultureInfo.InvariantCulture);
        return rendered.TrimEnd('0').TrimEnd('.');
    }
}
