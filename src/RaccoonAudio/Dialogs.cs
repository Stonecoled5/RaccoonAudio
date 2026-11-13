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
		page.Controls.Add(new Label { Text = heading, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(TextWidth, 0),
			Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold), Margin = new Padding(3, 0, 3, 8) });
		foreach(var line in lines.Where((l) => !string.IsNullOrWhiteSpace(l)))
		{
			page.Controls.Add(new Label { Text = line, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(TextWidth, 0), Margin = new Padding(3, 0, 3, 8) });
		}
		page.Controls.Add(Extra);
		page.Controls.Add(buttons);
		side.Controls.Add(face);
		side.Controls.Add(page);
		Controls.Add(side);
		HandleCreated += (_, _) => Theme.DarkTitleBar(this);
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
		var size = side.GetPreferredSize(Size.Empty);
		ClientSize = new Size(side.Left + size.Width + 24, side.Top + size.Height + 22);
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
