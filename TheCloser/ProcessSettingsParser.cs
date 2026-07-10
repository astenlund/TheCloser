using Microsoft.Extensions.Configuration;

namespace TheCloser;

internal static class ProcessSettingsParser
{
    public static ProcessSettings Parse(IConfiguration config, string processName, Action<string>? logWarning = null)
    {
        var section = config.GetSection(processName);

        var simpleValue = section.Value;

        if (!string.IsNullOrEmpty(simpleValue))
        {
            return new ProcessSettings { Method = simpleValue };
        }

        return new ProcessSettings
        {
            Method = section["Method"],
            ClickPosition = ParseClickPosition(section["ClickPosition"], processName, logWarning)
        };
    }

    private static TitleBarClickPosition? ParseClickPosition(string? value, string processName, Action<string>? logWarning)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (Enum.TryParse<TitleBarClickPosition>(value, ignoreCase: true, out var clickPosition))
        {
            return clickPosition;
        }

        logWarning?.Invoke($"Invalid ClickPosition '{value}' for process '{processName}'. Ignoring it.");

        return null;
    }
}
