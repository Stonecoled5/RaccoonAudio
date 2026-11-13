using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
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

	// Updates: where it looks, what it found, and whether it's in the middle of looking or of updating. It looks when
	// it starts and about once a day, and it changes nothing until the person presses Update.
	readonly Startup startup;
	readonly Updater updater;
	readonly ToolStripMenuItem updateItem = new("Update") { Visible = false };
	readonly CancellationTokenSource stopUpdate = new();
	Release? newer;
	bool checking;
	bool updating;
	bool closed;
	DateTime nextCheck = DateTime.UtcNow;
	DateTime lastCheck = DateTime.MinValue;
	// The first look for a new version, made as soon as the app starts, hasn't finished yet
	bool startupCheck = true;
	// The window asking whether to update, while it's open
	UpdateForm? asking;
	// The version that's running: the three numbers the release was built with
	public static AppVersion Version { get; } = VersionOf(typeof(TrayApp).Assembly.GetName().Version);
	static AppVersion VersionOf(System.Version? built) => built == null ? new AppVersion(0, 0, 0) : new AppVersion(built.Major, built.Minor, Math.Max(built.Build, 0));
	// A newer release, once one's been found
	public Release? Newer => newer;
	public bool Updating => updating;

	public Settings Settings => settings;
	public ClientState State => client.State;
	// The last thing worth telling the person, shown in the window
	public string LastWord { get; private set; } = "";
	public bool HasKey => client.HasKey;
	// Go live was pressed and it hasn't been stopped, whether or not he's playing yet
	public bool WantsLive => client.WantsLive;

	public TrayApp(Startup startup)
	{
		this.startup = startup;
		updater = new Updater(startup.Updates);
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
		menu.Items.Add(updateItem);
		updateItem.Click += (_, _) => StartUpdate();
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
		if(!startup.Hidden) ShowWindow();
		Report("running version " + Version + (startup.AfterUpdate ? ", started by the copy it replaced" : ""));
		if(startup.AfterUpdate) Tell("Updated to version " + Version + ".", true);
		// What an update leaves beside the app, above all the old version, is deleted. After an update the old copy
		// can take a moment to finish closing, and its file can't be deleted until it has.
		if(Published) _ = Task.Run(async () =>
		{
			for(var tries = 0; tries < 20 && !ExeSwap.CleanUp(Environment.ProcessPath!); tries++) await Task.Delay(500);
		});
	}

	// Starts the connection to raccoon man. Separate from setting up, so that a copy taking over after an update
	// can set up while the copy it replaces is still connected, and connect once that one has closed.
	public void Connect() => client.Start();

	// Set up but never run, because another copy turned out to be running: the icon by the clock goes
	public void Abandon()
	{
		tray.Visible = false;
		tray.Dispose();
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

	// --- Updates ---

	// Whether this is the app as it's released: one file, with nothing of its own beside it. A development build has
	// its parts next to it, and swapping its exe for a released one would leave a muddle, so it never updates itself.
	static bool Published => Environment.ProcessPath is string exe && !File.Exists(Path.ChangeExtension(exe, ".dll"));

	// Looks for a newer release: one request to GitHub that says nothing about the person or the computer. When the
	// app does this by itself it says nothing unless there is one, and a look that fails is tried again in an hour.
	// When the person asked, it says what it found either way.
	public async Task CheckForUpdateAsync(bool asked)
	{
		if(checking || updating) return;
		checking = true;
		lastCheck = DateTime.UtcNow;
		var atStartup = startupCheck;
		var action = UpdateAction.Nothing;
		try
		{
			var found = await Task.Run(() => updater.CheckAsync(Version));
			nextCheck = DateTime.UtcNow.AddDays(1);
			newer = found;
			if(found == null)
			{
				Report("nothing newer than version " + Version);
				if(asked) Tell("You have the newest version (" + Version + ").", false);
			}
			else
			{
				Report("found version " + found.Version);
				// Trying an update without anyone to click is handled below, with nothing put in front of anyone
				if(startup.UpdateReport == null) action = UpdatePolicy.Decide(atStartup, settings.UpdateOnStartup, CanInstallNow, asked, settings.UpdateOffered == found.Tag);
				if(action != UpdateAction.Nothing && settings.UpdateOffered != found.Tag)
				{
					settings.UpdateOffered = found.Tag;
					settings.Save();
				}
			}
		}
		catch(Exception err)
		{
			nextCheck = DateTime.UtcNow.AddHours(1);
			Report("the look for a new version failed: " + err.Message);
			if(asked) Tell(err is UpdateException ? err.Message : "Couldn't look for a new version. " + err.Message, false);
		}
		checking = false;
		startupCheck = false;
		// It was closed while it was looking
		if(closed) return;
		ShowUpdate();
		if(action == UpdateAction.Install)
		{
			Report("installing version " + newer!.Version + " at startup");
			StartUpdate();
		}
		// At startup with the setting on, it would have installed by itself: say why it didn't
		else if(action == UpdateAction.Ask) AskToUpdate(atStartup && settings.UpdateOnStartup ? "It would have installed by itself, but " +
			(Published ? "you're live. Stop the stream first: updating closes the app and opens the new version." : "this copy was built for development, so it doesn't update itself.") : "");
		// Trying an update without anyone to click: take what's offered, or close when there's nothing to take
		if(startup.UpdateReport != null)
		{
			if(newer != null) StartUpdate();
			else await ExitAsync();
		}
	}

	// Whether an update could start right now without stopping anything: not live, and a released copy
	bool CanInstallNow => Published && client.State != ClientState.Live && !client.WantsLive;

	// A window in front of everything else, saying a newer version is out, with Update now and Later. Only one at a time.
	void AskToUpdate(string why = "")
	{
		var release = newer;
		if(release == null || updating) return;
		if(asking is { IsDisposed: false })
		{
			asking.Activate();
			return;
		}
		asking = new UpdateForm(release.Version, Version, why);
		asking.UpdateNow += StartUpdate;
		asking.FormClosed += (_, _) => asking = null;
		asking.Show();
		asking.Activate();
	}

	// The person's setting for installing what the look at startup finds
	// Opens or folds away the requests side of the window, and remembers it
	public void SetRequestsOpen(bool open)
	{
		settings.RequestsOpen = open;
		settings.Save();
		if(window != null && !window.IsDisposed) window.ShowRequestsSide();
	}

	public void SetUpdateOnStartup(bool on)
	{
		settings.UpdateOnStartup = on;
		settings.Save();
	}

	// The person pressed Update. Downloads the release from GitHub, checks it against the checksum GitHub publishes
	// for it, starts it once to see that it runs, swaps it in for this copy, starts it for real and closes. If
	// anything goes wrong at any step, this copy is still the one on disk, still running, and says what happened.
	public async void StartUpdate()
	{
		var release = newer;
		if(release == null || updating) return;
		if(client.State == ClientState.Live || client.WantsLive)
		{
			Tell("Stop the stream first, then press Update. Updating closes the app and opens the new version.", true);
			return;
		}
		var exe = Environment.ProcessPath;
		if(exe == null || !Published)
		{
			Tell("This copy was built for development, so it doesn't update itself.", true);
			return;
		}
		updating = true;
		// Updating from the window or the menu while the question is still open: it's been answered
		if(asking is { IsDisposed: false }) asking.Close();
		var swapped = false;
		var launched = false;
		string? wrong = null;
		try
		{
			Report("updating to version " + release.Version);
			Progress("Downloading version " + release.Version + "…");
			var fresh = await Task.Run(() => updater.FetchAsync(release, exe, (percent) => ui.Post((_) => Progress("Downloading version " + release.Version + "… " + percent + "%"), null), stopUpdate.Token));
			stopUpdate.Token.ThrowIfCancellationRequested();
			// Before anything is swapped, the new version is started once, from where it was unpacked, to see that
			// it runs on this computer at all
			Progress("Checking that version " + release.Version + " starts…");
			wrong = await StartCopyAsync(fresh, tryOut: true);
			if(wrong == null)
			{
				Report("the new version starts");
				stopUpdate.Token.ThrowIfCancellationRequested();
				// From the moment this copy's file is renamed aside it can't fetch any part of itself it hasn't
				// used yet (see ExeSwap), so everything is loaded now. Starting the new version just above also
				// means everything that starting it again needs has already been used once.
				var more = await Task.Run(LoadEverything);
				Report("loaded the " + more + " parts of this version that hadn't been needed yet");
				// The last moment Exit can still leave everything as it was
				stopUpdate.Token.ThrowIfCancellationRequested();
				ExeSwap.Replace(exe, fresh);
				swapped = true;
				Report("the new version is in place, starting it");
				Progress("Starting version " + release.Version + "…");
				wrong = await StartCopyAsync(exe, tryOut: false);
				launched = wrong == null;
			}
		}
		catch(OperationCanceledException)
		{
			// Exit was chosen part way through
		}
		catch(UpdateException err)
		{
			wrong = err.Message;
		}
		catch(Exception err)
		{
			Report("unexpected: " + err);
			wrong = "Something unexpected went wrong. " + (err.Message != "" ? err.Message : err.GetType().Name);
		}
		if(!launched)
		{
			// The old version goes back where it was, if it had been moved at all, and what was downloaded goes
			try
			{
				if(swapped || !File.Exists(exe)) ExeSwap.PutBack(exe);
				ExeSwap.CleanUp(exe);
			}
			catch(Exception err)
			{
				wrong = (wrong ?? "") + " The old version couldn't be put back by itself (" + err.Message + "): look for " + Path.GetFileName(ExeSwap.OldPath(exe)) + " next to the app and rename it back to " + Path.GetFileName(exe) + ".";
			}
		}
		updating = false;
		// The new copy is up and waiting for this one to close. Or Exit was chosen, and this copy is as it was.
		if(launched || stopUpdate.IsCancellationRequested)
		{
			try
			{
				await ExitAsync();
			}
			catch(Exception)
			{
				// Closing is all that's left to do, whatever went wrong on the way out
				Environment.Exit(0);
			}
			return;
		}
		Tell("Couldn't update. " + wrong + " You're still on version " + Version + ", and nothing was changed.", true);
		ShowUpdate();
		if(startup.UpdateReport != null)
		{
			Report("still running version " + Version);
			await ExitAsync();
		}
	}

	// Starts a copy of the app and waits to hear from it. With tryOut, it's the new version being started once to see
	// that it runs: it says it started and closes again. Without, it's the new version taking over: it sets itself
	// up, says it's ready, and waits for this copy to close. Null if that's what happened; if not, what went wrong,
	// with the copy stopped.
	async Task<string?> StartCopyAsync(string path, bool tryOut)
	{
		var failed = tryOut ? "The new version didn't start when it was tried, so it wasn't put in place." : "The new version wouldn't start, so the old one was put back.";
		Process? copy = null;
		try
		{
			var name = Program.UpdateStartedName(startup.Separate);
			using var started = new EventWaitHandle(false, EventResetMode.ManualReset, name);
			// It starts in the folder this copy was started in, so a folder given on the command line still means
			// the same place
			var start = new ProcessStartInfo(path) { UseShellExecute = false };
			if(tryOut)
			{
				start.ArgumentList.Add("--self-check");
				start.ArgumentList.Add(name);
			}
			else
			{
				// It starts the way this copy was started, except that it knows it comes after an update, and it
				// opens its window if this one's is open
				foreach(var arg in startup.Args.Where((a) => a is not "--tray" and not "--after-update")) start.ArgumentList.Add(arg);
				start.ArgumentList.Add("--after-update");
				if(window is not { IsDisposed: false, Visible: true }) start.ArgumentList.Add("--tray");
			}
			copy = Process.Start(start);
			if(copy == null) return failed;
			var heard = Task.Run(() => started.WaitOne(TimeSpan.FromSeconds(60)));
			var ended = copy.WaitForExitAsync();
			await Task.WhenAny(heard, ended);
			// Whether it said it started, however the wait ended. Then the wait is let go, before the thing it
			// waits on is put away.
			var said = started.WaitOne(0);
			started.Set();
			await heard;
			if(said && tryOut)
			{
				// Tried out, it closes by itself straight after
				await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(30)));
				if(copy.HasExited && copy.ExitCode == 0) return null;
			}
			else if(said && !copy.HasExited) return null;
			Stop(copy);
			return failed;
		}
		catch(Exception err)
		{
			if(copy != null) Stop(copy);
			// Windows' own reason, without the file names .NET wraps round it
			return failed + " " + (err is System.ComponentModel.Win32Exception windows ? new System.ComponentModel.Win32Exception(windows.NativeErrorCode).Message.Replace("%1", "the file") : err.Message);
		}
		finally
		{
			copy?.Dispose();
		}
	}

	// Loads every part of the app that hasn't been needed yet: everything the loaded parts refer to, and then
	// everything else the app was published with. How many that was.
	static int LoadEverything()
	{
		var count = 0;
		var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var waiting = new Queue<Assembly>();
		foreach(var loaded in AppDomain.CurrentDomain.GetAssemblies())
		{
			if(loaded.GetName().Name is string name) known.Add(name);
			waiting.Enqueue(loaded);
		}
		void Load(AssemblyName part)
		{
			if(part.Name == null || !known.Add(part.Name)) return;
			try
			{
				waiting.Enqueue(Assembly.Load(part));
				count++;
			}
			catch(Exception)
			{
				// Not a part of this app, or not one that can be loaded on its own
			}
		}
		while(waiting.Count > 0) foreach(var reference in waiting.Dequeue().GetReferencedAssemblies()) Load(reference);
		if(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string all)
		{
			foreach(var file in all.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) Load(new AssemblyName(Path.GetFileNameWithoutExtension(file)));
			while(waiting.Count > 0) foreach(var reference in waiting.Dequeue().GetReferencedAssemblies()) Load(reference);
		}
		return count;
	}

	static void Stop(Process process)
	{
		try
		{
			if(!process.HasExited) process.Kill();
			process.WaitForExit(10000);
		}
		catch(Exception)
		{
			// Already gone
		}
	}

	// The menu and the window are brought in line with whether there's an update and whether it's under way
	void ShowUpdate()
	{
		updateItem.Visible = newer != null;
		updateItem.Enabled = !updating;
		if(newer != null) updateItem.Text = updating ? "Updating…" : "Update to version " + newer.Version;
		Refresh();
	}

	// How an update is getting on, in the window
	void Progress(string text)
	{
		LastWord = text;
		ShowUpdate();
	}

	// For --update-test: what happened, written down as it happens
	void Report(string line)
	{
		if(startup.UpdateReport == null) return;
		try
		{
			File.AppendAllText(startup.UpdateReport, Version + ": " + line + Environment.NewLine);
		}
		catch(Exception)
		{
			// The report is only for a test
		}
	}

	// Whether the window is what the person is looking at
	bool Looking => window is { IsDisposed: false, Visible: true } && window.WindowState != FormWindowState.Minimized && Form.ActiveForm == window;

	public void ToggleLive()
	{
		// It's about to close and open again as the new version
		if(updating) return;
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

	// Unpairs this computer: the bot is asked to forget it (so it comes off the list in /me), then its key is forgotten
	// here. If the bot can't be reached, the key is still forgotten here, and the person is told to finish in /me.
	public async Task UnpairAsync()
	{
		var told = await client.UnpairAsync(TimeSpan.FromSeconds(5));
		await client.StopAsync();
		StopCapture();
		Settings.ForgetKey();
		client.ForgetKey();
		client.Start();
		Tell(told ? "This computer is unpaired, here and in /me." :
			"This computer's key is forgotten, but the bot couldn't be reached. To finish, unpair it in /me → Audio app in Discord as well.", false);
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
		// Part way through an update: the update is told to stop, and closes the app itself once this copy is back
		// as it was
		if(updating)
		{
			stopUpdate.Cancel();
			return;
		}
		closed = true;
		tray.Visible = false;
		everySecond.Stop();
		StopCapture();
		await client.StopAsync();
		client.Dispose();
		media.Dispose();
		capture.Dispose();
		updater.Dispose();
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
		// The bot says this version is too old: see whether there's a newer one to offer, unless it just looked
		if(state == ClientState.NeedsUpdate && newer == null && DateTime.UtcNow - lastCheck > TimeSpan.FromMinutes(1)) _ = CheckForUpdateAsync(false);
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
		// When it starts, and about once a day after
		if(DateTime.UtcNow >= nextCheck) _ = CheckForUpdateAsync(startup.UpdateReport != null);
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
		Report("said: " + text);
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

	// The icon by the clock: the raccoon, with a coloured dot in the corner for what it's doing. Grey when idle, green
	// when connected, red while live. Marked, it has a small yellow dot in the other corner.
	static Icon Dot(Color colour, bool marked = false)
	{
		using var image = new Bitmap(32, 32);
		using(var drawing = Graphics.FromImage(image))
		{
			drawing.SmoothingMode = SmoothingMode.AntiAlias;
			drawing.InterpolationMode = InterpolationMode.HighQualityBicubic;
			drawing.Clear(Color.Transparent);
			drawing.DrawImage(Theme.Picture, 0, 0, 32, 32);
			using var fill = new SolidBrush(colour);
			using var ring = new Pen(Color.FromArgb(27, 28, 38), 2);
			drawing.FillEllipse(fill, 19, 19, 12, 12);
			drawing.DrawEllipse(ring, 19, 19, 12, 12);
			if(marked)
			{
				using var mark = new SolidBrush(Color.FromArgb(255, 205, 40));
				drawing.FillEllipse(mark, 20, 0, 11, 11);
				drawing.DrawEllipse(ring, 20, 0, 11, 11);
			}
		}
		return Icon.FromHandle(image.GetHicon());
	}
}
