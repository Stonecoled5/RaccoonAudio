namespace RaccoonAudio.Core;

// Turning captured sound (fractions from -1 to 1) into the 16-bit numbers that get encoded.
//
// While live, the app can turn the streamed program down to almost nothing on the streamer's own computer, so they
// hear the music once (from raccoon man) instead of twice. Windows hands over a program's sound after its volume, so
// what's captured is that much fainter too, and it's turned back up here by exactly as much.
public static class Pcm
{
	// quietLevel is the volume the program has been turned down to (1 = it hasn't been).
	//
	// Sound is only turned up when it really is that faint. Anything louder means the program isn't turned down
	// right now (its slider was moved, or the change hasn't taken effect yet), and turning that up a thousand times
	// would be a roar in everyone's ears, so it's passed on as it is.
	public static void FromFloat(ReadOnlySpan<float> samples, Span<byte> pcm, float quietLevel = 1)
	{
		var gain = 1f;
		if(quietLevel is > 0 and < 1)
		{
			var peak = 0f;
			foreach(var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
			// A little headroom, since Windows fades to a new volume rather than jumping
			if(peak <= quietLevel * 1.05f) gain = 1 / quietLevel;
		}
		for(var i = 0; i < samples.Length; i++)
		{
			var value = (int)MathF.Round(Math.Clamp(samples[i] * gain, -1f, 1f) * 32767f);
			pcm[i * 2] = (byte)(value & 0xff);
			pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
		}
	}
}
