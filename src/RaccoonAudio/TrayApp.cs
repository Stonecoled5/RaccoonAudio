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
	MainForm? window;

	public Settings Settings => settings;
	public ClientState State => client.State;
	// The last thing worth telling the person, shown in the window
	public string LastWord { get; private set; } = "";
	public bool HasKey => client.HasKey;
	// Go live was pressed and it hasn't been stopped, whether or not he's playing yet
	public bool WantsLive => client.WantsLive;

	public TrayApp(bool startHidden)
	{
		// The window's thread: everything that touches the window or the icon is sent back here
		ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
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
		menu.Items.Add(new ToolStripSeparator());
		menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
		liveItem.Click += (_, _) => ToggleLive();
		tray.ContextMenuStrip = menu;
		tray.Icon = idleIcon;
		tray.Text = "Raccoon Audio";
		tray.Visible = true;
		tray.DoubleClick += (_, _) => ShowWindow();

		if(!ProcessLoopbackCapture.Supported) LastWord = "This needs Windows 11. On this version of Windows it can't capture one program's sound, and it won't capture the whole computer instead.";
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
		if(client.State == ClientState.Live) StartCapture();
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
		StopCapture();
		await client.StopAsync();
		client.Dispose();
		media.Dispose();
		capture.Dispose();
		tray.Dispose();
		ExitThread();
	}

	// --- What the bot and the connection do ---

	void OnState(ClientState state)
	{
		if(state == ClientState.Live)
		{
			LastWord = "";
			StartCapture();
		}
		else StopCapture();
		tray.Icon = state == ClientState.Live ? liveIcon : state == ClientState.Connected ? readyIcon : idleIcon;
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
				Tell(message.Text("by") + " asked for: " + message.Text("song"), true);
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
		media.Stop();
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

	// The icon by the clock: a coloured dot. Grey when idle, green when connected, red while live.
	static Icon Dot(Color colour)
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
		}
		return Icon.FromHandle(image.GetHicon());
	}
}
