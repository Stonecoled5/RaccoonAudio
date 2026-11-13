using RaccoonAudio.Audio;
using RaccoonAudio.Core;

namespace RaccoonAudio;

// The app's one window. On the left: what it's doing, which program it streams, Go live, and the few settings. On the
// right, for the whole height of the window: the songs people have asked for, which scroll when there are more than
// fit. It's built in code rather than a designer file, and it only ever shows what TrayApp knows and asks TrayApp to
// do things.
sealed class MainForm : Form
{
	readonly TrayApp app;
	readonly Label status = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14, FontStyle.Bold) };
	readonly Label word = new() { AutoSize = true, MaximumSize = new Size(420, 0), UseMnemonic = false };
	// Only there when a newer version is out
	readonly Label updateLine = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 8, 3, 0) };
	readonly Button update = new() { Text = "Update", AutoSize = true };
	readonly FlowLayoutPanel updateRow;
	readonly Label version = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(3, 8, 3, 0) };
	readonly Button checkForUpdate = new() { Text = "Check for updates", AutoSize = true };
	readonly ComboBox programs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
	readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
	readonly Label sound = new() { AutoSize = true, MaximumSize = new Size(420, 0), UseMnemonic = false };
	readonly CheckBox quiet = new() { Text = "Don't play it on this computer while I'm live", AutoSize = true };
	readonly AccentButton live = new() { Text = "Go live", Width = 420, Height = 48 };
	readonly Button pair = new() { Text = "Pair this computer…", AutoSize = true };
	readonly RadioButton skipAuto = new() { Text = "Skip the song", AutoSize = true };
	readonly RadioButton skipTell = new() { Text = "Just tell me", AutoSize = true };
	readonly TextBox jam = new() { Width = 300, PlaceholderText = "https://spotify.link/…" };
	readonly Button shareJam = new() { Text = "Share", AutoSize = true };
	// Opens and closes the advanced settings, with an arrow that says which it is
	readonly Button advanced = new() { Text = Collapsed, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 3, 3, 3), FlatAppearance = { BorderSize = 0 } };
	readonly FlowLayoutPanel advancedPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, Visible = false, WrapContents = false };
	readonly TextBox server = new() { Width = 420 };
	readonly Button applyServer = new() { Text = "Use this address", AutoSize = true };
	readonly CheckBox startup = new() { Text = "Start with Windows (in the tray, not live)", AutoSize = true };
	readonly Button unpair = new() { Text = "Unpair this computer", AutoSize = true };
	readonly Label requestsTitle = new() { Text = "Requests", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold), Margin = new Padding(3, 4, 12, 3) };
	readonly Button clearRequests = new() { Text = "Clear all", AutoSize = true };
	readonly FlowLayoutPanel requestRows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill };
	bool rendering;

	public MainForm(TrayApp app)
	{
		this.app = app;
		Text = "Raccoon Audio";
		Padding = new Padding(14);
		StartPosition = FormStartPosition.CenterScreen;

		Icon = Theme.AppIcon;
		var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
		// The raccoon in headphones, with what the app is doing next to him
		var face = new PictureBox { Image = Theme.Picture, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Margin = new Padding(0, 0, 10, 0) };
		var title = new Label { Text = "Raccoon Audio", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 0) };
		var heading = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
		heading.Controls.Add(title);
		heading.Controls.Add(status);
		page.Controls.Add(Row(face, heading));
		page.Controls.Add(Spacer());
		page.Controls.Add(word);
		updateRow = Row(updateLine, update);
		updateRow.Visible = false;
		page.Controls.Add(updateRow);
		page.Controls.Add(Spacer());
		page.Controls.Add(Heading("Program to stream"));
		page.Controls.Add(Row(programs, refresh));
		page.Controls.Add(sound);
		page.Controls.Add(Note("Only this program's sound is sent, and only while you're live. Nothing else on your computer ever is."));
		page.Controls.Add(Spacer());
		page.Controls.Add(quiet);
		page.Controls.Add(Note("You hear it through raccoon man like everyone else, instead of twice. While you're live the program is turned almost all the way down in Windows' volume mixer, and put back when you stop."));
		page.Controls.Add(Spacer());
		page.Controls.Add(live);
		page.Controls.Add(pair);
		page.Controls.Add(Spacer());
		page.Controls.Add(Heading("When the channel votes to skip"));
		page.Controls.Add(Row(skipAuto, skipTell));
		page.Controls.Add(Spacer());
		page.Controls.Add(Heading("Spotify Jam link (optional)"));
		page.Controls.Add(Row(jam, shareJam));
		page.Controls.Add(Note("He posts it in the voice channel's chat so people can add songs from their own Spotify."));
		page.Controls.Add(Spacer());
		page.Controls.Add(advanced);
		advancedPanel.Controls.Add(Heading("Raccoon man's address"));
		// The button goes under the address, so the address can be as wide as everything else
		advancedPanel.Controls.Add(server);
		advancedPanel.Controls.Add(applyServer);
		advancedPanel.Controls.Add(startup);
		advancedPanel.Controls.Add(unpair);
		advancedPanel.Controls.Add(Row(version, checkForUpdate));
		page.Controls.Add(advancedPanel);

		// The left side keeps its own width and scrolls if the window is shorter than it; the right side takes
		// whatever room is left, so making the window bigger makes the list of requests bigger
		var left = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
		left.Controls.Add(page);
		var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(14, 0, 0, 0) };
		right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
		right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		right.Controls.Add(Row(requestsTitle, clearRequests), 0, 0);
		right.Controls.Add(requestRows, 0, 1);
		// Measured with the advanced settings open, so opening them never needs more room than the column has
		advancedPanel.Visible = true;
		var wanted = page.GetPreferredSize(Size.Empty);
		advancedPanel.Visible = false;
		var leftWidth = Math.Max(wanted.Width, 440) + SystemInformation.VerticalScrollBarWidth + 8;
		var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
		columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, leftWidth));
		columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
		columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
		columns.Controls.Add(left, 0, 0);
		columns.Controls.Add(right, 1, 0);
		Controls.Add(columns);
		// Tall enough for the left side once its text is filled in, without being taller than the screen
		var room = (Screen.PrimaryScreen?.WorkingArea.Height ?? 800) - 80;
		ClientSize = new Size(leftWidth + 380 + Padding.Horizontal, Math.Min(Math.Max(wanted.Height + 150, 600), room));
		MinimumSize = new Size(leftWidth + 300, 420);

		refresh.Click += (_, _) => FillPrograms();
		// Opening the list brings it up to date too
		programs.DropDown += (_, _) => FillPrograms();
		clearRequests.Click += (_, _) => app.ClearRequests();
		// The rows are as wide as the list, so long requests wrap instead of running off the side
		requestRows.ClientSizeChanged += (_, _) => FitRequests();
		// Looking at the window is seeing the requests
		Activated += (_, _) => app.RequestsSeen();
		quiet.CheckedChanged += (_, _) => { if(!rendering) app.SetQuietWhileLive(quiet.Checked); };
		programs.SelectedIndexChanged += (_, _) => { if(!rendering && programs.SelectedItem is AudioProgram chosen) app.ChooseProgram(chosen.Name); };
		live.Click += (_, _) => app.ToggleLive();
		update.Click += (_, _) => app.StartUpdate();
		checkForUpdate.Click += async (_, _) => await app.CheckForUpdateAsync(true);
		pair.Click += (_, _) => AskForCode();
		skipAuto.CheckedChanged += (_, _) => { if(!rendering && skipAuto.Checked) app.SetSkipAutomatically(true); };
		skipTell.CheckedChanged += (_, _) => { if(!rendering && skipTell.Checked) app.SetSkipAutomatically(false); };
		shareJam.Click += (_, _) =>
		{
			if(!app.ShareJam(jam.Text)) MessageBox.Show(this, "That doesn't look like a Spotify Jam link. It's the one Spotify gives you when you start a Jam, starting with https://spotify.link/ or https://open.spotify.com/.", Text);
		};
		advanced.Click += (_, _) =>
		{
			advancedPanel.Visible = !advancedPanel.Visible;
			advanced.Text = advancedPanel.Visible ? Expanded : Collapsed;
			if(advancedPanel.Visible) left.ScrollControlIntoView(advancedPanel);
		};
		applyServer.Click += async (_, _) =>
		{
			if(!await app.SetServerAsync(server.Text)) MessageBox.Show(this, "That address won't work. It has to start with wss:// (or ws://127.0.0.1 for a copy running on this computer).", Text);
		};
		startup.CheckedChanged += (_, _) => { if(!rendering) Settings.StartsWithWindows = startup.Checked; };
		unpair.Click += async (_, _) =>
		{
			if(MessageBox.Show(this, "Unpair this computer? It comes off your list in /me too, and you'll need a new pairing code to use it again.", Text, MessageBoxButtons.OKCancel) == DialogResult.OK) await app.UnpairAsync();
		};
		// Closing the window leaves it running by the clock. Exit is in the icon's menu.
		FormClosing += (_, closing) =>
		{
			if(closing.CloseReason != CloseReason.UserClosing) return;
			closing.Cancel = true;
			Hide();
		};
		Theme.Apply(this);
		HandleCreated += (_, _) => Theme.DarkTitleBar(this);
		FillPrograms();
		Render();
		ShowRequests(app.Requests);
	}

	const string Collapsed = "▸  Advanced";
	const string Expanded = "▾  Advanced";

	// The songs people have asked for, oldest first, each with a way to find it in Spotify and to tick it off
	public void ShowRequests(IReadOnlyList<SongRequest> requests)
	{
		var scrolled = requestRows.AutoScrollPosition;
		requestRows.SuspendLayout();
		while(requestRows.Controls.Count > 0)
		{
			var old = requestRows.Controls[0];
			requestRows.Controls.RemoveAt(0);
			old.Dispose();
		}
		requestsTitle.Text = requests.Count == 0 ? "Requests" : "Requests (" + requests.Count + ")";
		clearRequests.Visible = requests.Count > 0;
		if(requests.Count == 0) requestRows.Controls.Add(new Label { Text = "No requests yet. People in your voice channel ask for songs with /radio add, and they wait here until you tick them off.", AutoSize = true, ForeColor = Theme.Muted, Tag = "wrap" });
		foreach(var request in requests)
		{
			var song = new Label { Text = request.Song, AutoSize = true, UseMnemonic = false, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Tag = "wrap", Margin = new Padding(3, 0, 3, 0) };
			var who = new Label { Text = request.By + "  ·  " + request.At.ToShortTimeString(), AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, Tag = "wrap", Margin = new Padding(3, 0, 3, 2) };
			var open = new Button { Text = "Open in Spotify", AutoSize = true };
			var done = new Button { Text = "Done", AutoSize = true };
			open.Click += (_, _) => app.OpenInSpotify(request);
			done.Click += (_, _) => app.RequestDone(request);
			var row = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 10), Padding = new Padding(8) };
			row.Controls.Add(song);
			row.Controls.Add(who);
			row.Controls.Add(Row(open, done));
			Theme.Apply(row);
			// Each request is a card, a shade lighter than the window
			row.BackColor = Theme.Surface;
			foreach(Control part in row.Controls) if(part is FlowLayoutPanel) part.BackColor = Theme.Surface;
			requestRows.Controls.Add(row);
		}
		FitRequests();
		requestRows.ResumeLayout();
		requestRows.AutoScrollPosition = new Point(-scrolled.X, -scrolled.Y);
	}

	// Wraps the text in the list of requests at the list's width
	void FitRequests()
	{
		// Less the cards' own padding
		var width = Math.Max(requestRows.ClientSize.Width - 30, 120);
		foreach(Control row in requestRows.Controls)
		{
			if(Equals(row.Tag, "wrap")) row.MaximumSize = new Size(width, 0);
			foreach(Control part in row.Controls) if(Equals(part.Tag, "wrap")) part.MaximumSize = new Size(width, 0);
		}
	}

	static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 3, 3, 2) };
	static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText };
	static Control Spacer() => new Panel { Height = 8, Width = 10 };

	static FlowLayoutPanel Row(params Control[] controls)
	{
		var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
		row.Controls.AddRange(controls);
		return row;
	}

	// The programs with sound open, with the remembered one picked even if it isn't running right now
	const string NotRunning = "  (not running)";

	void FillPrograms()
	{
		rendering = true;
		var found = AudioPrograms.Find();
		var wanted = app.Settings.Program;
		// The chosen one stays in the list when it has no sound open, saying why it isn't among the others
		if(!found.Any((p) => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))) found.Insert(0, new AudioProgram(wanted, wanted + (AudioPrograms.ProcessFor(wanted) == null ? NotRunning : "  (no sound yet)"), 0, false));
		programs.Items.Clear();
		programs.Items.AddRange(found.Cast<object>().ToArray());
		programs.SelectedItem = found.First((p) => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
		rendering = false;
	}

	void AskForCode()
	{
		using var dialog = new PairForm();
		if(dialog.ShowDialog(this) == DialogResult.OK) app.Pair(dialog.Code);
	}

	// Whether the chosen program is making sound, kept up to date once a second while the window's open
	public void ShowSound(string text)
	{
		if(sound.Text != text) sound.Text = text;
		// The list still says the chosen program isn't running, and now it is: bring the list up to date
		if(programs.SelectedItem is AudioProgram { ProcessId: 0 } chosen && chosen.Title.EndsWith(NotRunning) && !programs.DroppedDown && AudioPrograms.ProcessFor(chosen.Name) != null) FillPrograms();
	}

	// Brings the window in line with what the app is doing
	public void Render()
	{
		rendering = true;
		var state = app.State;
		var isLive = state == ClientState.Live;
		status.Text = isLive ? "🔴 LIVE in voice" : char.ToUpper(TrayApp.Describe(state)[0]) + TrayApp.Describe(state)[1..];
		status.ForeColor = isLive ? Theme.Live : Theme.Text;
		word.Text = app.LastWord != "" ? app.LastWord : state switch
		{
			ClientState.NeedsPairing => "In Discord, run /me, open Audio app… and get a pairing code. Then press Pair this computer.",
			ClientState.Connected => "Join a voice channel in Discord, then press Go live.",
			ClientState.Live => "Everyone in your voice channel hears " + app.Settings.Program + ". /radio in that channel's chat controls it.",
			ClientState.Reconnecting => "Lost the connection to raccoon man. Trying again by itself.",
			ClientState.NeedsUpdate => "This version is too old to talk to raccoon man. " + (app.Newer != null ? "Press Update to get version " + app.Newer.Version + "." : "Get the latest one from where you downloaded it."),
			_ => ""
		};
		live.Text = isLive || app.WantsLive ? "Stop" : "Go live";
		live.Stopping = isLive || app.WantsLive;
		live.Invalidate();
		live.Enabled = state is ClientState.Connected or ClientState.Live or ClientState.Reconnecting && !app.Updating;
		// A newer version: a line saying so and a button, and nothing happens until it's pressed
		updateRow.Visible = app.Newer != null;
		if(app.Newer != null) updateLine.Text = "Version " + app.Newer.Version + " is out. You have " + TrayApp.Version + ".";
		update.Text = app.Updating ? "Updating…" : "Update";
		update.Enabled = !app.Updating;
		version.Text = "Version " + TrayApp.Version;
		checkForUpdate.Enabled = !app.Updating;
		live.Visible = app.HasKey;
		pair.Visible = !app.HasKey;
		unpair.Enabled = app.HasKey;
		quiet.Checked = app.Settings.QuietWhileLive;
		ShowSound(app.SoundStatus());
		skipAuto.Checked = app.Settings.SkipAutomatically;
		skipTell.Checked = !app.Settings.SkipAutomatically;
		if(!jam.Focused) jam.Text = app.Settings.JamLink;
		if(!server.Focused) server.Text = app.Settings.Server;
		startup.Checked = Settings.StartsWithWindows;
		rendering = false;
	}
}

// The small window that asks for the pairing code
sealed class PairForm : Form
{
	readonly TextBox code = new() { Width = 160, MaxLength = 12, Font = new Font(FontFamily.GenericMonospace, 16), CharacterCasing = CharacterCasing.Upper, TextAlign = HorizontalAlignment.Center };
	public string Code => code.Text;

	public PairForm()
	{
		Text = "Pair this computer";
		Icon = Theme.AppIcon;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		AutoSize = true;
		AutoSizeMode = AutoSizeMode.GrowAndShrink;
		Padding = new Padding(14);
		StartPosition = FormStartPosition.CenterParent;
		var ok = new Button { Text = "Pair", AutoSize = true, Enabled = false };
		var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
		var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
		page.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(360, 0), Text = "In Discord, run /me, open Audio app… and press Get a pairing code. Type the six characters here. The code works once and runs out after ten minutes." });
		page.Controls.Add(code);
		var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
		buttons.Controls.Add(ok);
		buttons.Controls.Add(cancel);
		page.Controls.Add(buttons);
		Controls.Add(page);
		AcceptButton = ok;
		CancelButton = cancel;
		Theme.Apply(this);
		HandleCreated += (_, _) => Theme.DarkTitleBar(this);
		code.TextChanged += (_, _) => ok.Enabled = Protocol.LooksLikeCode(code.Text);
		ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
	}
}
