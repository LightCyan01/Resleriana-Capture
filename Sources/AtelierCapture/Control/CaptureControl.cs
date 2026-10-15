using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace AtelierCapture;

internal sealed class CaptureControl : IDisposable
{
    internal const int RequestLimit = 4096;
    internal const int ResponseLimit = 1024 * 1024;
    private readonly string runId;
    private readonly Func<object> status;
    private readonly Action<string> abort;
    private readonly OperationalStatus operational;
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    private int disposed;
    internal string PipeName { get; }

    internal CaptureControl(
        string runId,
        Func<object> status,
        Action<string> abort,
        OperationalStatus operational,
        string? pipeName = null
    )
    {
        this.runId = runId;
        this.status = status;
        this.abort = abort;
        this.operational = operational;
        PipeName = pipeName ?? $"atelier-capture-{Environment.ProcessId}";
        server = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = LocalControlPipe.Create(PipeName);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var length = new byte[4];
                await pipe.ReadExactlyAsync(length, timeout.Token).ConfigureAwait(false);
                var size = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size <= 0 || size > RequestLimit)
                    throw new InvalidDataException("Control frame length.");
                var body = new byte[size];
                await pipe.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
                var response = Handle(body);
                if (response.Length > ResponseLimit)
                    throw new InvalidDataException("Control response length.");
                BinaryPrimitives.WriteInt32LittleEndian(length, response.Length);
                await pipe.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await pipe.WriteAsync(response, timeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                operational.Report($"Capture control connection failed: {error.GetType().Name}.");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), stop.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal byte[] Handle(ReadOnlySpan<byte> input)
    {
        try
        {
            if (input.Length == 0 || input.Length > RequestLimit)
                return Reply(false, "invalid_frame");
            using var document = JsonDocument.Parse(
                input.ToArray(),
                new JsonDocumentOptions { MaxDepth = 4 }
            );
            var data = document.RootElement;
            if (
                data.ValueKind != JsonValueKind.Object
                || data.GetProperty("schema").GetInt32() != 1
            )
                return Reply(false, "invalid_schema");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in data.EnumerateObject())
                if (
                    field.Name is not ("schema" or "command" or "run_id" or "reason")
                    || !fields.Add(field.Name)
                )
                    return Reply(false, "invalid_fields");
            var command = data.GetProperty("command").GetString();
            var requestedRun = data.TryGetProperty("run_id", out var run) ? run.GetString() : null;
            if (requestedRun is not null && requestedRun != runId)
                return Reply(false, "wrong_run");
            if (command == "status")
                return JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        schema = 1,
                        accepted = true,
                        run_id = runId,
                        process_id = Environment.ProcessId,
                        status = status(),
                    }
                );
            if (command != "abort")
                return Reply(false, "invalid_command");
            if (requestedRun != runId)
                return Reply(false, "run_required");
            var reason = data.GetProperty("reason").GetString();
            if (
                string.IsNullOrWhiteSpace(reason)
                || reason.Length > 256
                || reason.Any(char.IsControl)
            )
                return Reply(false, "invalid_reason");
            abort(reason);
            return Reply(true, "abort_requested");
        }
        catch (JsonException)
        {
            return Reply(false, "malformed_json");
        }
        catch (Exception error)
            when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Reply(false, "invalid_request");
        }
    }

    private byte[] Reply(bool accepted, string result) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schema = 1,
                accepted,
                result,
                run_id = runId,
                process_id = Environment.ProcessId,
            }
        );

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        stop.Cancel();
        // The control thread never waits for recorder I/O or native callbacks.
        _ = server.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
