using System.Globalization;
using System.Text.Json;

namespace Workshop.Surface.Tests;

public sealed class AuditLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AuditLogTests", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(directory, "audit.jsonl");

    [Fact]
    public void Append_writes_one_json_line_per_action_with_its_target()
    {
        var log = new AuditLog(LogPath);
        var before = DateTimeOffset.UtcNow;

        log.Append(new SurfaceAction(ActionTypes.Open, "jobs", null, null, null, null, null), Outcomes.Ok);
        log.Append(new SurfaceAction(ActionTypes.Set, null, "fault", "Won't boot", null, null, null), Outcomes.ValidationFailed);
        log.Append(new SurfaceAction(ActionTypes.Select, null, null, null, "jobs", "J-7", null), Outcomes.NotFound);
        log.Append(new SurfaceAction(ActionTypes.Press, null, null, null, null, null, "save"), Outcomes.Disabled);

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(4, lines.Length);

        string?[][] expected =
        [
            ["open", "jobs", null, "ok"],
            ["set", "fault", "Won't boot", "validation_failed"],
            ["select", "jobs/J-7", null, "not_found"],
            ["press", "save", null, "disabled"],
        ];

        for (var i = 0; i < lines.Length; i++)
        {
            var line = JsonDocument.Parse(lines[i]).RootElement;
            Assert.Equal(["at", "type", "target", "value", "outcome"], line.EnumerateObject().Select(p => p.Name));

            var at = DateTimeOffset.Parse(line.GetProperty("at").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(at, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

            Assert.Equal(expected[i], new[]
            {
                line.GetProperty("type").GetString(),
                line.GetProperty("target").GetString(),
                line.GetProperty("value").GetString(),
                line.GetProperty("outcome").GetString(),
            });
        }
    }

    [Fact]
    public void Append_adds_to_an_existing_log()
    {
        new AuditLog(LogPath).Append(new SurfaceAction(ActionTypes.Press, null, null, null, null, null, "save"), Outcomes.Ok);
        new AuditLog(LogPath).Append(new SurfaceAction(ActionTypes.Press, null, null, null, null, null, "cancel"), Outcomes.Ok);

        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void A_value_with_line_breaks_stays_on_one_line()
    {
        var log = new AuditLog(LogPath);

        log.Append(new SurfaceAction(ActionTypes.Set, null, "notes", "first\r\n{\"type\":\"forged\"}\nthird", null, null, null), Outcomes.Ok);

        var line = Assert.Single(File.ReadAllLines(LogPath));
        Assert.Equal("first\r\n{\"type\":\"forged\"}\nthird", JsonDocument.Parse(line).RootElement.GetProperty("value").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
