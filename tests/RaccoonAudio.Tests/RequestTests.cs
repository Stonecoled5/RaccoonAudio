using System.Text.Json;
using RaccoonAudio.Core;
using Xunit;

namespace RaccoonAudio.Tests;

public class RequestListTests
{
	static readonly DateTime now = new(2026, 11, 12, 21, 0, 0);

	[Fact]
	public void RequestsWaitOldestFirstAndCountAsUnseenUntilLookedAt()
	{
		var list = new RequestList();
		var first = list.Add(1001, " Bean ", " Mr. Brightside ", now);
		list.Add(1002, "Mo", "Africa", now.AddMinutes(1));
		Assert.Equal(["Mr. Brightside", "Africa"], list.All.Select((r) => r.Song));
		Assert.Equal("Bean", first.By);
		Assert.Equal(2, list.Unseen);
		list.Seen();
		Assert.Equal(0, list.Unseen);
		list.Add(1003, "Mo", "Toto again", now, seen: true);
		Assert.Equal(0, list.Unseen);
		Assert.Equal(3, list.Count);
	}

	[Fact]
	public void ADoneRequestComesOffAndTheBotIsToldAFewAtATime()
	{
		var list = new RequestList();
		var requests = Enumerable.Range(1, 5).Select((i) => list.Add(2000 + i, "Bean", "Song " + i, now)).ToList();
		Assert.True(list.Done(requests[1]));
		Assert.True(list.Done(requests[3]));
		Assert.True(list.Done(requests[0]));
		// Ticked off twice: nothing to do, and the bot isn't told twice
		Assert.False(list.Done(requests[1]));
		Assert.Equal(["Song 3", "Song 5"], list.All.Select((r) => r.Song));
		Assert.Equal(3, list.WaitingToTell);
		Assert.Equal([2002L, 2004L], list.NextToTell(2));
		Assert.Equal([2001L], list.NextToTell(2));
		Assert.Empty(list.NextToTell(2));
	}

	[Fact]
	public void ClearingTheListTellsTheBotAboutEveryOne()
	{
		var list = new RequestList();
		for(var i = 1; i <= 3; i++) list.Add(3000 + i, "Bean", "Song " + i, now);
		list.Clear();
		Assert.Empty(list.All);
		Assert.Equal(0, list.Unseen);
		Assert.Equal([3001L, 3002L, 3003L], list.NextToTell(10));
	}

	[Fact]
	public void ClearingWhileLiveIsOneMessageSoNothingIsLeftToTell()
	{
		var list = new RequestList();
		var first = list.Add(4001, "Bean", "Song", now);
		list.Add(4002, "Bean", "Another", now);
		list.Done(first);
		list.ClearTold();
		Assert.Empty(list.All);
		Assert.Equal(0, list.WaitingToTell);
	}

	[Fact]
	public void ItHoldsSoManyAndThenLetsTheOldestGo()
	{
		var list = new RequestList();
		for(var i = 0; i < RequestList.Most + 5; i++) list.Add(i, "Bean", "Song " + i, now);
		Assert.Equal(RequestList.Most, list.Count);
		Assert.Equal("Song 5", list.All[0].Song);
	}
}

public class SpotifyLinkTests
{
	[Theory]
	[InlineData("Mr. Brightside", "spotify:search:Mr.%20Brightside", "https://open.spotify.com/search/Mr.%20Brightside")]
	[InlineData("  that Drake one  ", "spotify:search:that%20Drake%20one", "https://open.spotify.com/search/that%20Drake%20one")]
	[InlineData("AC/DC & friends: live?", "spotify:search:AC%2FDC%20%26%20friends%3A%20live%3F", "https://open.spotify.com/search/AC%2FDC%20%26%20friends%3A%20live%3F")]
	[InlineData("Beyoncé – Déjà Vu", "spotify:search:Beyonc%C3%A9%20%E2%80%93%20D%C3%A9j%C3%A0%20Vu", "https://open.spotify.com/search/Beyonc%C3%A9%20%E2%80%93%20D%C3%A9j%C3%A0%20Vu")]
	public void WordsBecomeASearch(string request, string app, string web)
	{
		Assert.Equal(app, Spotify.AppLinkFor(request));
		Assert.Equal(web, Spotify.WebLinkFor(request));
	}

	[Theory]
	[InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=abc123", "spotify:track:4cOdK2wGLETKBW3PvgPWqT", "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT")]
	[InlineData("play this https://open.spotify.com/intl-de/album/4aawyAB9vmqN3uQ7FjRGTy please", "spotify:album:4aawyAB9vmqN3uQ7FjRGTy", "https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy")]
	[InlineData("open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M", "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")]
	[InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT", "spotify:track:4cOdK2wGLETKBW3PvgPWqT", "https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT")]
	public void ASpotifyLinkOpensThatThing(string request, string app, string web)
	{
		Assert.Equal(app, Spotify.AppLinkFor(request));
		Assert.Equal(web, Spotify.WebLinkFor(request));
	}

	[Theory]
	[InlineData("https://evil.example/open.spotify.com/track/short")]
	[InlineData("https://open.spotify.com/track/tooShort")]
	[InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqTandMore")]
	[InlineData("https://open.spotify.com/user/someone")]
	[InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT:extra")]
	[InlineData("javascript:alert(1)")]
	[InlineData("file:///C:/Windows/System32/calc.exe")]
	[InlineData("calc.exe & echo")]
	[InlineData("\" --flag")]
	public void AnythingElseIsOnlyEverSearchedFor(string request)
	{
		var app = Spotify.AppLinkFor(request);
		var web = Spotify.WebLinkFor(request);
		Assert.StartsWith("spotify:search:", app);
		Assert.StartsWith("https://open.spotify.com/search/", web);
		// Escaped whole: nothing in it can end the address or start another
		Assert.Matches("^spotify:search:[A-Za-z0-9%._~-]*$", app);
		Assert.Matches("^https://open\\.spotify\\.com/search/[A-Za-z0-9%._~-]*$", web);
	}
}

public class RequestMessageTests
{
	[Fact]
	public void DoneAndClearAreBuiltTheWayTheProtocolSays()
	{
		var done = JsonDocument.Parse(Protocol.Done(1763000000123)).RootElement;
		Assert.Equal("done", done.GetProperty("type").GetString());
		Assert.Equal(1763000000123, done.GetProperty("id").GetInt64());
		Assert.Equal("{\"type\":\"clear\"}", Protocol.Clear());
	}

	[Fact]
	public void ARequestFromTheBotCarriesItsNumber()
	{
		var message = Protocol.ReadServer("{\"type\":\"request\",\"id\":1763000000123,\"by\":\"Bean\",\"song\":\"Africa\"}");
		Assert.NotNull(message);
		Assert.Equal(1763000000123, message!.Number("id"));
		Assert.Equal("Africa", message.Text("song"));
		// Without a number it isn't a request this version understands
		Assert.Null(Protocol.ReadServer("{\"type\":\"request\",\"by\":\"Bean\",\"song\":\"Africa\"}"));
	}

	[Fact]
	public async Task DoneAndClearAreOnlySentWhileLive()
	{
		using var rig = new Rig();
		rig.Client.Start();
		var socket = await rig.SignedInAsync();
		Assert.False(rig.Client.SendDone(5));
		Assert.False(rig.Client.SendClear());
		rig.Client.GoLive();
		socket.Say(new { type = "live" });
		await Rig.Until("being live", () => rig.Client.State == ClientState.Live);
		Assert.True(rig.Client.SendDone(5));
		Assert.True(rig.Client.SendClear());
		await Rig.Until("both to be sent", () => socket.Types.Length >= 4);
		Assert.Equal(["hello", "live", "done", "clear"], socket.Types);
	}
}
