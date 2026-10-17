using System.Text;
using Microsoft.Extensions.Hosting;

namespace ProxyDesk.Service;

public sealed class ActivityLogWorker(EngineService engine, ServiceOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        var path = Path.Combine(options.DataDirectory, "activity.log");
        try
        {
            while (await engine.ActivityReader.WaitToReadAsync(token))
            {
                // Rotate on the background writer, never on the packet-processing thread.
                if (File.Exists(path) && new FileInfo(path).Length >= 5 * 1024 * 1024)
                {
                    if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2", true);
                    File.Move(path, path + ".1", true);
                }
                var batch = new StringBuilder();
                var dropped = engine.TakeDroppedActivities();
                if (dropped != 0) batch.AppendLine($"{DateTimeOffset.Now:O} [Warning] Activity journal overflow: dropped={dropped}");
                for (var i = 0; i < 128 && engine.ActivityReader.TryRead(out var entry); i++)
                    batch.AppendLine($"{entry.Time:O} [{entry.Level}] #{entry.Sequence} {entry.Message}");
                await File.AppendAllTextAsync(path, batch.ToString(), Encoding.UTF8, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { engine.Activity("Error", "Activity file logging failed: " + error.Message); }
    }
}
