namespace com.lifepixer.mangaplex.MediaWorker.Protocol;

using System.Text.Json;
using com.lifepixer.mangaplex.Core.WorkerProtocol;

/// <summary>
/// JSON-lines framing for worker IPC. Each message is a single JSON object
/// terminated by a newline. stdout is reserved for protocol messages only.
/// stderr is for diagnostics (drained and sanitized by the server).
/// </summary>
public static class WorkerProtocolFraming
{
    private const int MaxMessageBytes = 1024 * 1024; // 1 MiB per message

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads a single envelope from the stream. Returns null on EOF.
    /// Throws if the message is malformed or exceeds the size limit.
    /// </summary>
    public static async Task<WorkerEnvelope?> ReadEnvelopeAsync(Stream stream, CancellationToken ct = default)
    {
        var line = await ReadLineAsync(stream, ct);
        if (line is null)
            return null;

        if (line.Length > MaxMessageBytes)
            throw new InvalidDataException($"Message exceeds {MaxMessageBytes} bytes");

        return JsonSerializer.Deserialize<WorkerEnvelope>(line, s_jsonOptions)
            ?? throw new InvalidDataException("Failed to deserialize envelope");
    }

    /// <summary>
    /// Writes a single envelope to the stream as one JSON-lines message.
    /// </summary>
    public static async Task WriteEnvelopeAsync(Stream stream, WorkerEnvelope envelope, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(envelope, s_jsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > MaxMessageBytes)
            throw new InvalidDataException($"Serialized message exceeds {MaxMessageBytes} bytes");

        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Creates an envelope with a typed payload.
    /// </summary>
    public static WorkerEnvelope CreateEnvelope<T>(string type, string correlationId, T payload)
    {
        var json = JsonSerializer.SerializeToElement(payload, s_jsonOptions);
        return new WorkerEnvelope
        {
            Type = type,
            CorrelationId = correlationId,
            ProtocolVersion = WorkerProtocolVersion.Current,
            Payload = json,
        };
    }

    /// <summary>
    /// Deserializes the payload of an envelope to a specific type.
    /// </summary>
    public static T? GetPayload<T>(WorkerEnvelope envelope)
    {
        return JsonSerializer.Deserialize<T>(envelope.Payload, s_jsonOptions);
    }

    /// <summary>
    /// Maximum allowed message size in bytes.
    /// </summary>
    public static int MaxMessageSize => MaxMessageBytes;

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        // Simple line reader — reads until newline or EOF
        var buffer = new List<byte>();
        var oneByte = new byte[1];

        while (true)
        {
            int read = await stream.ReadAsync(oneByte, ct);
            if (read == 0)
            {
                // EOF
                return buffer.Count == 0 ? null : System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (oneByte[0] == '\n')
                return System.Text.Encoding.UTF8.GetString(buffer.ToArray());

            if (oneByte[0] != '\r')
                buffer.Add(oneByte[0]);

            if (buffer.Count > MaxMessageBytes)
                throw new InvalidDataException($"Message line exceeds {MaxMessageBytes} bytes before newline");
        }
    }
}
