using ZeppieSays.Engines;
using ZeppieSays.Notes;

namespace ZeppieSays.Tests;

public class EchoTests
{
    const int Rate = Audio.SampleRate;

    /// <summary>Noise at an average loudness of about <paramref name="level"/>, in [from, to) seconds.</summary>
    static void Burst(float[] track, double from, double to, float level, int seed)
    {
        var random = new Random(seed);
        for (var i = (int)(from * Rate); i < (int)(to * Rate); i++)
            track[i] += (float)(random.NextDouble() * 2 - 1) * level * 1.7f;
    }

    [Theory]
    [InlineData(0.15f)] // headphones-ish: a little leaks into the microphone
    [InlineData(0.45f)] // open speakers
    [InlineData(0.9f)]  // loud speakers close to the microphone
    public void What_the_speakers_play_is_not_the_user_however_loud_it_reaches_the_microphone(float leak)
    {
        var call = new float[10 * Rate];
        var mic = new float[10 * Rate];
        Burst(call, 1, 4, 0.1f, 1);                       // the other person talks
        for (var i = 1 * Rate; i < 4 * Rate; i++)         // and the microphone hears it, a little late
            mic[i + 800] += call[i] * leak;
        Burst(mic, 6, 8, 0.08f, 2);                       // then the user talks, the call is silent

        var regions = AudioFile.SpeechRegions(mic, call);

        Assert.Single(regions);
        Assert.InRange(regions[0].Start, 5.5 * Rate, 6.1 * Rate);
        Assert.InRange(regions[0].End, 7.9 * Rate, 8.5 * Rate);
    }

    [Fact]
    public void The_user_talking_over_the_other_person_still_counts()
    {
        var call = new float[10 * Rate];
        var mic = new float[10 * Rate];
        Burst(call, 1, 4, 0.05f, 1);
        Burst(mic, 2, 3, 0.12f, 3); // clearly louder than anything the call leaks into the microphone
        var regions = AudioFile.SpeechRegions(mic, call);
        Assert.Single(regions);
    }
}
