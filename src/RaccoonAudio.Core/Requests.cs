using System.Text.RegularExpressions;

namespace RaccoonAudio.Core;

// A song someone in the voice channel asked for with /radio add. The id is the bot's: it's what the app names when
// it tells the bot the request has been dealt with.
public sealed record SongRequest(long Id, string By, string Song, DateTime At);

// The songs people have asked for and the streamer hasn't dealt with yet, oldest first.
//
// The bot keeps the same list, to hold each person to so many waiting requests. So when one comes off here it's also
// put on a list of things to tell the bot, which the app sends a few at a time while it's live. This list outlives
// the stream (the streamer may still want to play them); the bot's doesn't, and it ignores news about a request it
// no longer has.
//
// Used from the window's thread only.
public sealed class RequestList
{
	// The most it holds. The bot stops taking requests at the same number; past it the oldest goes.
	public const int Most = 200;

	readonly List<SongRequest> waiting = [];
	readonly Queue<long> toTell = new();

	public IReadOnlyList<SongRequest> All => waiting.ToArray();
	public int Count => waiting.Count;
	// How many have arrived since the streamer last looked
	public int Unseen { get; private set; }
	// How many finished requests the bot hasn't been told about yet
	public int WaitingToTell => toTell.Count;

	public SongRequest Add(long id, string by, string song, DateTime at, bool seen = false)
	{
		var request = new SongRequest(id, by.Trim(), song.Trim(), at);
		waiting.Add(request);
		if(waiting.Count > Most) waiting.RemoveAt(0);
		if(!seen) Unseen++;
		return request;
	}

	// The streamer has dealt with it. False if it wasn't on the list any more.
	public bool Done(SongRequest request)
	{
		if(!waiting.Remove(request)) return false;
		toTell.Enqueue(request.Id);
		return true;
	}

	// All of them at once
	public void Clear()
	{
		foreach(var request in waiting) toTell.Enqueue(request.Id);
		waiting.Clear();
		Unseen = 0;
	}

	// The whole list was cleared while live and the bot has been told so in one message: nothing is left to tell
	public void ClearTold()
	{
		waiting.Clear();
		toTell.Clear();
		Unseen = 0;
	}

	public void Seen() => Unseen = 0;

	// The next few finished requests to tell the bot about, taken off the list of those
	public IReadOnlyList<long> NextToTell(int atMost)
	{
		var ids = new List<long>();
		while(ids.Count < atMost && toTell.Count > 0) ids.Add(toTell.Dequeue());
		return ids;
	}
}

// Where "Open in Spotify" goes for a request. Requests are whatever someone typed, so this never looks anything up:
// it opens Spotify on a search for those words, which works for "that Drake one" as well as an exact title. If what
// they typed is a Spotify link to a song, album, playlist or artist, it opens that instead.
//
// What someone typed never becomes anything but a spotify: or https://open.spotify.com/ address: it's either matched
// against a strict pattern or escaped whole into a search.
public static partial class Spotify
{
	[GeneratedRegex(@"open\.spotify\.com/(?:intl-[a-z]{2,5}/)?(track|album|playlist|artist|episode|show)/([A-Za-z0-9]{22})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
	private static partial Regex WebLink();

	[GeneratedRegex(@"^spotify:(track|album|playlist|artist|episode|show):([A-Za-z0-9]{22})$", RegexOptions.IgnoreCase)]
	private static partial Regex AppLink();

	static (string Kind, string Id)? Thing(string request)
	{
		var text = request.Trim();
		var match = AppLink().Match(text);
		if(!match.Success) match = WebLink().Match(text);
		return match.Success ? (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value) : null;
	}

	// For the Spotify program on this computer
	public static string AppLinkFor(string request) => Thing(request) is { } thing ? "spotify:" + thing.Kind + ":" + thing.Id : "spotify:search:" + Uri.EscapeDataString(request.Trim());

	// For a web browser, when the Spotify program isn't installed
	public static string WebLinkFor(string request) => Thing(request) is { } thing ? "https://open.spotify.com/" + thing.Kind + "/" + thing.Id : "https://open.spotify.com/search/" + Uri.EscapeDataString(request.Trim());
}
