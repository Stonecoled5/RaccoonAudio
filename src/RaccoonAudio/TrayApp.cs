using System.Diagnostics;
using System.Drawing.Drawing2D;
using RaccoonAudio.Audio;
using RaccoonAudio.Core;
using RaccoonAudio.Media;

namespace RaccoonAudio;

// The app itself: an icon by the clock, the window it opens, and the pieces behind them. It keeps a connection to
// raccoon man, and while it's live it captures the chosen program's sound, encodes it, and sends it.
//
// Two rules it never breaks:
// - Sound is only captured while a stream is live. The moment a stream ends, for any reason, capturing stops.
// - Only the chosen program is captured, never the whole computer.
//
// While live it can also turn that program down to almost nothing on this computer, so the person streaming hears
// the music once, from raccoon man, instead of twice. Whatever it turns down it puts back: when the stream ends, when
// the app closes, and, if the app ended badly, the next time it starts.
sealed class TrayApp : ApplicationContext
{
	readonly Settings settings = Settings.Load();
	readonly StreamClient client;
	readonly ProcessLoopbackCapture capture = new();
	readonly OpusFramer framer = new();
	readonly MediaWatcher media = new();
	readonly NotifyIcon tray = new();
	readonly ToolStripMenuItem liveItem = new("Go live");
	readonly SynchronizationContext ui;
	readonly Icon idleIcon = Dot(Color.FromArgb(140, 140, 140));
	readonly Icon readyIcon = Dot(Color.FromArgb(70, 160, 90));
	readonly Icon liveIcon = Dot(Color.FromArgb(220, 50, 50));
	// The same, with a mark for song requests that haven't been looked at
	readonly Icon idleMarked = Dot(Color.FromArgb(140, 140, 140), true);
	readonly Icon readyMarked = Dot(Color.FromArgb(70, 160, 90), true);
	readonly Icon liveMarked = Dot(Color.FromArgb(220, 50, 50), true);
	// The songs people have asked for with /radio add, until they're ticked off
	readonly RequestList requests = new();
	readonly ToolStripMenuItem requestsItem = new("Requests");
	// How many requests the icon's menu lists before it just counts the rest
	const int MenuRequests = 10;
	// The list is redrawn a moment after it changes, so a burst of requests is one redraw, not one each
	readonly System.Windows.Forms.Timer requestsRedraw = new() { Interval = 100 };
	// What the window was last told the app is doing, and when it last went live
	ClientState shownState = ClientState.Off;
	DateTime wentLiveAt = DateTime.MinValue;
	MainForm? window;

	// How far the program is turned down on this computer while live, when that's wanted: a thousandth, which is
	// as good as silent, and what's sent is turned up by the same
	public const float QuietLevel = 0.001f;
	// The program turned down for the stream, and the volume to put it back to, while one is
	string quietedProgram = "";
	float? quietedFrom;
	readonly System.Windows.Forms.Timer everySecond = new() { Interval = 1000 };

	public Settings Settings => settings;
	public ClientState State => client.State;
	// The last thing worth telling the person, shown in the window
	public string LastWord { get; private set; } = "";
	public bool HasKey => client.HasKey;
	// Go live was pressed and it hasn't been stopped, whether or not he's playing yet
	public bool WantsLive => client.WantsLive;

	public TrayApp(bool startHidden)
	{
		// The window's thread: everything that touches the window or the icon is sent back here. It's made here, on
		// that thread, and never taken from whatever context happens to be current: before the first control exists
		// that can be a plain one that runs things on background threads, and a control first shown from a background
		// thread freezes the window.
		ui = new WindowsFormsSynchronizationContext();
		SynchronizationContext.SetSynchronizationContext(ui);
		client = new StreamClient(settings.ServerUri() ?? new Uri(Settings.DefaultServer), Settings.LoadKey());
		client.StateChanged += (state) => ui.Post((_) => OnState(state), null);
		client.Said += (message) => ui.Post((_) => OnSaid(message), null);
		client.Problem += (text) => ui.Post((_) => Tell(text, true), null);
		client.Paired += (key) => ui.Post((_) => { Settings.SaveKey(key); Tell("This computer is paired. Join a voice channel and press Go live.", true); }, null);
		client.KeyRejected += () => ui.Post((_) => Settings.ForgetKey(), null);
		capture.Sound += (buffer, count) => framer.Push(buffer.AsSpan(0, count), client.SendAudio);
		capture.Failed += (err) => ui.Post((_) => { client.StopLive(); Tell("Couldn't capture " + settings.Program + "'s sound, so the stream was stopped. " + err.Message, true); }, null);
		media.Changed += (title, artist, playing) => client.SendNow(title, artist, playing);

		var menu = new ContextMenuStrip();
		menu.Items.Add("Open Raccoon Audio", null, (_, _) => ShowWindow());
		menu.Items.Add(liveItem);
		menu.Items.Add(requestsItem);
		// Opening the menu is seeing how many there are
		menu.Opening += (_, _) => RequestsSeen();
		// Clicking a pop-up by the clock opens the window, where the requests are
		tray.BalloonTipClicked += (_, _) => ShowWindow();
		requestsRedraw.Tick += (_, _) =>
		{
			requestsRedraw.Stop();
			DrawRequests();
		};
		DrawRequests();
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
		liveItem.Click += (_, _) => ToggleLive();
		tray.ContextMenuStrip = menu;
		tray.Icon = idleIcon;
		tray.Text = "Raccoon Audio";
		tray.Visible = true;
		tray.DoubleClick += (_, _) => ShowWindow();

		if(!ProcessLoopbackCapture.Supported) LastWord = "This needs Windows 11. On this version of Windows it can't capture one program's sound, and it won't capture the whole computer instead.";
		// It ended badly last time while a program was turned down for a stream: put that back
		if(settings.QuietedProgram != "")
		{
			AudioPrograms.TurnBackUp(settings.QuietedProgram, QuietLevel, settings.QuietedFrom);
			settings.QuietedProgram = "";
			settings.Save();
		}
		AppDomain.CurrentDomain.ProcessExit += (_, _) => Louden();
		AppDomain.CurrentDomain.UnhandledException += (_, _) => Louden();
		everySecond.Tick += (_, _) => EverySecond();
		everySecond.Start();
		client.Start();
		if(!startHidden) ShowWindow();
	}

	// --- What the window and the menu ask for ---

	public void ShowWindow()
	{
		if(window == null || window.IsDisposed) window = new MainForm(this);
		window.Show();
		window.WindowState = FormWindowState.Normal;
		window.Activate();
		window.Render();
		RequestsSeen();
	}

	// --- Song requests ---

	public IReadOnlyList<SongRequest> Requests => requests.All;

	// Finds the request in Spotify: a search for what was typed, or the very thing if they sent a Spotify link.
	// Adding it to the queue there is the streamer's to do.
	public void OpenInSpotify(SongRequest request)
	{
		try
		{
			Process.Start(new ProcessStartInfo(Spotify.AppLinkFor(request.Song)) { UseShellExecute = true })?.Dispose();
		}
		catch(Exception)
		{
			// No Spotify program on this computer to hand it to: the same thing in a web browser
			try
			{
				Process.Start(new ProcessStartInfo(Spotify.WebLinkFor(request.Song)) { UseShellExecute = true })?.Dispose();
			}
			catch(Exception)
			{
				Tell("Couldn't open Spotify for that request.", false);
			}
		}
	}

	// Played, or not wanted: it comes off the list, and whoever asked can ask for another
	public void RequestDone(SongRequest request)
	{
		if(requests.Done(request)) RequestsChanged();
	}

	public void ClearRequests()
	{
		// Live, the bot is told in one message. Otherwise it's told about each as soon as there's a stream again.
		if(client.SendClear()) requests.ClearTold();
		else requests.Clear();
		RequestsChanged();
	}

	public void RequestsSeen()
	{
		if(requests.Unseen == 0) return;
		requests.Seen();
		tray.Icon = IconFor(client.State);
	}

	// The list changed: the icon, its menu and the window are brought in line in a moment
	void RequestsChanged()
	{
		if(!requestsRedraw.Enabled) requestsRedraw.Start();
	}

	void DrawRequests()
	{
		tray.Icon = IconFor(client.State);
		var all = requests.All;
		requestsItem.Text = all.Count == 0 ? "Requests" : "Requests (" + all.Count + ")";
		var items = requestsItem.DropDownItems;
		while(items.Count > 0)
		{
			var old = items[0];
			items.RemoveAt(0);
			old.Dispose();
		}
		if(all.Count == 0) items.Add(new ToolStripMenuItem("None yet. People ask with /radio add.") { Enabled = false });
		foreach(var request in all.Take(MenuRequests))
		{
			var item = new ToolStripMenuItem(InMenu(Short(request.Song, 45) + "   (" + Short(request.By, 20) + ")")) { ToolTipText = "Open in Spotify" };
			item.Click += (_, _) => OpenInSpotify(request);
			items.Add(item);
		}
		if(all.Count > MenuRequests) items.Add(new ToolStripMenuItem("…and " + (all.Count - MenuRequests) + " more in the window") { Enabled = false });
		items.Add(new ToolStripSeparator());
		items.Add("Show them all…", null, (_, _) => ShowWindow());
		if(all.Count > 0) items.Add("Clear all", null, (_, _) => ClearRequests());
		if(window is { IsDisposed: false }) window.ShowRequests(all);
	}

	static string Short(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";
	// A menu reads "&" as "underline the next letter"
	static string InMenu(string text) => text.Replace("&", "&&");

	Icon IconFor(ClientState state)
	{
		var marked = requests.Unseen > 0;
		return state == ClientState.Live ? (marked ? liveMarked : liveIcon) : state == ClientState.Connected ? (marked ? readyMarked : readyIcon) : (marked ? idleMarked : idleIcon);
	}

	public void ToggleLive()
	{
		if(client.State == ClientState.Live || client.WantsLive)
		{
			client.StopLive();
			StopCapture();
			Refresh();
			return;
		}
		if(!ProcessLoopbackCapture.Supported)
		{
			Tell("This needs Windows 11 to capture one program's sound.", true);
			return;
		}
		if(AudioPrograms.ProcessFor(settings.Program) == null)
		{
			Tell(settings.Program + " isn't running. Start it, then go live.", true);
			return;
		}
		LastWord = "";
		client.GoLive();
		Refresh();
	}

	public void Pair(string code)
	{
		LastWord = "Pairing…";
		client.Pair(code, Environment.MachineName);
		Refresh();
	}

	// Forgets this computer's key here. It stays listed in /me until it's unpaired there too.
	public async Task UnpairAsync()
	{
		await client.StopAsync();
		StopCapture();
		Settings.ForgetKey();
		client.ForgetKey();
		client.Start();
		Tell("This computer's key is forgotten. To finish, unpair it in /me → Audio app in Discord as well.", false);
	}

	public void ChooseProgram(string name)
	{
		if(string.Equals(settings.Program, name, StringComparison.OrdinalIgnoreCase)) return;
		settings.Program = name;
		settings.Save();
		// Changing it mid-stream switches what's being sent
		if(client.State == ClientState.Live)
		{
			Louden();
			StartCapture();
		}
	}

	public void SetQuietWhileLive(bool quiet)
	{
		settings.QuietWhileLive = quiet;
		settings.Save();
		if(client.State != ClientState.Live) return;
		if(quiet) Quieten();
		else Louden();
	}

	public void SetSkipAutomatically(bool automatically)
	{
		settings.SkipAutomatically = automatically;
		settings.Save();
	}

	// Shares the Jam link now if live, and keeps it for the next stream. False if it isn't a Spotify Jam link.
	public bool ShareJam(string link)
	{
		if(link.Trim() != "" && !Protocol.IsJamLink(link)) return false;
		settings.JamLink = link.Trim();
		settings.Save();
		if(settings.JamLink != "") client.SendJam(settings.JamLink);
		return true;
	}

	// Points the app at another server. False if the address isn't one it will use.
	public async Task<bool> SetServerAsync(string address)
	{
		var before = settings.Server;
		settings.Server = address.Trim();
		var url = settings.ServerUri();
		if(url == null)
		{
			settings.Server = before;
			return false;
		}
		settings.Save();
		await client.StopAsync();
		StopCapture();
		client.Server = url;
		client.Start();
		return true;
	}

	public void TryAgain() => client.Retry();

	public async Task ExitAsync()
	{
		tray.Visible = false;
		everySecond.Stop();
		StopCapture();
		await client.StopAsync();
		client.Dispose();
		media.Dispose();
		capture.Dispose();
		everySecond.Dispose();
		requestsRedraw.Dispose();
		tray.Dispose();
		ExitThread();
	}

	// --- What the bot and the connection do ---

	void OnState(ClientState state)
	{
		shownState = state;
		if(state == ClientState.Live) wentLiveAt = DateTime.UtcNow;
		if(state == ClientState.Live)
		{
			LastWord = "";
			StartCapture();
		}
		else StopCapture();
		tray.Icon = IconFor(state);
		tray.Text = "Raccoon Audio: " + Describe(state);
		liveItem.Text = state == ClientState.Live || client.WantsLive ? "Stop" : "Go live";
		liveItem.Enabled = state is ClientState.Connected or ClientState.Live or ClientState.Reconnecting;
		Refresh();
	}

	async void OnSaid(ServerMessage message)
	{
		switch(message.Type)
		{
			case "pause":
				var paused = await media.PauseAsync();
				Tell((paused ? "Paused: " : "Asked to pause, but " + settings.Program + " has no pause button I can press: ") + message.Text("reason") + ".", true);
				break;
			case "resume":
				await media.PlayAsync();
				Tell("Playing again.", false);
				break;
			case "skip":
				if(settings.SkipAutomatically && await media.SkipAsync()) Tell("The channel voted to skip, so I skipped it.", true);
				else Tell("The channel voted to skip this song.", true);
				break;
			case "request":
				// It goes on the list, and pops up by the clock unless the window is what's being looked at
				var looking = window is { IsDisposed: false, Visible: true } && window.WindowState != FormWindowState.Minimized && Form.ActiveForm == window;
				if(requests.Add(message.Number("id"), message.Text("by"), message.Text("song"), DateTime.Now, seen: looking) == null) break;
				// Coming back to a stream, the bot says again what's waiting. Those go on the list without a pop-up each.
				var catchingUp = shownState != ClientState.Live || DateTime.UtcNow - wentLiveAt < TimeSpan.FromSeconds(3);
				if(!looking && !catchingUp) tray.ShowBalloonTip(5000, message.Text("by") == "" ? "Song request" : "Song request from " + Short(message.Text("by"), 40), Short(message.Text("song"), 200), ToolTipIcon.None);
				RequestsChanged();
				break;
			case "notify":
				Tell(message.Text("text"), true);
				break;
			case "ended":
				// "stopped" is this app's own Stop coming back; anything else is the bot saying why
				if(message.Text("reason") != "stopped") Tell(message.Text("reason"), true);
				break;
		}
	}

	void StartCapture()
	{
		var process = AudioPrograms.ProcessFor(settings.Program);
		if(process == null)
		{
			client.StopLive();
			Tell(settings.Program + " isn't running any more, so the stream was stopped.", true);
			return;
		}
		framer.Reset();
		capture.Start(process.Value);
		Quieten();
		_ = FollowMediaAsync();
	}

	async Task FollowMediaAsync()
	{
		await media.StartAsync(settings.Program);
		// The bot hears what's on as the stream starts, not only when the song next changes
		media.Repeat();
		if(settings.JamLink != "") client.SendJam(settings.JamLink);
	}

	void StopCapture()
	{
		capture.Stop();
		Louden();
		media.Stop();
	}

	// Turns the program down to almost nothing on this computer, if that's wanted, and has what's captured turned
	// up to match. What's captured is only turned up once the program really is turned down: turned up when it
	// isn't, a quiet passage would come out as a roar.
	void Quieten()
	{
		if(!settings.QuietWhileLive)
		{
			capture.QuietLevel = 1;
			return;
		}
		var was = AudioPrograms.TurnDown(settings.Program, QuietLevel);
		if(was != null && quietedFrom == null)
		{
			quietedFrom = was;
			quietedProgram = settings.Program;
			settings.QuietedProgram = quietedProgram;
			settings.QuietedFrom = was.Value;
			settings.Save();
		}
		capture.QuietLevel = AudioPrograms.TurnedDown(settings.Program, QuietLevel) ? QuietLevel : 1;
	}

	// Puts the program's volume back. Safe to call when nothing was turned down, and from any thread.
	void Louden()
	{
		capture.QuietLevel = 1;
		var from = quietedFrom;
		if(from == null) return;
		quietedFrom = null;
		AudioPrograms.TurnBackUp(quietedProgram, QuietLevel, from.Value);
		quietedProgram = "";
		settings.QuietedProgram = "";
		settings.Save();
	}

	void EverySecond()
	{
		// A program that opened its sound after the stream started, or whose slider was moved, is turned down again
		if(client.State == ClientState.Live)
		{
			Quieten();
			// Requests ticked off since last time: the bot is told a couple a second, well inside how fast it listens
			foreach(var id in requests.NextToTell(2)) client.SendDone(id);
		}
		if(window is { IsDisposed: false, Visible: true }) window.ShowSound(SoundStatus());
	}

	// Whether the chosen program is making sound right now, in words, for under the list of programs
	public string SoundStatus()
	{
		var name = settings.Program;
		if(AudioPrograms.ProcessFor(name) == null) return name + " isn't running.";
		var live = client.State == ClientState.Live;
		if(!AudioPrograms.MakingSound(name, quietedFrom != null ? QuietLevel : 1)) return name + " is quiet right now." + (live ? " Nothing goes out until it plays." : "");
		if(!live) return name + " is making sound right now.";
		return quietedFrom != null ? name + " is playing. It's turned down on this computer while you're live, so you hear it through raccoon man." : name + " is playing, and going out to the voice channel.";
	}

	// Something to tell the person: kept for the window, and popped up by the clock if it matters when they're
	// looking at something else
	void Tell(string text, bool popUp)
	{
		if(string.IsNullOrWhiteSpace(text)) return;
		LastWord = text;
		if(popUp && (window == null || !window.Visible || window.WindowState == FormWindowState.Minimized || Form.ActiveForm != window))
		{
			tray.ShowBalloonTip(5000, "Raccoon Audio", text.Length > 240 ? text[..240] : text, ToolTipIcon.None);
		}
		Refresh();
	}

	void Refresh()
	{
		if(window is { IsDisposed: false }) window.Render();
	}

	public static string Describe(ClientState state) => state switch
	{
		ClientState.NeedsPairing => "not paired yet",
		ClientState.Connecting or ClientState.SigningIn => "connecting…",
		ClientState.Connected => "connected, not streaming",
		ClientState.Live => "LIVE in voice",
		ClientState.Reconnecting => "reconnecting…",
		ClientState.NeedsUpdate => "needs an update",
		_ => "not connected"
	};

	// The icon by the clock: a coloured dot. Grey when idle, green when connected, red while live. Marked, it has a
	// small yellow dot in the corner.
	static Icon Dot(Color colour, bool marked = false)
	{
		using var image = new Bitmap(32, 32);
		using(var drawing = Graphics.FromImage(image))
		{
			drawing.SmoothingMode = SmoothingMode.AntiAlias;
			drawing.Clear(Color.Transparent);
			using var fill = new SolidBrush(colour);
			drawing.FillEllipse(fill, 3, 3, 26, 26);
			using var ring = new Pen(Color.FromArgb(40, 40, 40), 2);
			drawing.DrawEllipse(ring, 3, 3, 26, 26);
			if(marked)
			{
				using var mark = new SolidBrush(Color.FromArgb(255, 205, 40));
				drawing.FillEllipse(mark, 17, 0, 15, 15);
				drawing.DrawEllipse(ring, 17, 0, 14, 14);
			}
		}
		return Icon.FromHandle(image.GetHicon());
	}
}
