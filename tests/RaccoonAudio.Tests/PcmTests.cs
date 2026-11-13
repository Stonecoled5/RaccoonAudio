using RaccoonAudio.Core;
using Xunit;

namespace RaccoonAudio.Tests;

public class PcmTests
{
	static short[] Convert(float[] samples, float quietLevel = 1)
	{
		var pcm = new byte[samples.Length * 2];
		Pcm.FromFloat(samples, pcm, quietLevel);
		var numbers = new short[samples.Length];
		for(var i = 0; i < samples.Length; i++) numbers[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
		return numbers;
	}

	[Fact]
	public void SoundThatIsntTurnedDownComesOutAsItWentIn()
	{
		Assert.Equal(new short[] { 0, 16384, -16384, 32767, -32767 }, Convert([0f, 0.5f, -0.5f, 1f, -1f]));
	}

	[Fact]
	public void SoundLouderThanPossibleIsHeldAtTheLoudestInsteadOfWrappingRound()
	{
		Assert.Equal(new short[] { 32767, -32767 }, Convert([1.7f, -3f]));
	}

	[Fact]
	public void AProgramTurnedDownIsTurnedBackUpByAsMuch()
	{
		// Turned down to a thousandth: what was 0.5 arrives as 0.0005
		Assert.Equal(new short[] { 16384, -16384, 0 }, Convert([0.0005f, -0.0005f, 0f], 0.001f));
	}

	[Fact]
	public void TurnedUpSoundIsAsCleanAsIfItHadNeverBeenTurnedDown()
	{
		var loud = new float[480];
		var faint = new float[480];
		for(var i = 0; i < loud.Length; i++)
		{
			loud[i] = MathF.Sin(2 * MathF.PI * 440 * i / 48000) * 0.8f;
			faint[i] = loud[i] * 0.001f;
		}
		var straight = Convert(loud);
		var turnedUp = Convert(faint, 0.001f);
		for(var i = 0; i < loud.Length; i++) Assert.InRange(turnedUp[i] - straight[i], -1, 1);
	}

	[Fact]
	public void SoundThatIsntActuallyFaintIsNeverTurnedUp()
	{
		// The app thinks the program is turned down, but its slider was moved back up: turned up a thousand times
		// this would be a roar
		Assert.Equal(new short[] { 16384, 33, -16384 }, Convert([0.5f, 0.001f, -0.5f], 0.001f));
	}

	[Fact]
	public void SoundOnlyALittleOverTheLevelStillCountsAsTurnedDown()
	{
		// Windows fades to a new volume, so the level isn't exact
		Assert.Equal(new short[] { 32767 }, Convert([0.00104f], 0.001f));
		Assert.Equal(new short[] { 36 }, Convert([0.0011f], 0.001f));
	}

	[Theory]
	[InlineData(0f)]
	[InlineData(-1f)]
	[InlineData(1f)]
	[InlineData(5f)]
	public void ALevelThatMakesNoSenseMeansNotTurnedDown(float level)
	{
		Assert.Equal(new short[] { 33 }, Convert([0.001f], level));
	}
}
