namespace RaccoonAudio;

// Every pop-up the app shows is one of these, so they all look alike: the raccoon on the left, a bold heading, what
// to say, anything else the pop-up needs (the pairing code's field), and its buttons, with room all round. Its size
// is worked out before it's shown, so it opens centred: over the app's window when it has one, otherwise on the
// screen. Windows' own message boxes can't take the app's look, so the app doesn't use them.
class RaccoonDialog : Form
{
	// Where a pop-up puts anything besides its message, between the message and the buttons
	protected readonly FlowLayoutPanel Extra = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
	readonly FlowLayoutPanel page = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
	readonly FlowLayoutPanel side = new() { AutoSize = true, WrapContents = false, Location = new Point(24, 22) };
	readonly FlowLayoutPanel buttons = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
	const int TextWidth = 340;
	readonly Label heading;
	readonly List<Label> lines = [];

	public RaccoonDialog(string title, string heading, params string[] lines)
	{
		Text = title;
		Icon = Theme.AppIcon;
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = false;
		StartPosition = FormStartPosition.CenterParent;
		var face = new PictureBox { Image = Theme.Picture, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(72, 72), Margin = new Padding(0, 0, 16, 0) };
		this.heading = new Label { Text = heading, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(TextWidth, 0),
			Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold), Margin = new Padding(3, 0, 3, 8) };
		page.Controls.Add(this.heading);
		page.Controls.Add(Extra);
		SetLines(lines);
		page.Controls.Add(buttons);
		side.Controls.Add(face);
		side.Controls.Add(page);
		Controls.Add(side);
		HandleCreated += (_, _) => Theme.DarkTitleBar(this);
	}

	// Puts the message's lines (between the heading and Extra) in place of whatever was there
	void SetLines(string[] text)
	{
		foreach(var old in lines)
		{
			page.Controls.Remove(old);
			old.Dispose();
		}
		lines.Clear();
		foreach(var line in text.Where((l) => !string.IsNullOrWhiteSpace(l)))
		{
			var label = new Label { Text = line, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(3, 0, 3, 8), ForeColor = heading.ForeColor };
			page.Controls.Add(label);
			page.Controls.SetChildIndex(label, page.Controls.IndexOf(Extra));
			lines.Add(label);
		}
	}

	// A pop-up that's already open says something else: a new heading and message, and its size fitted to them,
	// staying centred where it was
	protected void Say(string heading, params string[] text)
	{
		SuspendLayout();
		this.heading.Text = heading;
		SetLines(text);
		ResumeLayout();
		Fit();
	}

	// The size worked out again for what's in it now, keeping the same middle once it's on screen
	protected void Fit()
	{
		var middle = new Point(Left + Width / 2, Top + Height / 2);
		var size = side.GetPreferredSize(Size.Empty);
		ClientSize = new Size(side.Left + size.Width + 24, side.Top + size.Height + 22);
		if(Visible)
		{
			var screen = Screen.FromPoint(middle).WorkingArea;
			Location = new Point(Math.Clamp(middle.X - Width / 2, screen.Left, Math.Max(screen.Left, screen.Right - Width)), Math.Clamp(middle.Y - Height / 2, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));
		}
	}

	// The main button: the gradient one, pressed by Enter
	protected AccentButton Primary(string text, DialogResult result = DialogResult.OK)
	{
		var button = new AccentButton { Text = text, DialogResult = result, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11, FontStyle.Bold), Margin = new Padding(3) };
		button.Size = new Size(Math.Max(120, TextRenderer.MeasureText(text, button.Font).Width + 40), 40);
		buttons.Controls.Add(button);
		AcceptButton = button;
		return button;
	}

	// A plain button next to it, pressed by Esc
	protected Button Secondary(string text, DialogResult result = DialogResult.Cancel)
	{
		var button = new Button { Text = text, DialogResult = result, Margin = new Padding(9, 3, 3, 3) };
		button.Size = new Size(Math.Max(96, TextRenderer.MeasureText(text, button.Font).Width + 36), 40);
		buttons.Controls.Add(button);
		CancelButton = button;
		return button;
	}

	// Called once everything is added: the theme, then the size, which has to come after the theme has put any text
	// field in its rounded frame
	protected void Finish()
	{
		Theme.Apply(this);
		Fit();
	}

	// A pop-up with one button: something to tell the person
	public static void Tell(IWin32Window? owner, string title, string heading, string message, string button = "OK")
	{
		using var dialog = new Message(title, heading, message, button, null);
		Show(dialog, owner);
	}

	// A pop-up with two buttons: true if they pick the first
	public static bool Ask(IWin32Window? owner, string title, string heading, string message, string yes, string no = "Cancel")
	{
		using var dialog = new Message(title, heading, message, yes, no);
		return Show(dialog, owner) == DialogResult.OK;
	}

	static DialogResult Show(Form dialog, IWin32Window? owner)
	{
		if(owner == null) dialog.StartPosition = FormStartPosition.CenterScreen;
		return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
	}

	sealed class Message : RaccoonDialog
	{
		public Message(string title, string heading, string message, string yes, string? no) : base(title, heading, message)
		{
			Primary(yes);
			if(no != null) Secondary(no);
			else CancelButton = AcceptButton;
			Finish();
		}
	}
}
