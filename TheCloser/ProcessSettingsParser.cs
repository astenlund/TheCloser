using Microsoft.Extensions.Configuration;

using static TheCloser.TitleBarClickPosition;

namespace TheCloser;

internal static class ProcessSettingsParser
{
    public static ProcessSettings Parse(IConfiguration config, string processName, Action<string>? logWarning = null)
    {
        var section = config.GetSection(processName);

        var simpleValue = section.Value;

        if (!string.IsNullOrEmpty(simpleValue))
        {
            return new ProcessSettings { Method = simpleValue.ToUpperInvariant() };
        }

        return new ProcessSettings
        {
            Method = section["Method"]?.ToUpperInvariant(),
            ClickPosition = Enum.TryParse<TitleBarClickPosition>(section["ClickPosition"], out var clickPos)
                ? clickPos
                : Left
        };
    }
}
