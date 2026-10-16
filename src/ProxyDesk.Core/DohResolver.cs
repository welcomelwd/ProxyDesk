using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace ProxyDesk.Core;

public sealed class DohResolver
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset StoredAt, DateTimeOffset Expiry, byte[] Response)> cache = new();

    public async Task<byte[]> ResolveAsync(byte[] query, int pid, Guid profileId, ProxyServer proxy,
        string password, Settings settings, CancellationToken token, string processIdentity = "", uint queryOptions = 0)
    {
        if (query.Length is < 12 or > 65535) throw new InvalidDataException("Invalid DNS message length.");
        var key = $"{pid}:{processIdentity}:{profileId}:{proxy.Id}:{proxy.Revision}:{Convert.ToHexString(query.AsSpan(2))}";
        var bypassCache = (queryOptions & (0x8 | 0x100)) != 0;
        if (!bypassCache && cache.TryGetValue(key, out var entry) && entry.Expiry > DateTimeOffset.UtcNow)
        {
            var cached = (byte[])entry.Response.Clone(); cached[0] = query[0]; cached[1] = query[1];
            DnsWire.AgeAnswerTtls(cached, (uint)(DateTimeOffset.UtcNow - entry.StoredAt).TotalSeconds); return cached;
        }
        if ((queryOptions & 0x10) != 0) throw new DnsCacheMissException();
        using var client = ProxyTunnel.CreateHttpClient(proxy, password, settings.DohBootstrapIp);
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.DohUrl) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/dns-message")
            throw new InvalidDataException("DoH server did not return a DNS wireformat response.");
        var wire = await response.Content.ReadAsByteArrayAsync(token);
        if (wire.Length is < 12 or > 65535 || (wire[2] & 0x80) == 0)
            throw new InvalidDataException("Invalid DoH response.");
        wire[0] = query[0]; wire[1] = query[1];
        // Respect the minimum answer TTL; negative answers are not cached here.
        var ttl = DnsWire.MinimumAnswerTtl(wire);
        if (ttl > 0)
        {
            if (cache.Count >= 4096) foreach (var item in cache.Where(x => x.Value.Expiry <= DateTimeOffset.UtcNow)) cache.TryRemove(item.Key, out _);
            if (cache.Count < 4096) cache[key] = (DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(ttl), (byte[])wire.Clone());
        }
        return wire;
    }
    public void Clear() => cache.Clear();
}

public sealed class DnsCacheMissException : Exception
{
    public DnsCacheMissException() : base("No application DNS cache entry is available.") { }
}

public static class DnsWire
{
    public static void AgeAnswerTtls(Span<byte> wire, uint seconds)
    {
        static int SkipName(ReadOnlySpan<byte> bytes, int offset)
        {
            while (offset < bytes.Length)
            {
                var length = bytes[offset++];
                if (length == 0) return offset;
                if ((length & 0xc0) == 0xc0 && offset < bytes.Length) return offset + 1;
                if (length > 63 || offset + length > bytes.Length) throw new InvalidDataException("Invalid DNS name.");
                offset += length;
            }
            throw new InvalidDataException("Truncated DNS name.");
        }
        var position = 12;
        var questions = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(4, 2));
        var answers = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(6, 2));
        for (var index = 0; index < questions; index++) position = SkipName(wire, position) + 4;
        for (var index = 0; index < answers; index++)
        {
            position = SkipName(wire, position);
            var ttl = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(wire.Slice(position + 4, 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wire.Slice(position + 4, 4), ttl > seconds ? ttl - seconds : 0);
            position += 10 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(wire.Slice(position + 8, 2));
        }
    }
    public static string QuestionName(ReadOnlySpan<byte> wire)
    {
        var position = 12; var labels = new List<string>();
        while (position < wire.Length)
        {
            var length = wire[position++];
            if (length == 0) return string.Join('.', labels);
            if (length > 63 || position + length > wire.Length) throw new InvalidDataException("Invalid DNS question.");
            labels.Add(System.Text.Encoding.ASCII.GetString(wire.Slice(position, length))); position += length;
        }
        throw new InvalidDataException("Truncated DNS question.");
    }
    public static uint MinimumAnswerTtl(ReadOnlySpan<byte> wire)
    {
        static ushort U16(ReadOnlySpan<byte> bytes, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
        static int SkipName(ReadOnlySpan<byte> bytes, int offset)
        {
            while (offset < bytes.Length)
            {
                var length = bytes[offset++];
                if (length == 0) return offset;
                if ((length & 0xc0) == 0xc0) { if (offset >= bytes.Length) throw new InvalidDataException("Invalid DNS pointer."); return offset + 1; }
                if (length > 63 || offset + length > bytes.Length) throw new InvalidDataException("Invalid DNS label.");
                offset += length;
            }
            throw new InvalidDataException("Truncated DNS name.");
        }
        var position = 12;
        var questions = U16(wire, 4); var answers = U16(wire, 6);
        for (var i = 0; i < questions; i++) position = SkipName(wire, position) + 4;
        uint minimum = uint.MaxValue;
        for (var i = 0; i < answers; i++)
        {
            position = SkipName(wire, position);
            if (position + 10 > wire.Length) throw new InvalidDataException("Truncated DNS record.");
            minimum = Math.Min(minimum, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(wire.Slice(position + 4, 4)));
            position += 10 + U16(wire, position + 8);
            if (position > wire.Length) throw new InvalidDataException("Truncated DNS record data.");
        }
        return minimum == uint.MaxValue ? 0 : minimum;
    }
}
