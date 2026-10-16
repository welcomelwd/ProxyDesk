using System.Buffers.Binary;
using System.Text.Json;

namespace ProxyDesk.Core;

public sealed record RpcRequest(int Version, string Operation, JsonElement Payload)
{
    public static RpcRequest Create(string operation, object? payload = null) =>
        new(1, operation, JsonSerializer.SerializeToElement(payload, JsonDefaults.Options));
}
public sealed record RpcResponse(bool Success, JsonElement Payload, string? Error)
{
    public static RpcResponse Ok(object? payload) => new(true, JsonSerializer.SerializeToElement(payload, JsonDefaults.Options), null);
    public static RpcResponse Fail(string error) => new(false, JsonSerializer.SerializeToElement<object?>(null), error);
    public T Read<T>() => Success ? Payload.Deserialize<T>(JsonDefaults.Options)!
        : throw new InvalidOperationException(Error);
}
public sealed record ConfigurationUpdate(AppConfiguration Configuration, Dictionary<Guid, string> Passwords);
public sealed record ToggleRequest(bool Value);
public sealed record ProxyTestRequest(Guid ProxyId);
public sealed record ManagedRegistration(int Pid, string Path);
public sealed record ShutdownRequest(int ExpectedProcessId);

public static class FrameProtocol
{
    public const int MaxFrameLength = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxFrameLength) throw new InvalidDataException("Invalid IPC frame length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("Invalid IPC message.");
    }
}

public interface ITrafficBackend : IDisposable
{
    BackendCapabilities Capabilities { get; }
    event Action<ConnectionEvent>? Connection;
    event Action<string>? Log;
    void Apply(AppConfiguration configuration, Func<Guid, string> getPassword);
    void Start();
    void Stop();
}

public interface IPlatformIntegration
{
    string ConfigurationDirectory { get; }
    string? GetCurrentSsid();
    void SetLaunchAtLogin(bool enabled);
    byte[] ProtectSecret(byte[] value);
    byte[] UnprotectSecret(byte[] value);
}
