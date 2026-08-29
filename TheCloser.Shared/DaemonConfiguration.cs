using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace TheCloser.Shared;

// The daemon's configuration root: optional appsettings.json beside the executable, hot-reloaded,
// with parse failures logged and swallowed so a bad edit degrades to the last good snapshot
// instead of killing the daemon (see the fix design's Configuration section).
internal static class DaemonConfiguration
{
    public static IConfigurationRoot Build(string directory, Action<string> logError) => new ConfigurationBuilder()
        .SetBasePath(directory)
        .AddJsonFile(source =>
        {
            source.Path = "appsettings.json";
            source.Optional = true;
            source.ReloadOnChange = true;
            // Parse failures only: the provider opens the file outside this handler's reach, so
            // an open failure faults the framework's discarded watcher task instead (accepted;
            // see the fix design's Configuration section).
            source.OnLoadException = context =>
            {
                logError($"Configuration reload failed: {context.Exception.Message}");
                context.Ignore = true;
            };
            source.ResolveFileProvider();
        })
        .Build();
}
