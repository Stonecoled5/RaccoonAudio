using Windows.Media.Control;

namespace RaccoonAudio.Media;

// What's playing, and pressing pause, play and skip, through Windows' own media controls: the same thing the
// keyboard's media keys and the volume pop-up use. It works for Spotify and any other player that shows up there,
// and needs no login to anything.
//
// It follows the media session of the program being streamed. A program with no session (most games, some web
// pages) can still be streamed: there's just no song title to show and nothing for pause or skip to press.
sealed class MediaWatcher : IDisposable
{
	// The title, the artist, and whether it's playing. Raised on some thread of Windows' choosing.
	public event Action<string, string, bool>? Changed;

	GlobalSystemMediaTransportControlsSessionManager? manager;
	GlobalSystemMediaTransportControlsSession? session;
	string program = "";
	(string Title, string Artist, bool Playing)? last;

	public bool HasSession => session != null;

	public async Task StartAsync(string programName)
	{
		program = programName;
		last = null;
		try
		{
			if(manager == null)
			{
				manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
				manager.SessionsChanged += (_, _) => Follow();
			}
			Follow();
		}
		catch(Exception)
		{
			// Windows won't give out the media controls: streaming goes on without titles
		}
	}

	public void Stop()
	{
		Unfollow();
		last = null;
	}

	// Picks the session belonging to the program being streamed. Sessions are named by an app id, which for an
	// ordinary program is its file name ("Spotify.exe") and for a Store app contains its name.
	void Follow()
	{
		if(manager == null) return;
		GlobalSystemMediaTransportControlsSession? found = null;
		try
		{
			found = manager.GetSessions().FirstOrDefault((s) => s.SourceAppUserModelId.Contains(program, StringComparison.OrdinalIgnoreCase));
		}
		catch(Exception) { }
		if(found?.SourceAppUserModelId == session?.SourceAppUserModelId && found != null) return;
		Unfollow();
		session = found;
		if(session == null) return;
		session.MediaPropertiesChanged += OnChanged;
		session.PlaybackInfoChanged += OnPlaybackChanged;
		_ = ReportAsync();
	}

	void Unfollow()
	{
		if(session == null) return;
		try
		{
			session.MediaPropertiesChanged -= OnChanged;
			session.PlaybackInfoChanged -= OnPlaybackChanged;
		}
		catch(Exception) { }
		session = null;
	}

	void OnChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = ReportAsync();
	void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = ReportAsync();

	// Says what's playing, if it's different from what was last said
	async Task ReportAsync()
	{
		var current = session;
		if(current == null) return;
		try
		{
			var properties = await current.TryGetMediaPropertiesAsync();
			var playing = current.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
			var now = (properties?.Title ?? "", properties?.Artist ?? "", playing);
			if(now == last) return;
			last = now;
			Changed?.Invoke(now.Item1, now.Item2, now.playing);
		}
		catch(Exception)
		{
			// The session went away while it was being asked
		}
	}

	// Says it again, for when a stream starts and the bot hasn't heard what's on
	public void Repeat()
	{
		last = null;
		_ = ReportAsync();
	}

	public async Task<bool> PauseAsync() => await TryAsync((s) => s.TryPauseAsync().AsTask());
	public async Task<bool> PlayAsync() => await TryAsync((s) => s.TryPlayAsync().AsTask());
	public async Task<bool> SkipAsync() => await TryAsync((s) => s.TrySkipNextAsync().AsTask());

	async Task<bool> TryAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> press)
	{
		var current = session;
		if(current == null) return false;
		try { return await press(current); } catch(Exception) { return false; }
	}

	public void Dispose() => Unfollow();
}
