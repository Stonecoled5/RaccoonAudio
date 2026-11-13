using System.Text.Json;
using RaccoonAudio.Core;

namespace RaccoonAudio.Tests;

// The app's side of the conversation, against a fake connection: signing in, pairing, going live, what happens to
// audio, coming back after a drop, and what each of the bot's errors makes it do.
public class StreamClientTests
{
	static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	[Fact]
	public async Task WithNoKeyItWaitsForAPairingCodeAndConnectsNowhere()
	{
		using var rig = new Rig(token: null);
		rig.Client.Start();
		await Rig.Until("it to ask for pairing", () => rig.Client.State == ClientState.NeedsPairing);
		await Task.Delay(50);
		Assert.Equal(0, rig.SocketCount);
		Assert.False(rig.Client.HasKey);
	}

	[Fact]
	public async Task APairedComputerSignsInWithItsKeyFirst()
	{
		using var rig = new Rig("key-1");
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		var hello = Json(socket.Texts[0]);
		Assert.Equal("hello", hello.GetProperty("type").GetString());
		Assert.Equal("key-1", hello.GetProperty("token").GetString());
		Assert.Equal(Protocol.Version, hello.GetProperty("v").GetInt32());
		Assert.Equal(ClientState.Connected, rig.Client.State);
		Assert.Equal([ClientState.Connecting, ClientState.SigningIn, ClientState.Connected], rig.States);
	}

	[Fact]
	public async Task PairingSendsTheTidiedCodeAndKeepsTheKeyItGetsBack()
	{
		using var rig = new Rig(token: null);
		rig.Client.Pair(" abc-234 ", "Den PC");
		var socket = await rig.SocketAsync(1);
		await Rig.Until("the pair message", () => socket.Types.Length >= 1);
		var pair = Json(socket.Texts[0]);
		Assert.Equal("pair", pair.GetProperty("type").GetString());
		Assert.Equal("ABC234", pair.GetProperty("code").GetString());
		Assert.Equal("Den PC", pair.GetProperty("device").GetString());
		socket.Say(new { type = "paired", token = "new-key" });
		socket.Say(new { type = "welcome", v = Protocol.Version, name = "Raccoon Man" });
		await Rig.Until("being signed in", () => rig.Client.State == ClientState.Connected);
		Assert.Equal(["new-key"], rig.Keys);
		Assert.True(rig.Client.HasKey);
		// The next connection uses the key, not the code
		socket.Close();
		var second = await rig.SocketAsync(2);
		await Rig.Until("hello", () => second.Types.Length >= 1);
		Assert.Equal("new-key", Json(second.Texts[0]).GetProperty("token").GetString());
	}

	[Fact]
	public async Task GoingLiveSendsAudioOnlyOnceTheBotSaysItIsPlaying()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.SendAudio(new byte[] { 1, 2, 3 });
		rig.Client.GoLive();
		await Rig.Until("live to be asked for", () => socket.Types.Contains("live"));
		rig.Client.SendAudio(new byte[] { 4, 5, 6 });
		Assert.Equal(ClientState.Connected, rig.Client.State);
		socket.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		rig.Client.SendAudio(new byte[] { 7, 8, 9 });
		rig.Client.SendAudio(Array.Empty<byte>());
		rig.Client.SendAudio(new byte[Protocol.MaxAudioFrameBytes + 1]);
		rig.Client.SendAudio(new byte[] { 10 });
		await Rig.Until("the audio", () => socket.Packets.Length >= 2);
		await Task.Delay(30);
		Assert.Equal([[7, 8, 9], [10]], socket.Packets);
	}

	[Fact]
	public async Task GoLivePressedWhileStillConnectingHappensAsSoonAsItIsSignedIn()
	{
		using var rig = new Rig();
		rig.Client.GoLive();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		await Rig.Until("live to be asked for", () => socket.Types.Contains("live"));
		Assert.Equal(["hello", "live"], socket.Types);
	}

	[Fact]
	public async Task StoppingTellsTheBotAndStaysConnected()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		rig.Client.StopLive();
		await Rig.Until("stop to be sent", () => socket.Types.Contains("stop"));
		socket.Say(new { type = "ended", reason = "stopped" });
		await Rig.Until("the stream to be over", () => rig.Client.State == ClientState.Connected);
		Assert.False(rig.Client.WantsLive);
		Assert.Equal(1, rig.SocketCount);
		Assert.Equal("ended", Assert.Single(rig.Said).Type);
	}

	[Fact]
	public async Task WhatIsPlayingAndTheJamLinkOnlyGoWhileLive()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.SendNow("Song", "Band", true);
		Assert.True(rig.Client.SendJam("https://spotify.link/abc"));
		rig.Client.GoLive();
		socket.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		rig.Client.SendNow("Song", "Band", true);
		Assert.True(rig.Client.SendJam(" https://spotify.link/abc "));
		Assert.False(rig.Client.SendJam("https://evil.example/spotify.link"));
		await Rig.Until("both to be sent", () => socket.Types.Length >= 4);
		Assert.Equal(["hello", "live", "now", "jam"], socket.Types);
		Assert.Equal("https://spotify.link/abc", Json(socket.Texts[3]).GetProperty("url").GetString());
	}

	[Fact]
	public async Task ADropWhileLiveSignsInAgainAndGoesLiveAgainByItself()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var first = await rig.SignedInAsync();
		rig.Client.GoLive();
		first.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		first.Close();
		var second = await rig.SignedInAsync(2);
		await Rig.Until("live to be asked for again", () => second.Types.Contains("live"));
		Assert.Equal(["hello", "live"], second.Types);
		Assert.Contains(ClientState.Reconnecting, rig.States);
		Assert.True(first.Disposed);
		// It was signed in when it dropped, so it comes straight back on the shortest wait
		Assert.Equal(StreamClient.RetryDelays[0], rig.Waits[0]);
		second.Say(new { type = "live" });
		await Rig.Until("being live again", () => rig.Client.State == ClientState.Live);
		// Nothing from before the drop is sent on the new connection
		rig.Client.SendAudio(new byte[] { 1 });
		await Rig.Until("audio on the new connection", () => second.Packets.Length == 1);
		Assert.Empty(first.Packets);
	}

	// The bot restarts at least once a day, and its stream server is up before the rest of it is ready to play
	[Fact]
	public async Task AfterADropItKeepsAskingToGoLiveUntilHeIsReady()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var first = await rig.SignedInAsync();
		rig.Client.GoLive();
		first.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		first.Close();
		var second = await rig.SignedInAsync(2);
		await Rig.Until("live to be asked for", () => second.Types.Count((type) => type == "live") == 1);
		second.Say(new { type = "error", code = "unavailable", message = "I'm still starting up. Try again in a moment." });
		await Rig.Until("live to be asked for again", () => second.Types.Count((type) => type == "live") == 2);
		second.Say(new { type = "error", code = "unavailable", message = "I'm still starting up. Try again in a moment." });
		await Rig.Until("and again", () => second.Types.Count((type) => type == "live") == 3);
		second.Say(new { type = "live" });
		await Rig.Until("being live again", () => rig.Client.State == ClientState.Live);
		Assert.Empty(rig.Problems);
		Assert.True(rig.Client.WantsLive);
		Assert.Equal(2, rig.Waits.Count((wait) => wait == StreamClient.ResumeDelay));
	}

	[Fact]
	public async Task IfHeNeverTakesTheStreamBackItGivesUpAndSaysWhy()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var first = await rig.SignedInAsync();
		rig.Client.GoLive();
		first.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		first.Close();
		var second = await rig.SignedInAsync(2);
		for(var asked = 1; asked <= StreamClient.ResumeTries + 1; asked++)
		{
			await Rig.Until("live to be asked for, time " + asked, () => second.Types.Count((type) => type == "live") == asked);
			second.Say(new { type = "error", code = "unavailable", message = "Get in a voice channel first, then go live." });
		}
		await Rig.Until("it to give up", () => rig.Problems.Count == 1);
		Assert.Equal(["Get in a voice channel first, then go live."], rig.Problems);
		Assert.False(rig.Client.WantsLive);
		Assert.Equal(ClientState.Connected, rig.Client.State);
		await Task.Delay(50);
		Assert.Equal(StreamClient.ResumeTries + 1, second.Types.Count((type) => type == "live"));
	}

	// Pressing Go live is different: if he can't, the person is told at once rather than after half a minute
	[Fact]
	public async Task AFreshGoLiveThatHeRefusesIsNotRetried()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "error", code = "unavailable", message = "Get in a voice channel first, then go live." });
		await Rig.Until("the reason", () => rig.Problems.Count == 1);
		await Task.Delay(50);
		Assert.Equal(1, socket.Types.Count((type) => type == "live"));
		Assert.DoesNotContain(StreamClient.ResumeDelay, rig.Waits);
	}

	[Fact]
	public async Task ItWaitsLongerEachTimeItCanNotConnectAndStartsOverOnceItHas()
	{
		using var rig = new Rig();
		rig.FailNext = 6;
		rig.Client.Start();
		var socket = await rig.SignedInAsync(7);
		Assert.Equal(new[] { 1, 2, 5, 10, 10, 10 }, rig.Waits.Select((w) => (int)w.TotalSeconds).ToArray());
		rig.FailNext = 1;
		socket.Close();
		await rig.SignedInAsync(9);
		Assert.Equal(new[] { 1, 1 }, rig.Waits.Skip(6).Select((w) => (int)w.TotalSeconds).ToArray());
	}

	[Fact]
	public async Task AnOlderAppIsToldToUpdateAndStopsTrying()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SocketAsync(1);
		socket.Say(new { type = "error", code = "update", message = "Update the app." });
		socket.Close();
		await Rig.Until("it to give up", () => rig.Client.State == ClientState.NeedsUpdate);
		await Task.Delay(50);
		Assert.Equal(1, rig.SocketCount);
		Assert.Equal(["Update the app."], rig.Problems);
	}

	[Fact]
	public async Task AKeyTheBotNoLongerKnowsIsForgottenAndItAsksToPairAgain()
	{
		using var rig = new Rig();
		rig.Client.GoLive();
		rig.Client.Start();
		var socket = await rig.SocketAsync(1);
		socket.Say(new { type = "error", code = "bad-token", message = "Pair it again." });
		socket.Close();
		await Rig.Until("it to ask for pairing", () => rig.Client.State == ClientState.NeedsPairing);
		Assert.Equal(1, rig.KeyRejections);
		Assert.False(rig.Client.HasKey);
		Assert.False(rig.Client.WantsLive);
		await Task.Delay(50);
		Assert.Equal(1, rig.SocketCount);
	}

	[Fact]
	public async Task UnpairingAsksTheBotAndForgetsTheKeyOnceItConfirms()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync(1);
		var unpairing = rig.Client.UnpairAsync(TimeSpan.FromSeconds(10));
		await Rig.Until("the unpair to go out", () => socket.Types.Contains("unpair"));
		socket.Say(new { type = "unpaired" });
		socket.Close();
		Assert.True(await unpairing);
		await Rig.Until("it to ask for pairing", () => rig.Client.State == ClientState.NeedsPairing);
		Assert.False(rig.Client.HasKey);
		Assert.False(rig.Client.WantsLive);
	}

	[Fact]
	public async Task UnpairingWhileNotConnectedSaysTheBotWasNotTold()
	{
		using var rig = new Rig(token: null);
		Assert.False(await rig.Client.UnpairAsync(TimeSpan.FromSeconds(1)));
	}

	[Fact]
	public async Task AnUnpairTheBotRefusesIsNotConfirmed()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync(1);
		var unpairing = rig.Client.UnpairAsync(TimeSpan.FromSeconds(10));
		await Rig.Until("the unpair to go out", () => socket.Types.Contains("unpair"));
		socket.Say(new { type = "error", code = "unavailable", message = "Couldn't unpair this computer on the bot's side. Unpair it in /me instead." });
		Assert.False(await unpairing);
		Assert.True(rig.Client.HasKey);
	}

	[Fact]
	public async Task TheBotsNameComesWithTheWelcomeAndIsOnlyAnnouncedWhenItChanges()
	{
		using var rig = new Rig();
		var heard = new List<string>();
		rig.Client.NameChanged += (name) => { lock(heard) heard.Add(name); };
		Assert.Null(rig.Client.BotName);
		rig.Client.Start();
		var socket = await rig.SocketAsync(1);
		await Rig.Until("hello", () => socket.Types.Length >= 1);
		socket.Say(new { type = "welcome", v = Protocol.Version, name = "Captain Trash" });
		await Rig.Until("being signed in", () => rig.Client.State == ClientState.Connected);
		Assert.Equal("Captain Trash", rig.Client.BotName);
		// Signing in again under the same name says nothing new
		socket.Close();
		var second = await rig.SocketAsync(2);
		await Rig.Until("hello again", () => second.Types.Length >= 1);
		second.Say(new { type = "welcome", v = Protocol.Version, name = "Captain Trash" });
		await Rig.Until("signed in again", () => rig.Client.State == ClientState.Connected && rig.SocketCount == 2);
		lock(heard) Assert.Equal(["Captain Trash"], heard);
	}

	[Theory]
	[InlineData("bad-code")]
	[InlineData("too-many-devices")]
	public async Task AFailedPairingSaysWhyAndGoesBackToAskingForACode(string code)
	{
		using var rig = new Rig(token: null);
		rig.Client.Pair("ABC234", "Den PC");
		var socket = await rig.SocketAsync(1);
		socket.Say(new { type = "error", code, message = "No." });
		socket.Close();
		await Rig.Until("it to ask for pairing", () => rig.Client.State == ClientState.NeedsPairing);
		Assert.Equal(["No."], rig.Problems);
		Assert.Empty(rig.Keys);
		await Task.Delay(50);
		Assert.Equal(1, rig.SocketCount);
	}

	[Fact]
	public async Task ShutOutForTooManyTriesItWaitsTheWholeTimeBeforeTryingAgain()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SocketAsync(1);
		socket.Say(new { type = "error", code = "too-many-tries", message = "Wait a while." });
		socket.Close();
		await rig.SocketAsync(2);
		Assert.Equal([StreamClient.ShutOutDelay], rig.Waits);
	}

	[Fact]
	public async Task ReplacedByAnotherCopyItStopsInsteadOfFightingForTheConnection()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "error", code = "replaced", message = "Connected again." });
		socket.Close();
		await Rig.Until("it to stop", () => rig.Client.State == ClientState.Off);
		await Task.Delay(50);
		Assert.Equal(1, rig.SocketCount);
		Assert.False(rig.Client.WantsLive);
		// Until the person asks it to try again
		rig.Client.Retry();
		await rig.SignedInAsync(2);
	}

	[Fact]
	public async Task WhenHeCanNotStartTheStreamItSaysWhyAndStaysConnected()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "error", code = "unavailable", message = "Get in a voice channel first, then go live." });
		await Rig.Until("the reason", () => rig.Problems.Count == 1);
		Assert.Equal(["Get in a voice channel first, then go live."], rig.Problems);
		Assert.Equal(ClientState.Connected, rig.Client.State);
		Assert.False(rig.Client.WantsLive);
		Assert.Equal(1, rig.SocketCount);
	}

	[Theory]
	[InlineData("busy")]
	[InlineData("timeout")]
	[InlineData("too-fast")]
	[InlineData("bad-message")]
	public async Task OtherErrorsAreTriedAgain(string code)
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SocketAsync(1);
		socket.Say(new { type = "error", code, message = "Try again shortly." });
		socket.Close();
		await rig.SocketAsync(2);
		Assert.Equal(["Try again shortly."], rig.Problems);
	}

	[Fact]
	public async Task WhatTheBotAsksForWhileLiveIsPassedOnAndNonsenseIsIgnored()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "live" });
		socket.SayRaw("not json");
		socket.Say(new { type = "self-destruct" });
		socket.Say(new { type = "request", by = "Bean" });
		socket.Say(new { type = "pause", reason = "Bean paused it" });
		socket.Say(new { type = "resume" });
		socket.Say(new { type = "skip" });
		socket.Say(new { type = "request", id = 7, by = "Bean", song = "Mr. Brightside" });
		socket.Say(new { type = "notify", text = "Paused for a summon." });
		await Rig.Until("all five", () => rig.Said.Count >= 5);
		Assert.Equal(["pause", "resume", "skip", "request", "notify"], rig.Said.Select((m) => m.Type));
		Assert.Equal("Mr. Brightside", rig.Said[3].Text("song"));
		Assert.Equal(ClientState.Live, rig.Client.State);
		Assert.Equal(1, rig.SocketCount);
	}

	[Fact]
	public async Task AudioThatCanNotGetOutIsDroppedNotQueuedUp()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		socket.HoldAudio = new TaskCompletionSource();
		for(var i = 0; i < 200; i++) rig.Client.SendAudio(new byte[] { (byte)i });
		socket.HoldAudio.SetResult();
		await Task.Delay(100);
		// One was already on its way out when the rest arrived
		Assert.InRange(socket.Packets.Length, StreamClient.MaxWaitingPackets, StreamClient.MaxWaitingPackets + 1);
		// And once it's flowing again, new audio gets through
		socket.HoldAudio = null;
		rig.Client.SendAudio(new byte[] { 250 });
		await Rig.Until("fresh audio", () => socket.Packets.Any((p) => p[0] == 250));
	}

	[Fact]
	public async Task StoppingTheAppEndsTheStreamFirstSoHeLeavesVoiceAtOnce()
	{
		var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		rig.Client.GoLive();
		socket.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		await rig.Client.StopAsync();
		Assert.Contains("stop", socket.Types);
		Assert.Equal(ClientState.Off, rig.Client.State);
		Assert.True(socket.Disposed);
		rig.Dispose();
	}
}
