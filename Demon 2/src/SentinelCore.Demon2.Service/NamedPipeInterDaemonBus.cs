using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using SentinelCore.Demon2.Core;

namespace SentinelCore.Demon2.Service;

/// <summary>Maintains one Guard pipe session for the lifetime of this D2 service.</summary>
public sealed class NamedPipeInterDaemonBus : IInterDaemonBus, IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private bool _welcomed;
    private bool _disposed;

    public NamedPipeInterDaemonBus(string pipeName = "SentinelCore.Guard") => _pipeName = pipeName;

    public async ValueTask PublishAsync(InterDaemonEnvelope message, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Guard named pipe is Windows-only.");
        if (!InterDaemonEnvelopeValidator.TryValidate(message, DateTimeOffset.UtcNow, out var error))
            throw new InvalidOperationException(error);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await EnsureSessionAsync(message, timeout.Token);
                await WriteFrameAsync(_pipe!, message, timeout.Token);
                var ack = await ReadFrameAsync(_pipe!, timeout.Token);
                ValidateAck(ack, message);
            }
            catch
            {
                await ResetSessionAsync();
                throw;
            }
        }
        finally { _sessionGate.Release(); }
    }

    private async Task EnsureSessionAsync(InterDaemonEnvelope firstMessage, CancellationToken ct)
    {
        if (_pipe is { IsConnected: true } && _welcomed) return;
        await ResetSessionAsync();

        var candidate = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await candidate.ConnectAsync(2000, ct);
            var hello = new InterDaemonEnvelope(Guid.NewGuid(), firstMessage.CorrelationId, DateTimeOffset.UtcNow,
                "ThreatMonitor", "RiskResponseGuard", "HELLO", 1, firstMessage.ExpiresAtUtc,
                JsonSerializer.SerializeToElement(new { daemonId = "ThreatMonitor", capabilities = new[] { "findings", "context" }, supportedSchema = 1 }, JsonOptions));
            await WriteFrameAsync(candidate, hello, ct);
            var welcome = await ReadFrameAsync(candidate, ct);
            ValidateWelcome(welcome, firstMessage.CorrelationId);
            _pipe = candidate;
            _welcomed = true;
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }

    private static void ValidateWelcome(InterDaemonEnvelope welcome, string correlationId)
    {
        if (welcome.MessageType != "WELCOME" || welcome.SourceDaemon != "RiskResponseGuard" ||
            welcome.Destination != "ThreatMonitor" || welcome.SchemaVersion != 1 ||
            welcome.CorrelationId != correlationId || welcome.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            welcome.Payload.ValueKind != JsonValueKind.Object ||
            !welcome.Payload.TryGetProperty("sessionId", out var sessionId) ||
            sessionId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sessionId.GetString()) ||
            !welcome.Payload.TryGetProperty("supportedSchema", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion < 1)
            throw new InvalidDataException("Guard did not return a valid WELCOME for the v1 session.");
    }

    private static void ValidateAck(InterDaemonEnvelope ack, InterDaemonEnvelope sent)
    {
        if (ack.MessageType != "ACK" || ack.SourceDaemon != "RiskResponseGuard" || ack.Destination != "ThreatMonitor" ||
            ack.SchemaVersion != 1 || ack.CorrelationId != sent.CorrelationId || ack.Payload.ValueKind != JsonValueKind.Object ||
            !ack.Payload.TryGetProperty("messageId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(id.GetString(), out var ackId) || ackId != sent.MessageId)
            throw new InvalidDataException("Guard returned an invalid ACK.");
        var status = ack.Payload.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (status is not ("ACCEPTED" or "DUPLICATE"))
            throw new InvalidDataException($"Guard rejected message: {status ?? "unknown status"}.");
    }

    private async Task ResetSessionAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync();
        _pipe = null;
        _welcomed = false;
    }

    private static async Task WriteFrameAsync(Stream stream, InterDaemonEnvelope message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Message exceeds the common 64 KiB limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<InterDaemonEnvelope> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxMessageBytes) throw new InvalidDataException("Invalid frame length.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, ct);
        return JsonSerializer.Deserialize<InterDaemonEnvelope>(payload, JsonOptions)
            ?? throw new InvalidDataException("Guard returned invalid JSON.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], ct);
            if (count == 0) throw new EndOfStreamException("Guard closed the pipe before completing a frame.");
            offset += count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await ResetSessionAsync();
        }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
        }
    }
}
