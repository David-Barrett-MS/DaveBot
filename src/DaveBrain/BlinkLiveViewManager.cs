using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using DaveHome;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;

namespace DaveBrain;

public sealed record BlinkLiveViewOfferRequest(Guid DeviceId, string? Sdp);

public sealed record BlinkLiveViewStopRequest(string? SessionId, string? Reason);

public sealed record BlinkLiveViewOfferResult(
    bool Succeeded,
    string? SessionId = null,
    string? AnswerSdp = null,
    string? ErrorCode = null);

public sealed record BlinkLiveViewStopResult(
    bool Found,
    string? PeerConnectionState = null,
    int H264AccessUnits = 0);

public sealed class BlinkLiveViewManager(
    BlinkHubService blinkHubs,
    ILogger<BlinkLiveViewManager>? logger = null) : IAsyncDisposable
{
    private const int MaximumOfferLength = 128 * 1024;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, ActiveLiveView> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ViewerCamera, string> activeByViewerCamera = new();
    private readonly ILogger<BlinkLiveViewManager> logger = logger ?? NullLogger<BlinkLiveViewManager>.Instance;

    public async Task<BlinkLiveViewOfferResult> StartAsync(
        string authenticatedUserId,
        BlinkLiveViewOfferRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            request.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Sdp) ||
            request.Sdp.Length > MaximumOfferLength)
        {
            return new BlinkLiveViewOfferResult(false, ErrorCode: "invalid-request");
        }

        RTCPeerConnection? peerConnection = null;
        IBlinkLiveViewStream? stream = null;
        try
        {
            peerConnection = CreatePeerConnection();
            var remoteResult = peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = request.Sdp,
            });
            if (remoteResult != SetDescriptionResultEnum.OK)
            {
                peerConnection.Close("invalid offer");
                peerConnection.Dispose();
                return new BlinkLiveViewOfferResult(false, ErrorCode: "unsupported-browser");
            }

            var answer = peerConnection.createAnswer(new RTCAnswerOptions
            {
                X_WaitForIceGatheringToComplete = true,
            });
            if (string.IsNullOrWhiteSpace(answer?.sdp))
            {
                peerConnection.Close("answer unavailable");
                peerConnection.Dispose();
                return new BlinkLiveViewOfferResult(false, ErrorCode: "live-view-unavailable");
            }

            await peerConnection.setLocalDescription(answer);

            var existingKey = new ViewerCamera(authenticatedUserId, request.DeviceId);
            if (activeByViewerCamera.TryGetValue(existingKey, out var existingId) &&
                sessions.TryGetValue(existingId, out var existing))
            {
                await StopAsync(existing);
            }

            var liveView = await blinkHubs.StartLiveViewAsync(request.DeviceId, cancellationToken);
            if (!liveView.Succeeded || liveView.Stream is null)
            {
                peerConnection.Close("upstream unavailable");
                peerConnection.Dispose();
                return new BlinkLiveViewOfferResult(
                    false,
                    ErrorCode: ToErrorCode(liveView.Status));
            }

            stream = liveView.Stream;
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = Base64Url(RandomNumberGenerator.GetBytes(32));
            var lifetime = stream.Duration <= TimeSpan.Zero
                ? MaximumLifetime
                : TimeSpan.FromTicks(Math.Min(stream.Duration.Ticks, MaximumLifetime.Ticks));
            var active = new ActiveLiveView(
                sessionId,
                existingKey,
                peerConnection,
                stream,
                new CancellationTokenSource(lifetime));
            if (!sessions.TryAdd(sessionId, active))
            {
                await stream.DisposeAsync();
                peerConnection.Close("session creation failed");
                peerConnection.Dispose();
                return new BlinkLiveViewOfferResult(false, ErrorCode: "live-view-unavailable");
            }

            stream = null;
            activeByViewerCamera[existingKey] = sessionId;
            peerConnection.onconnectionstatechange += state =>
            {
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    _ = StopAsync(active);
                }
            };
            active.LifetimeCancellation.Token.Register(() => _ = StopAsync(active));
            active.PumpTask = PumpAsync(active);
            _ = StopIfNotConnectedAsync(active);
            return new BlinkLiveViewOfferResult(true, sessionId, answer.sdp);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            peerConnection?.Close("request cancelled");
            peerConnection?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning("Blink live view setup ended before a streaming session could be established.");
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            peerConnection?.Close("live view setup failed");
            peerConnection?.Dispose();
            return new BlinkLiveViewOfferResult(false, ErrorCode: "live-view-unavailable");
        }
    }

    public async Task<BlinkLiveViewStopResult> StopAsync(string authenticatedUserId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(authenticatedUserId) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !sessions.TryGetValue(sessionId, out var active) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(authenticatedUserId),
                System.Text.Encoding.UTF8.GetBytes(active.ViewerCamera.UserId)))
        {
            return new BlinkLiveViewStopResult(false);
        }

        var result = new BlinkLiveViewStopResult(
            true,
            active.PeerConnection.connectionState.ToString(),
            Volatile.Read(ref active.H264AccessUnits));
        await StopAsync(active);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(sessions.Values.Select(active => StopAsync(active)));
    }

    private static RTCPeerConnection CreatePeerConnection()
    {
        var peerConnection = new RTCPeerConnection(new RTCConfiguration());
        peerConnection.addTrack(new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            false,
            [new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                102,
                "H264",
                90_000,
                1,
                "packetization-mode=1;profile-level-id=42e01f")],
            MediaStreamStatusEnum.SendOnly));
        return peerConnection;
    }

    private async Task PumpAsync(ActiveLiveView active)
    {
        try
        {
            RuntimeAuditContext.Write(
                "Blink live view",
                "Media bridge",
                "Connecting to Blink IMMI stream.");
            await active.Stream.RunAsync(
                (accessUnit, _) =>
                {
                    if (accessUnit.Length != 0 && !active.IsStopped)
                    {
                        if (Interlocked.Increment(ref active.H264AccessUnits) == 1)
                        {
                            RuntimeAuditContext.Write(
                                "Blink live view",
                                "Media bridge",
                                "Received first H.264 access unit.");
                        }

                        active.PeerConnection.SendVideo(3_000, accessUnit.ToArray());
                    }

                    return ValueTask.CompletedTask;
                },
                active.LifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (active.IsStopped)
        {
        }
        catch (BlinkLiveViewTlsAuthenticationException exception)
        {
            logger.LogWarning("Blink live view TLS certificate validation failed.");
            RuntimeAuditContext.Write(
                "Blink live view",
                "Media bridge",
                $"TLS certificate validation failed: {exception.PolicyErrors}.");
        }
        catch (Exception exception) when (exception is AuthenticationException or EndOfStreamException or
            InvalidDataException or IOException or JsonException or SocketException)
        {
            logger.LogWarning("Blink live view stream ended unexpectedly.");
            RuntimeAuditContext.Write(
                "Blink live view",
                "Media bridge",
                $"Ended before media delivery: {exception.GetType().Name}.");
        }
        finally
        {
            await StopAsync(active);
        }
    }

    private async Task StopIfNotConnectedAsync(ActiveLiveView active)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), active.LifetimeCancellation.Token);
            if (!active.IsStopped &&
                active.PeerConnection.connectionState != RTCPeerConnectionState.connected)
            {
                await StopAsync(active);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task StopAsync(ActiveLiveView active)
    {
        if (Interlocked.Exchange(ref active.Stopped, 1) != 0)
        {
            return;
        }

        active.LifetimeCancellation.Cancel();
        try
        {
            await active.Stream.StopAsync();
        }
        finally
        {
            await active.Stream.DisposeAsync();
            active.PeerConnection.Close("live view stopped");
            active.PeerConnection.Dispose();
            sessions.TryRemove(new KeyValuePair<string, ActiveLiveView>(active.Id, active));
            activeByViewerCamera.TryRemove(
                new KeyValuePair<ViewerCamera, string>(active.ViewerCamera, active.Id));
            active.LifetimeCancellation.Dispose();
        }
    }

    private static string ToErrorCode(DeviceOperationStatus status) => status switch
    {
        DeviceOperationStatus.Unauthorized => "blink-authorization-failed",
        DeviceOperationStatus.ResourceNotFound => "camera-not-found",
        DeviceOperationStatus.TimedOut => "live-view-timeout",
        _ => "live-view-unavailable",
    };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ViewerCamera(string UserId, Guid DeviceId);

    private sealed class ActiveLiveView(
        string id,
        ViewerCamera viewerCamera,
        RTCPeerConnection peerConnection,
        IBlinkLiveViewStream stream,
        CancellationTokenSource lifetimeCancellation)
    {
        public string Id { get; } = id;
        public ViewerCamera ViewerCamera { get; } = viewerCamera;
        public RTCPeerConnection PeerConnection { get; } = peerConnection;
        public IBlinkLiveViewStream Stream { get; } = stream;
        public CancellationTokenSource LifetimeCancellation { get; } = lifetimeCancellation;
        public Task? PumpTask { get; set; }
        public int Stopped;
        public int H264AccessUnits;
        public bool IsStopped => Volatile.Read(ref Stopped) != 0;
    }
}
