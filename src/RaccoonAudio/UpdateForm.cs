using System.Diagnostics;
using System.Drawing.Drawing2D;
using RaccoonAudio.Core;

namespace RaccoonAudio;

// The update's own window, from the question to the end. It says a newer version is out, in front of everything else
// so it isn't missed (a pop-up by the clock was easy to miss), and once Update now is pressed it stays open: it shows
// each step and how much has downloaded, and it goes only when the new version takes over. If the update can't start
// (live, never unzipped, a folder that can't be changed) it says why, and if it fails it says what went wrong and
// where the update log is. Later closes it, and the update still waits in the main window and the icon's menu.
//
// It looks like every other pop-up (RaccoonDialog), and opens in the middle of the screen, since the app's window may
// not be open.
sealed class UpdateForm : RaccoonDialog
{
	// Update now was pressed
	public event Action? UpdateNow;
	// Stop the stream and update was pressed: it's live, and the person wants the update anyway
	public event Action? StopAndUpdate;

	enum Mode { Asking, Working, Blocked, Live, Failed }
	Mode mode = Mode.Asking;
	readonly AppVersion newer;
	readonly AccentButton go;
	readonly Button other;
	readonly Button log;
	readonly Label step = new() { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(340, 0), Margin = new Padding(3, 0, 3, 8), ForeColor = SystemColors.GrayText, Visible = false };
	readonly ProgressStrip bar = new() { Size = new Size(340, 10), Margin = new Padding(3, 4, 3, 4), Visible = false };
	string logPath = "";
	// The new version has taken over: nothing stands in the way of closing now
	bool done;

	public UpdateForm(AppVersion newer, AppVersion running, string why)
		: base("Raccoon Audio update", "Raccoon Audio version " + newer + " is out",
			"You have " + running + ". Updating takes about a minute: the app downloads the new version, closes, and opens it.", why)
	{
		this.newer = newer;
		ShowInTaskbar = true;
		TopMost = true;
		StartPosition = FormStartPosition.CenterScreen;
		Extra.Controls.Add(step);
		Extra.Controls.Add(bar);
		go = Primary("Update now", DialogResult.None);
		other = Secondary("Later", DialogResult.None);
		log = Secondary("Show the log", DialogResult.None);
		log.Visible = false;
		CancelButton = other;
		Finish();
		go.Click += (_, _) =>
		{
			switch(mode)
			{
				case Mode.Asking: UpdateNow?.Invoke(); break;
				case Mode.Live: StopAndUpdate?.Invoke(); break;
				default: Close(); break;
			}
		};
		other.Click += (_, _) => Close();
		log.Click += (_, _) => ShowLog();
		// While it's updating it can't be closed: closing it is how an update came to look like it did nothing
		FormClosing += (_, e) =>
		{
			if(mode == Mode.Working && !done && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
		};
		// In front and focused when it appears, even when the app was in the tray
		Shown += (_, _) => Activate();
	}

	// Under way: what it's doing now, and how far through the download it is (null when it can't tell)
	public void Working(string text, int? percent)
	{
		if(mode != Mode.Working)
		{
			mode = Mode.Working;
			// It's open for a minute or so: in front when it starts, but not over everything the whole time
			TopMost = false;
			Buttons(null, null, false);
			step.Visible = true;
			bar.Visible = true;
			Say("Updating to version " + newer, "This window closes by itself when the new version opens. Your settings and pairing stay as they are.");
		}
		var height = step.Height;
		step.Text = text;
		bar.Percent = percent;
		if(step.Height != height) Fit();
		if(!Visible) Bring();
	}

	// It can't start, and why. Live gets a button that stops the stream and updates; the rest only need closing.
	public void Blocked(UpdateBlock block, string heading, string message)
	{
		mode = block == UpdateBlock.Live ? Mode.Live : Mode.Blocked;
		step.Visible = false;
		bar.Visible = false;
		if(mode == Mode.Live) Buttons("Stop the stream and update", "Later", false);
		else Buttons("Close", null, false);
		Say(heading, message);
		Bring();
	}

	// It went wrong: everything it knows, where the log is, and a way to close
	public void Failed(string message, string logFile)
	{
		mode = Mode.Failed;
		logPath = logFile;
		TopMost = true;
		step.Visible = false;
		bar.Visible = false;
		Buttons("Close", null, File.Exists(logFile));
		Say("The update didn't work", message, "Every step is written down in " + logFile + ". If you ask for help, send that file: Show the log finds it.");
		Bring();
	}

	// The new version has taken over
	public void Done()
	{
		done = true;
		Close();
	}

	void Bring()
	{
		if(!Visible) Show();
		if(WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
		Activate();
	}

	// Which buttons show, and what they say. The main one is Enter; Esc closes when there's a second.
	void Buttons(string? main, string? second, bool showLog)
	{
		go.Visible = main != null;
		if(main != null)
		{
			go.Text = main;
			go.Width = Math.Max(120, TextRenderer.MeasureText(main, go.Font).Width + 40);
		}
		other.Visible = second != null;
		if(second != null) other.Text = second;
		log.Visible = showLog;
		AcceptButton = main != null ? go : null;
		CancelButton = second != null ? other : main != null ? go : null;
	}

	void ShowLog()
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + logPath }, UseShellExecute = false });
		}
		catch(Exception)
		{
			// Its path is in the message anyway
		}
	}
}

// How far the update has got: a thin bar in the ear cups' colours. With no number it's a short piece sliding along,
// for steps that can't say how far through they are.
sealed class ProgressStrip : Control
{
	int? percent;
	int slide;
	readonly System.Windows.Forms.Timer moving = new() { Interval = 30 };

	public ProgressStrip()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
		moving.Tick += (_, _) =>
		{
			slide = (slide + 4) % (Width + Width / 3);
			Invalidate();
		};
	}

	public int? Percent
	{
		get => percent;
		set
		{
			percent = value is int p ? Math.Clamp(p, 0, 100) : null;
			moving.Enabled = percent == null && Visible;
			Invalidate();
		}
	}

	protected override void OnVisibleChanged(EventArgs e)
	{
		base.OnVisibleChanged(e);
		moving.Enabled = percent == null && Visible;
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.Clear(Parent?.BackColor ?? Theme.Background);
		var track = new Rectangle(0, 0, Width, Height);
		using(var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, track);
		Rectangle fill;
		if(percent is int p) fill = new Rectangle(0, 0, Width * p / 100, Height);
		else
		{
			var piece = Math.Max(10, Width / 3);
			var left = Math.Max(0, slide - piece);
			fill = new Rectangle(left, 0, Math.Min(slide, Width) - left, Height);
		}
		if(fill.Width > 0)
		{
			using var brush = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Height), Theme.Cyan, Theme.Purple, LinearGradientMode.Horizontal);
			g.FillRectangle(brush, fill);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if(disposing) moving.Dispose();
		base.Dispose(disposing);
	}
}
