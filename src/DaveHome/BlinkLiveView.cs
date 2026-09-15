using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DaveHome;

public sealed record BlinkLiveViewStartResult(
    DeviceOperationStatus Status,
    IBlinkLiveViewStream? Stream = null,
    string? Detail = null)
{
    public bool Succeeded => Status == DeviceOperationStatus.Succeeded;
}

/// <summary>Represents an in-memory Blink live view which must be stopped when no longer displayed.</summary>
public interface IBlinkLiveViewStream : IAsyncDisposable
{
    TimeSpan Duration { get; }

    Task RunAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onH264AccessUnit,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}

internal sealed record BlinkImmiEndpoint(string Host, int Port, uint ClientId, string ConnectionId);

public sealed class BlinkLiveViewTlsAuthenticationException(
    SslPolicyErrors policyErrors,
    AuthenticationException innerException) : AuthenticationException(
        "The Blink live view TLS certificate could not be validated.",
        innerException)
{
    public SslPolicyErrors PolicyErrors { get; } = policyErrors;
}

internal static class BlinkLiveViewProtocol
{
    internal const int GreetingLength = 122;
    internal const int MaximumFramePayloadBytes = 1024 * 1024;
    private const SslPolicyErrors AllowedImmiCertificateErrors =
        SslPolicyErrors.RemoteCertificateNameMismatch |
        SslPolicyErrors.RemoteCertificateChainErrors;

    internal static bool TryParseImmiEndpoint(string? server, out BlinkImmiEndpoint endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "immis", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Port is not -1 and not 443) ||
            Uri.CheckHostName(uri.Host) == UriHostNameType.Unknown)
        {
            return false;
        }

        var finalPathSegment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrEmpty(finalPathSegment))
        {
            return false;
        }

        string connectionId;
        try
        {
            connectionId = Uri.UnescapeDataString(finalPathSegment);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var separator = connectionId.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 0 ||
            connectionId.IndexOf("__", separator + 2, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        connectionId = connectionId[..separator];
        if (!IsConnectionId(connectionId) ||
            !connectionId.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return false;
        }

        uint clientId = 0;
        var clientIdCount = 0;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var valueSeparator = pair.IndexOf('=');
            var encodedName = valueSeparator < 0 ? pair : pair[..valueSeparator];
            if (!string.Equals(Uri.UnescapeDataString(encodedName), "client_id", StringComparison.Ordinal))
            {
                continue;
            }

            clientIdCount++;
            var encodedValue = valueSeparator < 0 ? string.Empty : pair[(valueSeparator + 1)..];
            if (!uint.TryParse(
                    Uri.UnescapeDataString(encodedValue),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out clientId) ||
                clientId == 0)
            {
                return false;
            }
        }

        if (clientIdCount != 1)
        {
            return false;
        }

        endpoint = new BlinkImmiEndpoint(uri.Host, uri.IsDefaultPort ? 443 : uri.Port, clientId, connectionId);
        return true;
    }

    internal static bool TryCreateGreeting(
        string? cameraSerial,
        BlinkImmiEndpoint endpoint,
        out byte[] greeting)
    {
        greeting = Array.Empty<byte>();
        if (!IsHandshakeValue(cameraSerial) || !IsConnectionId(endpoint.ConnectionId))
        {
            return false;
        }

        greeting = new byte[GreetingLength];
        var bytes = greeting.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 40);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[4..], 16);
        WritePaddedUtf8(cameraSerial!, bytes.Slice(8, 16));
        BinaryPrimitives.WriteUInt32BigEndian(bytes[24..], endpoint.ClientId);
        bytes[28] = 1;
        bytes[29] = 8;
        BinaryPrimitives.WriteUInt32BigEndian(bytes[30..], 64);
        BinaryPrimitives.WriteUInt32BigEndian(bytes[98..], 16);
        WritePaddedUtf8(endpoint.ConnectionId, bytes.Slice(102, 16));
        BinaryPrimitives.WriteUInt32BigEndian(bytes[118..], 1);
        return true;
    }

    internal static bool TryExtractTransportStreamPayload(
        byte frameType,
        ReadOnlyMemory<byte> payload,
        out ReadOnlyMemory<byte> transportStream)
    {
        transportStream = ReadOnlyMemory<byte>.Empty;
        if (frameType != 0x00 || payload.IsEmpty || payload.Span[0] != 0x47)
        {
            return false;
        }

        transportStream = payload;
        return true;
    }

    internal static bool IsAcceptedImmiCertificate(
        X509Certificate? certificate,
        SslPolicyErrors policyErrors,
        DateTimeOffset utcNow)
    {
        if (certificate is null || (policyErrors & ~AllowedImmiCertificateErrors) != 0)
        {
            return false;
        }

        try
        {
            using var leaf = new X509Certificate2(certificate);
            var now = utcNow.UtcDateTime;
            if (leaf.NotBefore.ToUniversalTime() > now || leaf.NotAfter.ToUniversalTime() < now)
            {
                return false;
            }

            return leaf.Extensions
                .OfType<X509EnhancedKeyUsageExtension>()
                .All(extension => extension.EnhancedKeyUsages.Cast<Oid>()
                    .Any(usage => usage.Value == "1.3.6.1.5.5.7.3.1"));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal static async Task ReceiveAsync(
        BlinkImmiEndpoint endpoint,
        string cameraSerial,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onH264AccessUnit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onH264AccessUnit);
        if (!TryCreateGreeting(cameraSerial, endpoint, out var greeting))
        {
            throw new InvalidOperationException("The Blink live view handshake details are invalid.");
        }

        using var client = new TcpClient();
        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            try
            {
                ((TcpClient)state!).Close();
            }
            catch (SocketException)
            {
            }
        }, client);

        await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
        SslPolicyErrors certificatePolicyErrors = SslPolicyErrors.None;
        await using var tlsStream = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, errors) =>
            {
                certificatePolicyErrors = errors;
                return IsAcceptedImmiCertificate(certificate, errors, DateTimeOffset.UtcNow);
            });
        try
        {
            await tlsStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = endpoint.Host },
                cancellationToken);
        }
        catch (AuthenticationException exception)
            when (certificatePolicyErrors != SslPolicyErrors.None)
        {
            throw new BlinkLiveViewTlsAuthenticationException(certificatePolicyErrors, exception);
        }

        await tlsStream.WriteAsync(greeting, cancellationToken);
        await tlsStream.FlushAsync(cancellationToken);

        using var keepAliveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAliveTask = SendKeepAlivesAsync(tlsStream, keepAliveCancellation.Token);
        var demuxer = new MpegTsH264Demuxer();
        try
        {
            var header = new byte[9];
            while (true)
            {
                await ReadExactlyAsync(tlsStream, header, cancellationToken);
                var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
                if (payloadLength > MaximumFramePayloadBytes)
                {
                    throw new InvalidDataException("The Blink live view frame is too large.");
                }

                var payload = new byte[payloadLength];
                await ReadExactlyAsync(tlsStream, payload, cancellationToken);
                if (TryExtractTransportStreamPayload(header[0], payload, out var transportStream))
                {
                    demuxer.Push(
                        transportStream.Span,
                        accessUnit => onH264AccessUnit(accessUnit, cancellationToken));
                }
            }
        }
        finally
        {
            keepAliveCancellation.Cancel();
            try
            {
                await keepAliveTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await demuxer.FlushAsync(
                    accessUnit => onH264AccessUnit(accessUnit, CancellationToken.None));
            }
        }
    }

    private static async Task SendKeepAlivesAsync(Stream stream, CancellationToken cancellationToken)
    {
        uint commandSequence = 0;
        for (var second = 0; ; second++)
        {
            if (second % 10 == 0)
            {
                await WriteFrameAsync(stream, 0x0A, ++commandSequence, Array.Empty<byte>(), cancellationToken);
            }

            await WriteFrameAsync(stream, 0x12, 1000, new byte[24], cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte type,
        uint sequence,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[9];
        header[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken);
        if (payload.Length != 0)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The Blink live view ended unexpectedly.");
            }

            offset += read;
        }
    }

    private static bool IsHandshakeValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('\0') &&
        Encoding.UTF8.GetByteCount(value) <= 256;

    private static bool IsConnectionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128;

    private static void WritePaddedUtf8(string value, Span<byte> destination)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        encoder.Convert(
            value.AsSpan(),
            destination,
            flush: true,
            out _,
            out _,
            out _);
    }
}

internal sealed class BlinkLiveViewStream(
    BlinkImmiEndpoint endpoint,
    string cameraSerial,
    TimeSpan duration,
    TimeSpan pollingInterval,
    Func<CancellationToken, Task<bool>> isActiveAsync,
    Func<CancellationToken, Task> completeAsync) : IBlinkLiveViewStream
{
    private readonly CancellationTokenSource stopCancellation = new();
    private readonly object completionLock = new();
    private Task? completionTask;

    public TimeSpan Duration { get; } = duration;

    public async Task RunAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onH264AccessUnit,
        CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            stopCancellation.Token);
        var receiveTask = BlinkLiveViewProtocol.ReceiveAsync(
            endpoint,
            cameraSerial,
            onH264AccessUnit,
            linkedCancellation.Token);
        var monitorTask = MonitorCommandAsync(linkedCancellation.Token);
        try
        {
            await await Task.WhenAny(receiveTask, monitorTask);
        }
        finally
        {
            linkedCancellation.Cancel();
            try
            {
                await Task.WhenAll(receiveTask, monitorTask);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
            }

            await CompleteAsync();
        }
    }

    public Task StopAsync()
    {
        stopCancellation.Cancel();
        return CompleteAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private Task CompleteAsync()
    {
        lock (completionLock)
        {
            return completionTask ??= CompleteCoreAsync();
        }
    }

    private async Task CompleteCoreAsync()
    {
        stopCancellation.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await completeAsync(timeout.Token);
        }
        catch (HttpRequestException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MonitorCommandAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(pollingInterval, cancellationToken);
            if (!await isActiveAsync(cancellationToken))
            {
                return;
            }
        }
    }
}

internal sealed class MpegTsH264Demuxer
{
    private const int PacketLength = 188;
    private const int MaximumPesPayloadBytes = 4 * 1024 * 1024;
    private readonly List<byte> bufferedTransportStream = [];
    private readonly PsiSectionAssembler patSections = new();
    private readonly PsiSectionAssembler pmtSections = new();
    private readonly H264AccessUnitAssembler accessUnits = new();
    private readonly List<byte> pesPayload = [];
    private int? pmtPid;
    private int? h264Pid;

    internal void Push(ReadOnlySpan<byte> data, Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        foreach (var value in data)
        {
            bufferedTransportStream.Add(value);
        }

        while (bufferedTransportStream.Count >= PacketLength)
        {
            if (bufferedTransportStream[0] != 0x47)
            {
                var nextSync = bufferedTransportStream.FindIndex(1, value => value == 0x47);
                if (nextSync < 0)
                {
                    bufferedTransportStream.RemoveRange(0, bufferedTransportStream.Count - (PacketLength - 1));
                    return;
                }

                bufferedTransportStream.RemoveRange(0, nextSync);
                if (bufferedTransportStream.Count < PacketLength)
                {
                    return;
                }
            }

            var packet = bufferedTransportStream.GetRange(0, PacketLength).ToArray();
            bufferedTransportStream.RemoveRange(0, PacketLength);
            ProcessPacket(packet, onAccessUnit);
        }
    }

    internal ValueTask FlushAsync(Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        FlushPes(onAccessUnit);
        return accessUnits.FlushAsync(onAccessUnit);
    }

    private void ProcessPacket(byte[] packet, Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        if ((packet[1] & 0x80) != 0)
        {
            return;
        }

        var payloadUnitStart = (packet[1] & 0x40) != 0;
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationFieldControl = (packet[3] >> 4) & 0x03;
        if (adaptationFieldControl is 0 or 2)
        {
            return;
        }

        var offset = 4;
        if (adaptationFieldControl == 3)
        {
            if (offset >= PacketLength)
            {
                return;
            }

            offset += packet[offset] + 1;
        }

        if (offset >= PacketLength)
        {
            return;
        }

        var payload = packet.AsSpan(offset);
        if (pid == 0)
        {
            patSections.Push(payload, payloadUnitStart, ParsePat);
        }
        else if (pmtPid == pid)
        {
            pmtSections.Push(payload, payloadUnitStart, ParsePmt);
        }
        else if (h264Pid == pid)
        {
            PushPes(payload, payloadUnitStart, onAccessUnit);
        }
    }

    private void ParsePat(ReadOnlySpan<byte> section)
    {
        if (section.Length < 12 || section[0] != 0x00)
        {
            return;
        }

        var end = section.Length - 4;
        for (var offset = 8; offset + 4 <= end; offset += 4)
        {
            var programNumber = BinaryPrimitives.ReadUInt16BigEndian(section.Slice(offset, 2));
            if (programNumber != 0)
            {
                pmtPid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];
                return;
            }
        }
    }

    private void ParsePmt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16 || section[0] != 0x02)
        {
            return;
        }

        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        var end = section.Length - 4;
        for (var offset = 12 + programInfoLength; offset + 5 <= end;)
        {
            var streamType = section[offset];
            var elementaryPid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var descriptorLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            if (streamType == 0x1B)
            {
                h264Pid = elementaryPid;
                return;
            }

            offset += 5 + descriptorLength;
        }
    }

    private void PushPes(
        ReadOnlySpan<byte> payload,
        bool payloadUnitStart,
        Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        if (payloadUnitStart)
        {
            FlushPes(onAccessUnit);
            if (payload.Length < 9 ||
                payload[0] != 0 ||
                payload[1] != 0 ||
                payload[2] != 1)
            {
                return;
            }

            var headerLength = payload[8];
            var payloadOffset = 9 + headerLength;
            if (payloadOffset > payload.Length)
            {
                return;
            }

            AppendPesPayload(payload[payloadOffset..]);
        }
        else if (pesPayload.Count != 0)
        {
            AppendPesPayload(payload);
        }
    }

    private void AppendPesPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPesPayloadBytes - pesPayload.Count)
        {
            throw new InvalidDataException("The Blink live view video sample is too large.");
        }

        pesPayload.AddRange(payload.ToArray());
    }

    private void FlushPes(Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        if (pesPayload.Count == 0)
        {
            return;
        }

        accessUnits.PushPes(CollectionsMarshal.AsSpan(pesPayload), onAccessUnit);
        pesPayload.Clear();
    }

    private sealed class PsiSectionAssembler
    {
        private readonly List<byte> pending = [];

        public void Push(ReadOnlySpan<byte> payload, bool payloadUnitStart, Action<ReadOnlySpan<byte>> onSection)
        {
            if (payloadUnitStart)
            {
                if (payload.Length == 0)
                {
                    return;
                }

                var pointer = payload[0];
                if (pointer > payload.Length - 1)
                {
                    pending.Clear();
                    return;
                }

                if (pointer != 0 && pending.Count != 0)
                {
                    pending.AddRange(payload.Slice(1, pointer).ToArray());
                    Drain(onSection);
                }

                pending.Clear();
                payload = payload[(pointer + 1)..];
            }

            pending.AddRange(payload.ToArray());
            Drain(onSection);
        }

        private void Drain(Action<ReadOnlySpan<byte>> onSection)
        {
            while (pending.Count >= 3)
            {
                if (pending[0] == 0xFF)
                {
                    pending.Clear();
                    return;
                }

                var sectionLength = ((pending[1] & 0x0F) << 8) | pending[2];
                var totalLength = 3 + sectionLength;
                if (sectionLength < 4 || totalLength > 4_096)
                {
                    pending.Clear();
                    return;
                }

                if (pending.Count < totalLength)
                {
                    return;
                }

                onSection(CollectionsMarshal.AsSpan(pending).Slice(0, totalLength));
                pending.RemoveRange(0, totalLength);
            }
        }
    }
}

internal sealed class H264AccessUnitAssembler
{
    private const int MaximumAccessUnitBytes = 4 * 1024 * 1024;
    private readonly List<byte[]> current = [];
    private byte[]? sequenceParameterSet;
    private byte[]? pictureParameterSet;
    private bool containsVcl;
    private int currentLength;

    internal void PushPes(ReadOnlySpan<byte> pes, Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        foreach (var nal in ExtractNals(pes.ToArray()))
        {
            var type = nal[0] & 0x1F;
            if ((type == 9 || (IsVcl(type) && IsFirstSlice(nal) && containsVcl)))
            {
                FlushAsync(onAccessUnit).GetAwaiter().GetResult();
            }

            if (type == 7)
            {
                sequenceParameterSet = nal;
            }
            else if (type == 8)
            {
                pictureParameterSet = nal;
            }

            if (nal.Length > MaximumAccessUnitBytes - currentLength)
            {
                throw new InvalidDataException("The Blink live view access unit is too large.");
            }

            current.Add(nal);
            currentLength += nal.Length;
            containsVcl |= IsVcl(type);
        }

        if (containsVcl)
        {
            FlushAsync(onAccessUnit).GetAwaiter().GetResult();
        }
    }

    internal ValueTask FlushAsync(Func<ReadOnlyMemory<byte>, ValueTask> onAccessUnit)
    {
        if (!containsVcl)
        {
            return ValueTask.CompletedTask;
        }

        var hasSps = current.Any(nal => (nal[0] & 0x1F) == 7);
        var hasPps = current.Any(nal => (nal[0] & 0x1F) == 8);
        var needsParameters = current.Any(nal => (nal[0] & 0x1F) == 5);
        var nals = new List<byte[]>(current.Count + 2);
        if (needsParameters && !hasSps && sequenceParameterSet is not null)
        {
            nals.Add(sequenceParameterSet);
        }

        if (needsParameters && !hasPps && pictureParameterSet is not null)
        {
            nals.Add(pictureParameterSet);
        }

        nals.AddRange(current);
        var length = nals.Sum(nal => nal.Length + 4);
        var accessUnit = new byte[length];
        var offset = 0;
        foreach (var nal in nals)
        {
            accessUnit[offset + 3] = 1;
            offset += 4;
            nal.CopyTo(accessUnit, offset);
            offset += nal.Length;
        }

        current.Clear();
        currentLength = 0;
        containsVcl = false;
        return onAccessUnit(accessUnit);
    }

    private static IEnumerable<byte[]> ExtractNals(byte[] data)
    {
        var starts = new List<(int Position, int Length)>();
        for (var index = 0; index + 3 < data.Length; index++)
        {
            if (data[index] == 0 && data[index + 1] == 0 &&
                data[index + 2] == 1)
            {
                starts.Add((index, 3));
                index += 2;
            }
            else if (index + 4 < data.Length &&
                     data[index] == 0 && data[index + 1] == 0 &&
                     data[index + 2] == 0 && data[index + 3] == 1)
            {
                starts.Add((index, 4));
                index += 3;
            }
        }

        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index].Position + starts[index].Length;
            var end = index + 1 < starts.Count ? starts[index + 1].Position : data.Length;
            if (end > start)
            {
                yield return data.AsSpan(start, end - start).ToArray();
            }
        }
    }

    private static bool IsVcl(int type) => type is >= 1 and <= 5;

    private static bool IsFirstSlice(byte[] nal)
    {
        var bits = new List<bool>();
        for (var index = 1; index < nal.Length; index++)
        {
            if (index >= 3 && nal[index - 2] == 0 && nal[index - 1] == 0 && nal[index] == 3)
            {
                continue;
            }

            for (var bit = 7; bit >= 0; bit--)
            {
                bits.Add((nal[index] & (1 << bit)) != 0);
            }
        }

        var leadingZeros = 0;
        while (leadingZeros < bits.Count && !bits[leadingZeros])
        {
            leadingZeros++;
        }

        if (leadingZeros >= bits.Count || leadingZeros > 23 || bits.Count < (leadingZeros * 2) + 1)
        {
            return false;
        }

        var value = 0;
        for (var index = 1; index <= leadingZeros; index++)
        {
            value = (value << 1) | (bits[leadingZeros + index] ? 1 : 0);
        }

        return ((1 << leadingZeros) - 1 + value) == 0;
    }
}
