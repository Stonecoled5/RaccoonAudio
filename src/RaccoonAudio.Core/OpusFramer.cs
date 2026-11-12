using Concentus;
using Concentus.Enums;

namespace RaccoonAudio.Core;

// Turns captured sound into what the bot plays: 16-bit stereo PCM at 48 kHz in, Opus packets of exactly 20 ms out.
// Sound arrives from Windows in whatever sized pieces it likes, so this holds on to what's left over and hands out a
// packet each time it has a full 20 ms. The bot plays the packets as they are, without re-encoding.
public sealed class OpusFramer
{
	// Music at a rate where it sounds right and a packet stays far below the biggest the bot takes
	public const int BitsPerSecond = 128_000;

	readonly IOpusEncoder encoder;
	readonly short[] frame;
	readonly byte[] packet;
	int filled;
	byte? half;

	public OpusFramer()
	{
		encoder = OpusCodecFactory.CreateEncoder(Protocol.SampleRate, Protocol.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
		encoder.Bitrate = BitsPerSecond;
		encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
		encoder.UseVBR = true;
		frame = new short[Protocol.SamplesPerFrame * Protocol.Channels];
		packet = new byte[Protocol.MaxAudioFrameBytes];
	}

	// Takes sound (little-endian 16-bit samples, left then right) and calls back once for every 20 ms that fills up
	public void Push(ReadOnlySpan<byte> pcm, Action<ReadOnlyMemory<byte>> onPacket)
	{
		var at = 0;
		// A sample split across two pieces: its first byte was kept from last time
		if(half is byte low && pcm.Length > 0)
		{
			frame[filled++] = (short)(low | (pcm[0] << 8));
			half = null;
			at = 1;
			if(filled == frame.Length) Encode(onPacket);
		}
		for(; at + 1 < pcm.Length; at += 2)
		{
			frame[filled++] = (short)(pcm[at] | (pcm[at + 1] << 8));
			if(filled == frame.Length) Encode(onPacket);
		}
		if(at < pcm.Length) half = pcm[at];
	}

	void Encode(Action<ReadOnlyMemory<byte>> onPacket)
	{
		filled = 0;
		var length = encoder.Encode(frame, Protocol.SamplesPerFrame, packet, packet.Length);
		// A copy, since the packet waits in a queue while this buffer is reused for the next one
		if(length > 0) onPacket(packet.AsSpan(0, length).ToArray());
	}

	// Forgets the part-filled frame: the next stream starts clean
	public void Reset()
	{
		filled = 0;
		half = null;
		encoder.ResetState();
	}
}
