using System.Globalization;
using System.Net;
using Workshop.Surface;

namespace Workshop.App;

/// <summary>
/// The command line: <c>Workshop.App.exe [--db &lt;path&gt;] [--port &lt;n&gt;] [--session-dir &lt;dir&gt;]</c>.
/// Without <c>--db</c> the app creates a fresh database; with it, the file must already exist.
/// </summary>
internal sealed record AppOptions(string? DatabasePath, int Port, string SessionDirectory)
{
    public const string Usage = "Usage: Workshop.App.exe [--db <path>] [--port <n>] [--session-dir <dir>]";

    /// <exception cref="ArgumentException">An argument is unknown, repeated, or missing or bad in its value.</exception>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var name = args[index];
            if (name is not ("--db" or "--port" or "--session-dir"))
            {
                throw new ArgumentException($"Unknown argument '{name}'.");
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"{name} needs a value.");
            }

            if (!values.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"{name} is given more than once.");
            }
        }

        var port = SurfaceEndpoint.DefaultPort;
        if (values.TryGetValue("--port", out var portText)
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > IPEndPoint.MaxPort))
        {
            throw new ArgumentException($"--port needs a whole number from 1 to {IPEndPoint.MaxPort}, not '{portText}'.");
        }

        return new AppOptions(
            values.GetValueOrDefault("--db"),
            port,
            values.GetValueOrDefault("--session-dir") ?? SessionFile.DefaultDirectory);
    }
}
