using RaccoonAudio.Audio;
using RaccoonAudio.Core;

namespace RaccoonAudio;

// The app's one window: what it's doing, which program it streams, Go live, and the few settings. It's built in code
// rather than a designer file, and it only ever shows what TrayApp knows and asks TrayApp to do things.
sealed class MainForm : Form
{
	readonly TrayApp app;
	readonly Label status = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14, FontStyle.Bold) };
	readonly Label word = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
	readonly ComboBox programs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
	readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
	readonly Label sound = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
	readonly CheckBox quiet = new() { Text = "Don't play it on this computer while I'm live", AutoSize = true };
	readonly Button live = new() { Text = "Go live", Width = 420, Height = 44 };
	readonly Button pair = new() { Text = "Pair this computer…", AutoSize = true };
	readonly RadioButton skipAuto = new() { Text = "Skip the song", AutoSize = true };
	readonly RadioButton skipTell = new() { Text = "Just tell me", AutoSize = true };
	readonly TextBox jam = new() { Width = 300, PlaceholderText = "https://spotify.link/…" };
	readonly Button shareJam = new() { Text = "Share", AutoSize = true };
	readonly CheckBox advanced = new() { Text = "Advanced", AutoSize = true };
	readonly FlowLayoutPanel advancedPanel = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, Visible = false, WrapContents = false };
	readonly TextBox server = new() { Width = 300 };
	readonly Button applyServer = new() { Text = "Use this address", AutoSize = true };
	readonly CheckBox startup = new() { Text = "Start with Windows (in the tray, not live)", AutoSize = true };
	readonly Button unpair = new() { Text = "Unpair this computer", AutoSize = true };
	bool rendering;

	public MainForm(TrayApp app)
	{
		this.app = app;
		Text = "Raccoon Audio";
		FormBorderStyle = FormBorderStyle.FixedSingle;
		MaximizeBox = false;
		AutoSize = true;
		AutoSizeMode = AutoSizeMode.GrowAndShrink;
		Padding = new Padding(14);
		StartPosition = FormStartPosition.CenterScreen;

		var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
		page.Controls.Add(status);
		page.Controls.Add(word);
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
		advancedPanel.Controls.Add(Row(server, applyServer));
		advancedPanel.Controls.Add(startup);
		advancedPanel.Controls.Add(unpair);
		page.Controls.Add(advancedPanel);
		Controls.Add(page);

		refresh.Click += (_, _) => FillPrograms();
		quiet.CheckedChanged += (_, _) => { if(!rendering) app.SetQuietWhileLive(quiet.Checked); };
		programs.SelectedIndexChanged += (_, _) => { if(!rendering && programs.SelectedItem is AudioProgram chosen) app.ChooseProgram(chosen.Name); };
		live.Click += (_, _) => app.ToggleLive();
		pair.Click += (_, _) => AskForCode();
		skipAuto.CheckedChanged += (_, _) => { if(!rendering && skipAuto.Checked) app.SetSkipAutomatically(true); };
		skipTell.CheckedChanged += (_, _) => { if(!rendering && skipTell.Checked) app.SetSkipAutomatically(false); };
		shareJam.Click += (_, _) =>
		{
			if(!app.ShareJam(jam.Text)) MessageBox.Show(this, "That doesn't look like a Spotify Jam link. It's the one Spotify gives you when you start a Jam, starting with https://spotify.link/ or https://open.spotify.com/.", Text);
		};
		advanced.CheckedChanged += (_, _) => advancedPanel.Visible = advanced.Checked;
		applyServer.Click += async (_, _) =>
		{
			if(!await app.SetServerAsync(server.Text)) MessageBox.Show(this, "That address won't work. It has to start with wss:// (or ws://127.0.0.1 for a copy running on this computer).", Text);
		};
		startup.CheckedChanged += (_, _) => { if(!rendering) Settings.StartsWithWindows = startup.Checked; };
		unpair.Click += async (_, _) =>
		{
			if(MessageBox.Show(this, "Forget this computer's key? You'll need a new pairing code to use it again.", Text, MessageBoxButtons.OKCancel) == DialogResult.OK) await app.UnpairAsync();
		};
		// Closing the window leaves it running by the clock. Exit is in the icon's menu.
		FormClosing += (_, closing) =>
		{
			if(closing.CloseReason != CloseReason.UserClosing) return;
			closing.Cancel = true;
			Hide();
		};
		FillPrograms();
		Render();
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
	void FillPrograms()
	{
		rendering = true;
		var found = AudioPrograms.Find();
		var wanted = app.Settings.Program;
		if(!found.Any((p) => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))) found.Insert(0, new AudioProgram(wanted, wanted + "  (not running)", 0, false));
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
	}

	// Brings the window in line with what the app is doing
	public void Render()
	{
		rendering = true;
		var state = app.State;
		var isLive = state == ClientState.Live;
		status.Text = isLive ? "🔴 LIVE in voice" : char.ToUpper(TrayApp.Describe(state)[0]) + TrayApp.Describe(state)[1..];
		status.ForeColor = isLive ? Color.FromArgb(200, 40, 40) : SystemColors.ControlText;
		word.Text = app.LastWord != "" ? app.LastWord : state switch
		{
			ClientState.NeedsPairing => "In Discord, run /me, open Audio app… and get a pairing code. Then press Pair this computer.",
			ClientState.Connected => "Join a voice channel in Discord, then press Go live.",
			ClientState.Live => "Everyone in your voice channel hears " + app.Settings.Program + ". /radio in that channel's chat controls it.",
			ClientState.Reconnecting => "Lost the connection to raccoon man. Trying again by itself.",
			ClientState.NeedsUpdate => "This version is too old to talk to raccoon man. Get the latest one from where you downloaded it.",
			_ => ""
		};
		live.Text = isLive || app.WantsLive ? "Stop" : "Go live";
		live.Enabled = state is ClientState.Connected or ClientState.Live or ClientState.Reconnecting;
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
		code.TextChanged += (_, _) => ok.Enabled = Protocol.LooksLikeCode(code.Text);
		ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
	}
}
