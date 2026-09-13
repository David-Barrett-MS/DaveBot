using System.Buffers.Binary;
using DaveEar;

namespace DaveEar.Tests;

public sealed class AudioPipelineTests
{
    [Fact]
    public void SegmenterIncludesPreRollAndEndsAfterOneSecondOfSilence()
    {
        var segmenter = CreateSegmenter(preRollMilliseconds: 200);
        AudioSegment? segment = null;

        foreach (var frame in Frames(2, 0).Concat(Frames(3, 0.1f)).Concat(Frames(10, 0)))
        {
            segment ??= segmenter.Push(frame);
        }

        Assert.NotNull(segment);
        Assert.False(segment!.WasForced);
        Assert.Equal(1_400 * 16, segment.Samples.Length);
        Assert.Equal(0, segment.Samples[0]);
    }

    [Fact]
    public void SegmenterDoesNotStartUntilSustainedOnset()
    {
        var segmenter = new AudioSegmenter(CreateConfiguration() with { OnsetMilliseconds = 200 }, new EnergyVoiceActivityDetector(100));

        foreach (var frame in Frames(1, 0.1f).Concat(Frames(1, 0)))
        {
            Assert.Null(segmenter.Push(frame));
        }
        Assert.Null(segmenter.Flush());
    }

    [Fact]
    public void SegmenterForcesContinuousSoundAtMaximumDuration()
    {
        var configuration = CreateConfiguration(maximumMilliseconds: 2_000);
        var segmenter = new AudioSegmenter(configuration, new EnergyVoiceActivityDetector(100));
        AudioSegment? segment = null;
        foreach (var frame in Frames(20, 0.1f))
        {
            segment ??= segmenter.Push(frame);
        }

        Assert.NotNull(segment);
        Assert.True(segment!.WasForced);
        Assert.Equal(32_000, segment.Samples.Length);
    }

    [Fact]
    public void EncoderProducesPhaseFourPcmWavContract()
    {
        var wav = new PcmWavEncoder().Encode(new AudioSegment(Guid.NewGuid(), new float[16_000], 16_000, DateTimeOffset.UtcNow, false));

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal((short)16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    private static AudioSegmenter CreateSegmenter(int preRollMilliseconds = 300) =>
        new(CreateConfiguration(preRollMilliseconds: preRollMilliseconds), new EnergyVoiceActivityDetector(100));

    private static DaveEarConfiguration CreateConfiguration(int preRollMilliseconds = 300, int maximumMilliseconds = 30_000) =>
        DaveEarConfiguration.Create(new Uri("https://brain.example.test"), "test", Guid.NewGuid(), Guid.NewGuid()) with
        {
            EnergyThreshold = 100,
            OnsetMilliseconds = 100,
            PreRollMilliseconds = preRollMilliseconds,
            MaximumSegmentMilliseconds = maximumMilliseconds,
        };

    private static IEnumerable<AudioFrame> Frames(int count, float amplitude) =>
        Enumerable.Range(0, count).Select(index =>
            new AudioFrame(Enumerable.Repeat(amplitude, 1_600).ToArray(), 16_000, DateTimeOffset.UnixEpoch.AddMilliseconds(index * 100)));
}
