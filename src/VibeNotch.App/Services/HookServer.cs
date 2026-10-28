using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace VibeNotch;

public sealed record PermissionDecision(string Decision, string Reason, string Output, JsonNode? UpdatedInput);

public sealed class HookServer : IDisposable
{
    public const string PipeName = "vibe-notch";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _permissionTimeoutSeconds;

    public event Action<HookEvent>? EventReceived;
    public event Action<string>? PermissionTimedOut;

    public HookServer(int permissionTimeoutSeconds)
    {
        _permissionTimeoutSeconds = Math.Max(10, permissionTimeoutSeconds);
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    public void Resolve(string requestId, string decision, string reason, string output, JsonNode? updatedInput = null)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(new PermissionDecision(decision, reason, output, updatedInput));
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    20,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);

                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Write("pipe accept: " + ex.Message);
                await Task.Delay(200);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (string.IsNullOrWhiteSpace(line)) return;

                var evt = HookEvent.Parse(line);
                if (evt == null) return;

                if (string.IsNullOrEmpty(evt.RequestId))
                {
                    EventReceived?.Invoke(evt);
                    return;
                }

                var tcs = new TaskCompletionSource<PermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[evt.RequestId] = tcs;
                EventReceived?.Invoke(evt);

                var timeout = Task.Delay(TimeSpan.FromSeconds(_permissionTimeoutSeconds), _cts.Token);
                var winner = await Task.WhenAny(tcs.Task, timeout);
                _pending.TryRemove(evt.RequestId, out _);

                PermissionDecision decision;
                if (winner == tcs.Task)
                {
                    decision = await tcs.Task;
                }
                else
                {
                    decision = new PermissionDecision("ask", "", "qoder", null);
                    PermissionTimedOut?.Invoke(evt.RequestId);
                }

                var json = new JsonObject
                {
                    ["decision"] = decision.Decision,
                    ["reason"] = decision.Reason,
                    ["output"] = decision.Output
                };
                if (decision.UpdatedInput != null) json["updatedInput"] = decision.UpdatedInput.DeepClone();

                await writer.WriteLineAsync(json.ToJsonString());
                await writer.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Write("pipe client: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var tcs))
                {
                    tcs.TrySetResult(new PermissionDecision("ask", "", "qoder", null));
                }
            }
        }
        catch
        {
            // ignore shutdown errors
        }
    }
}
