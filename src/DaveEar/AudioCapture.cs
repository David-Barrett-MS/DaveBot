using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DaveEar;

public static class AudioCaptureFactory
{
    public static IAudioCapture Create(string? device) =>
        OperatingSystem.IsWindows()
            ? new WindowsWasapiAudioCapture(device)
            : OperatingSystem.IsLinux()
                ? new LinuxAlsaAudioCapture(device)
                : throw new PlatformNotSupportedException("DaveEar supports Windows WASAPI and Linux ALSA/PipeWire capture only.");
}

public sealed class WindowsWasapiAudioCapture : IAudioCapture
{
    private readonly string? _deviceName;

    public WindowsWasapiAudioCapture(string? deviceName) => _deviceName = deviceName;

    public Task<AudioCaptureDiagnostic> DiagnoseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new AudioCaptureDiagnostic(false, "wasapi-not-supported-on-this-os"));
        }
        if (!Environment.UserInteractive)
        {
            return Task.FromResult(new AudioCaptureDiagnostic(false, "windows-service-session-audio-unavailable"));
        }

        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrWhiteSpace(_deviceName)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(candidate => string.Equals(candidate.FriendlyName, _deviceName, StringComparison.Ordinal));
        return Task.FromResult(device is null
            ? new AudioCaptureDiagnostic(false, "microphone-not-found")
            : new AudioCaptureDiagnostic(true, "wasapi-ready"));
    }

    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI capture is available only on Windows.");
        }
        if (!Environment.UserInteractive)
        {
            throw new InvalidOperationException("Windows Service Session 0 has no interactive microphone endpoint.");
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrWhiteSpace(_deviceName)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(candidate => string.Equals(candidate.FriendlyName, _deviceName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The configured Windows microphone is not available.");
        using var capture = new WasapiCapture(device);
        var channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        capture.DataAvailable += (_, eventArgs) =>
        {
            try
            {
                var frame = PcmFrameConverter.ToMono16Khz(
                    eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded),
                    capture.WaveFormat,
                    DateTimeOffset.UtcNow);
                channel.Writer.TryWrite(frame);
            }
            catch
            {
                channel.Writer.TryComplete(new InvalidOperationException("The Windows microphone returned an unsupported format."));
            }
        };
        capture.RecordingStopped += (_, eventArgs) => channel.Writer.TryComplete(eventArgs.Exception);
        capture.StartRecording();
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }
        finally
        {
            capture.StopRecording();
            channel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LinuxAlsaAudioCapture : IAudioCapture
{
    private readonly string? _device;

    public LinuxAlsaAudioCapture(string? device) => _device = device;

    public async Task<AudioCaptureDiagnostic> DiagnoseAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new AudioCaptureDiagnostic(false, "alsa-not-supported-on-this-os");
        }

        try
        {
            var info = new ProcessStartInfo("arecord") { RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-l");
            using var process = Process.Start(info) ?? throw new InvalidOperationException();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? new AudioCaptureDiagnostic(true, "alsa-ready")
                : new AudioCaptureDiagnostic(false, "microphone-not-found");
        }
        catch (Win32Exception)
        {
            return new AudioCaptureDiagnostic(false, "arecord-not-installed");
        }
    }

    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("ALSA/PipeWire capture is available only on Linux.");
        }

        var info = new ProcessStartInfo("arecord")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(_device))
        {
            info.ArgumentList.Add("-D");
            info.ArgumentList.Add(_device);
        }
        foreach (var argument in new[] { "--file-type=raw", "--format=S16_LE", "--channels=1", "--rate=16000" })
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start ALSA capture.");
        var buffer = new byte[3200]; // 100 ms: bounded latency and memory.
        try
        {
            while (true)
            {
                var read = await ReadFullFrameAsync(process.StandardOutput.BaseStream, buffer, cancellationToken);
                if (read == 0)
                {
                    throw new InvalidOperationException("ALSA microphone capture ended.");
                }

                yield return PcmFrameConverter.FromPcm16(buffer.AsSpan(0, read), DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<int> ReadFullFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return total;
            }
            total += read;
        }
        return total;
    }
}

internal static class PcmFrameConverter
{
    public static AudioFrame FromPcm16(ReadOnlySpan<byte> bytes, DateTimeOffset capturedUtc)
    {
        var samples = new float[bytes.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes.Slice(i * 2, 2)) / 32768f;
        }
        return new AudioFrame(samples, 16_000, capturedUtc);
    }

    public static AudioFrame ToMono16Khz(ReadOnlySpan<byte> bytes, WaveFormat format, DateTimeOffset capturedUtc)
    {
        if (format.Encoding != WaveFormatEncoding.Pcm && format.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            throw new NotSupportedException();
        }

        var bytesPerValue = format.BitsPerSample / 8;
        if (bytesPerValue is not (2 or 4) || format.Channels < 1)
        {
            throw new NotSupportedException();
        }

        var inputFrames = bytes.Length / (bytesPerValue * format.Channels);
        var mono = new float[inputFrames];
        for (var frame = 0; frame < inputFrames; frame++)
        {
            double mixed = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = (frame * format.Channels + channel) * bytesPerValue;
                mixed += format.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(bytes.Slice(offset, 4))
                    : BitConverter.ToInt16(bytes.Slice(offset, 2)) / 32768f;
            }
            mono[frame] = (float)(mixed / format.Channels);
        }

        return format.SampleRate == 16_000
            ? new AudioFrame(mono, 16_000, capturedUtc)
            : new AudioFrame(Resample(mono, format.SampleRate), 16_000, capturedUtc);
    }

    private static float[] Resample(float[] input, int sourceRate)
    {
        var length = checked((int)Math.Round(input.Length * 16_000d / sourceRate));
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * sourceRate / 16_000d;
            var low = Math.Min((int)position, input.Length - 1);
            var high = Math.Min(low + 1, input.Length - 1);
            output[i] = (float)(input[low] + (input[high] - input[low]) * (position - low));
        }
        return output;
    }
}
