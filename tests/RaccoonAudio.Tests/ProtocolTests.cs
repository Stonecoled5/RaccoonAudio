using System.Text.Json;
using RaccoonAudio.Core;

namespace RaccoonAudio.Tests;

// The protocol as the app reads it from its copy of the bot's file, what it will and won't send, what it accepts
// from the bot, and Opus framing.
public class ProtocolTests
{
	static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	[Fact]
	public void ItReadsEverythingFromTheBotsFile()
	{
		Assert.Equal(2, Protocol.Version);
		Assert.Equal("/stream", Protocol.Path);
		Assert.Equal((48000, 2, 20), (Protocol.SampleRate, Protocol.Channels, Protocol.FrameMs));
		Assert.Equal(960, Protocol.SamplesPerFrame);
		Assert.True(Protocol.MaxAudioFrameBytes >= 1275, "room for the biggest Opus packet");
		Assert.Equal(["clear", "done", "hello", "jam", "live", "now", "pair", "stop"], Protocol.Client.Keys.Order());
		Assert.Equal(["ended", "error", "live", "notify", "paired", "pause", "request", "resume", "skip", "welcome"], Protocol.Server.Keys.Order());
		foreach(var code in new[] { "update", "bad-token", "bad-code", "too-many-devices", "too-many-tries", "replaced", "unavailable", "busy" }) Assert.Contains(code, Protocol.Errors.Keys);
	}

	// The app's repository carries a copy of the bot's file. With the bot's repository next to this one (or its path
	// in RACCOONBOT_PROTOCOL), the two have to be the same, byte for byte apart from line endings.
	[Fact]
	public void TheCopyMatchesTheBotsWhenTheBotIsAround()
	{
		var root = FindUp("RaccoonAudio.sln");
		var mine = Path.Combine(root, "protocol", "stream-protocol.json");
		var theirs = Environment.GetEnvironmentVariable("RACCOONBOT_PROTOCOL") ?? Path.Combine(root, "..", "RaccoonBot", "protocol", "stream-protocol.json");
		Assert.True(File.Exists(mine));
		if(!File.Exists(theirs)) return;
		static string Clean(string path) => File.ReadAllText(path).Replace("\r\n", "\n").Trim();
		Assert.True(Clean(mine) == Clean(theirs), "protocol/stream-protocol.json differs from the bot's copy at " + Path.GetFullPath(theirs) + ". Copy the bot's file over it.");
	}

	static string FindUp(string file)
	{
		var folder = new DirectoryInfo(AppContext.BaseDirectory);
		while(folder != null && !File.Exists(Path.Combine(folder.FullName, file))) folder = folder.Parent;
		return folder?.FullName ?? throw new InvalidOperationException("couldn't find " + file);
	}

	[Fact]
	public void EveryMessageItSendsIsExactlyOneTheProtocolLists()
	{
		var sent = new[] { Protocol.Hello("key"), Protocol.Pair("abc-234", "Den PC"), Protocol.Live(), Protocol.Stop(), Protocol.Now("Song", "Band", true), Protocol.Jam("https://spotify.link/x"), Protocol.Done(7), Protocol.Clear() };
		Assert.Equal(Protocol.Client.Keys.Order(), sent.Select((m) => Json(m).GetProperty("type").GetString()!).Order());
		foreach(var message in sent)
		{
			var json = Json(message);
			var shape = Protocol.Client[json.GetProperty("type").GetString()!];
			Assert.Equal(shape.Keys.Order(), json.EnumerateObject().Select((p) => p.Name).Where((n) => n != "type").Order());
			foreach(var (name, kind) in shape)
			{
				var value = json.GetProperty(name);
				Assert.Equal(kind, value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", _ => "boolean" });
			}
		}
		Assert.Equal("{\"type\":\"hello\",\"v\":2,\"token\":\"key\"}", sent[0]);
		Assert.Equal("{\"type\":\"live\"}", sent[2]);
	}

	[Fact]
	public void ItWillNotBuildAMessageTheBotWouldRefuse()
	{
		Assert.Throws<ArgumentException>(() => Protocol.Build("shutdown"));
		Assert.Throws<ArgumentException>(() => Protocol.Build("welcome", ("v", 1)));
		Assert.Throws<ArgumentException>(() => Protocol.Build("hello", ("v", 1)));
		Assert.Throws<ArgumentException>(() => Protocol.Build("hello", ("v", "1"), ("token", "key")));
		Assert.Throws<ArgumentException>(() => Protocol.Build("hello", ("v", 1), ("key", "key")));
		Assert.Throws<ArgumentException>(() => Protocol.Build("live", ("loud", true)));
		Assert.Throws<ArgumentException>(() => Protocol.Build("jam", ("url", new string('x', Protocol.LimitFor("url") + 1))));
		Assert.Throws<ArgumentException>(() => Protocol.Hello(new string('x', Protocol.LimitFor("token") + 1)));
	}

	[Fact]
	public void LongTitlesAndNamesAreCutToFitRatherThanRefused()
	{
		var now = Json(Protocol.Now(new string('t', 500), new string('a', 500), false));
		Assert.Equal(Protocol.LimitFor("title"), now.GetProperty("title").GetString()!.Length);
		Assert.Equal(Protocol.LimitFor("artist"), now.GetProperty("artist").GetString()!.Length);
		Assert.Equal(Protocol.LimitFor("device"), Json(Protocol.Pair("ABC234", new string('d', 500))).GetProperty("device").GetString()!.Length);
		// Quotes, backslashes and emoji survive the trip
		Assert.Equal("He said \"hi\" \\ 🦝", Json(Protocol.Now("He said \"hi\" \\ 🦝", "", true)).GetProperty("title").GetString());
	}

	[Theory]
	[InlineData("abc234", true)]
	[InlineData(" ABC-234 ", true)]
	[InlineData("a b c 2 3 4", true)]
	[InlineData("abc23", false)]
	[InlineData("abc2345", false)]
	[InlineData("abc23!", false)]
	[InlineData("", false)]
	public void APairingCodeIsSixLettersOrDigitsHoweverItIsTyped(string typed, bool looksRight)
	{
		Assert.Equal(looksRight, Protocol.LooksLikeCode(typed));
		if(looksRight) Assert.Equal(6, Protocol.TidyCode(typed).Length);
	}

	[Fact]
	public void ItReadsEveryMessageTheBotSends()
	{
		var messages = new Dictionary<string, string>
		{
			["welcome"] = "{\"type\":\"welcome\",\"v\":1}",
			["paired"] = "{\"type\":\"paired\",\"token\":\"abc\"}",
			["error"] = "{\"type\":\"error\",\"code\":\"busy\",\"message\":\"Try again.\"}",
			["live"] = "{\"type\":\"live\"}",
			["ended"] = "{\"type\":\"ended\",\"reason\":\"stopped\"}",
			["pause"] = "{\"type\":\"pause\",\"reason\":\"Bean paused it\"}",
			["resume"] = "{\"type\":\"resume\"}",
			["skip"] = "{\"type\":\"skip\"}",
			["request"] = "{\"type\":\"request\",\"id\":7,\"by\":\"Bean\",\"song\":\"Mr. Brightside\"}",
			["notify"] = "{\"type\":\"notify\",\"text\":\"Paused for a summon.\"}"
		};
		Assert.Equal(Protocol.Server.Keys.Order(), messages.Keys.Order());
		foreach(var (type, text) in messages) Assert.Equal(type, Protocol.ReadServer(text)?.Type);
		Assert.Equal("Mr. Brightside", Protocol.ReadServer(messages["request"])!.Text("song"));
		Assert.Equal(1, Protocol.ReadServer(messages["welcome"])!.Number("v"));
		// A field it doesn't know is fine: a newer bot may add one
		Assert.Equal("live", Protocol.ReadServer("{\"type\":\"live\",\"listeners\":3}")?.Type);
	}

	[Theory]
	[InlineData("")]
	[InlineData("live")]
	[InlineData("{")]
	[InlineData("[]")]
	[InlineData("{\"type\":\"self-destruct\"}")]
	[InlineData("{\"type\":\"hello\",\"v\":1,\"token\":\"x\"}")]
	[InlineData("{\"type\":[\"live\"]}")]
	[InlineData("{\"type\":\"welcome\"}")]
	[InlineData("{\"type\":\"welcome\",\"v\":\"1\"}")]
	[InlineData("{\"type\":\"request\",\"by\":\"Bean\"}")]
	[InlineData("{\"type\":\"error\",\"code\":7,\"message\":\"x\"}")]
	public void AnythingElseIsNotAMessage(string text) => Assert.Null(Protocol.ReadServer(text));

	[Theory]
	[InlineData("https://spotify.link/AbC123", true)]
	[InlineData(" https://open.spotify.com/socialsession/xyz ", true)]
	[InlineData("https://spotify.app.link/abc?_p=1", true)]
	[InlineData("", false)]
	[InlineData("spotify.link/abc", false)]
	[InlineData("http://spotify.link/abc", false)]
	[InlineData("https://spotify.link.evil.example/abc", false)]
	[InlineData("https://evil.example/?u=https://spotify.link/abc", false)]
	[InlineData("https://user:pass@spotify.link/abc", false)]
	[InlineData("https://discord.gg/abc", false)]
	public void OnlyARealSpotifyLinkCountsAsAJam(string text, bool isJam) => Assert.Equal(isJam, Protocol.IsJamLink(text));
}

public class OpusFramerTests
{
	// A second of a 440 Hz tone, as Windows hands sound over: 16-bit, left then right
	static byte[] Tone(double seconds)
	{
		var samples = (int)(Protocol.SampleRate * seconds);
		var pcm = new byte[samples * Protocol.Channels * 2];
		for(var i = 0; i < samples; i++)
		{
			var value = (short)(Math.Sin(2 * Math.PI * 440 * i / Protocol.SampleRate) * 12000);
			for(var channel = 0; channel < Protocol.Channels; channel++)
			{
				pcm[(i * Protocol.Channels + channel) * 2] = (byte)(value & 0xff);
				pcm[(i * Protocol.Channels + channel) * 2 + 1] = (byte)(value >> 8);
			}
		}
		return pcm;
	}

	[Fact]
	public void ASecondOfSoundIsFiftyPacketsEachSmallEnoughForTheBot()
	{
		var packets = new List<ReadOnlyMemory<byte>>();
		new OpusFramer().Push(Tone(1), packets.Add);
		Assert.Equal(1000 / Protocol.FrameMs, packets.Count);
		Assert.All(packets, (packet) => Assert.InRange(packet.Length, 1, 1275));
		// Around the rate it was asked for: 128 kbit/s is 16,000 bytes a second
		Assert.InRange(packets.Sum((p) => p.Length), 4_000, 24_000);
	}

	[Fact]
	public void SoundArrivingInOddSizedPiecesComesOutTheSame()
	{
		var tone = Tone(1);
		var whole = new List<byte[]>();
		new OpusFramer().Push(tone, (packet) => whole.Add(packet.ToArray()));
		var pieces = new List<byte[]>();
		var framer = new OpusFramer();
		// 441 bytes at a time: never a whole frame, and it splits samples down the middle
		for(var at = 0; at < tone.Length; at += 441) framer.Push(tone.AsSpan(at, Math.Min(441, tone.Length - at)), (packet) => pieces.Add(packet.ToArray()));
		Assert.Equal(whole.Count, pieces.Count);
		Assert.Equal(whole, pieces);
	}

	[Fact]
	public void LessThanTwentyMillisecondsIsHeldUntilTheRestArrives()
	{
		var framer = new OpusFramer();
		var count = 0;
		var frameBytes = Protocol.SamplesPerFrame * Protocol.Channels * 2;
		framer.Push(new byte[frameBytes - 2], (_) => count++);
		Assert.Equal(0, count);
		framer.Push(new byte[2], (_) => count++);
		Assert.Equal(1, count);
		// Starting again forgets the part it was holding
		framer.Push(new byte[frameBytes - 2], (_) => count++);
		framer.Reset();
		framer.Push(new byte[2], (_) => count++);
		Assert.Equal(1, count);
	}

	[Fact]
	public void EachPacketIsItsOwnCopySoOneWaitingToBeSentIsNotOverwritten()
	{
		var packets = new List<ReadOnlyMemory<byte>>();
		new OpusFramer().Push(Tone(0.1), packets.Add);
		var first = packets[0].ToArray();
		new OpusFramer().Push(Tone(0.5), (_) => { });
		Assert.Equal(first, packets[0].ToArray());
		Assert.NotEqual(packets[0].ToArray(), packets[2].ToArray());
	}
}
