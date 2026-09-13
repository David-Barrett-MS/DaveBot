using System.Buffers.Binary;

namespace DaveEar;

public sealed class EnergyVoiceActivityDetector(int threshold) : IVoiceActivityDetector
{
    private double _noiseFloor;

    public bool IsVoice(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Samples.Length == 0)
        {
            return false;
        }

        double sum = 0;
        foreach (var sample in frame.Samples)
        {
            sum += sample * sample;
        }

        var energy = Math.Sqrt(sum / frame.Samples.Length) * short.MaxValue;
        var effectiveThreshold = Math.Max(threshold, _noiseFloor * 2.5);
        if (energy < effectiveThreshold)
        {
            _noiseFloor = _noiseFloor == 0 ? energy : (_noiseFloor * 0.95) + (energy * 0.05);
        }

        return energy >= effectiveThreshold;
    }
}

public sealed class AudioSegmenter
{
    private readonly IVoiceActivityDetector _detector;
    private readonly int _sampleRate;
    private readonly int _onsetSamples;
    private readonly int _preRollSamples;
    private readonly int _silenceSamples;
    private readonly int _maximumSamples;
    private readonly Queue<float> _preRoll = [];
    private readonly List<float> _segment = [];
    private int _onsetCount;
    private int _silenceCount;
    private DateTimeOffset _startedUtc;

    public AudioSegmenter(DaveEarConfiguration configuration, IVoiceActivityDetector detector, int sampleRate = 16_000)
    {
        configuration.Validate();
        ArgumentNullException.ThrowIfNull(detector);
        if (sampleRate is < 8_000 or > 192_000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        _detector = detector;
        _sampleRate = sampleRate;
        _onsetSamples = MillisecondsToSamples(configuration.OnsetMilliseconds);
        _preRollSamples = MillisecondsToSamples(configuration.PreRollMilliseconds);
        _silenceSamples = MillisecondsToSamples(configuration.SilenceBoundaryMilliseconds);
        _maximumSamples = MillisecondsToSamples(configuration.MaximumSegmentMilliseconds);
    }

    public AudioSegment? Push(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != _sampleRate)
        {
            throw new InvalidOperationException("AudioSegmenter requires normalized 16 kHz mono frames.");
        }

        if (_segment.Count == 0)
        {
            AddPreRoll(frame.Samples);
            if (_detector.IsVoice(frame))
            {
                _onsetCount += frame.Samples.Length;
                if (_onsetCount >= _onsetSamples)
                {
                    _startedUtc = frame.CapturedUtc - TimeSpan.FromMilliseconds(
                        _preRoll.Count * 1000d / _sampleRate);
                    _segment.AddRange(_preRoll);
                    _preRoll.Clear();
                    _onsetCount = 0;
                }
            }
            else
            {
                _onsetCount = 0;
            }

            return null;
        }

        _segment.AddRange(frame.Samples);
        _silenceCount = _detector.IsVoice(frame) ? 0 : _silenceCount + frame.Samples.Length;
        if (_segment.Count >= _maximumSamples)
        {
            return Complete(forced: true);
        }

        return _silenceCount >= _silenceSamples ? Complete(forced: false) : null;
    }

    public AudioSegment? Flush()
    {
        if (_segment.Count == 0)
        {
            return null;
        }

        return Complete(forced: true);
    }

    private AudioSegment Complete(bool forced)
    {
        var completedSamples = forced && _segment.Count > _maximumSamples
            ? _segment.GetRange(0, _maximumSamples).ToArray()
            : _segment.ToArray();
        var result = new AudioSegment(Guid.NewGuid(), completedSamples, _sampleRate, _startedUtc, forced);
        if (forced && _segment.Count > _maximumSamples)
        {
            var overflow = _segment.GetRange(_maximumSamples, _segment.Count - _maximumSamples);
            _segment.Clear();
            _segment.AddRange(overflow);
            _startedUtc += TimeSpan.FromSeconds(completedSamples.Length / (double)_sampleRate);
        }
        else
        {
            _segment.Clear();
        }
        _silenceCount = 0;
        _onsetCount = 0;
        return result;
    }

    private void AddPreRoll(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _preRoll.Enqueue(sample);
        }

        while (_preRoll.Count > _preRollSamples)
        {
            _preRoll.Dequeue();
        }
    }

    private int MillisecondsToSamples(int milliseconds) => checked(_sampleRate * milliseconds / 1000);
}

public sealed class PcmWavEncoder : IAudioSegmentEncoder
{
    public byte[] Encode(AudioSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Samples.Length == 0 || segment.SampleRate <= 0)
        {
            throw new InvalidOperationException("An audio segment must contain samples.");
        }

        var samples = segment.SampleRate == 16_000
            ? segment.Samples
            : Resample(segment.Samples, segment.SampleRate, 16_000);
        var dataLength = checked(samples.Length * sizeof(short));
        var wav = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32_000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataLength);
        for (var index = 0; index < samples.Length; index++)
        {
            var pcm = (short)Math.Clamp(Math.Round(samples[index] * short.MaxValue), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + index * 2), pcm);
        }

        return wav;
    }

    private static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        var count = checked((int)Math.Round(input.Length * (double)targetRate / sourceRate));
        var result = new float[count];
        for (var destination = 0; destination < count; destination++)
        {
            var source = destination * (double)sourceRate / targetRate;
            var lower = Math.Min((int)source, input.Length - 1);
            var upper = Math.Min(lower + 1, input.Length - 1);
            result[destination] = (float)(input[lower] + (input[upper] - input[lower]) * (source - lower));
        }

        return result;
    }
}
