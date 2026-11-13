using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RaccoonAudio;

// The app's look, taken from its icon: the raccoon in headphones. A dark charcoal window like his mask, the navy of the
// headband for buttons, and the cyan-to-purple of the ear cups for the one button that matters (Go live). Every window
// calls Apply once it's built, and again on anything it adds later, so nothing is left in Windows' default grey.
static class Theme
{
	public static readonly Color Background = Color.FromArgb(27, 28, 38);
	public static readonly Color Surface = Color.FromArgb(38, 40, 56);
	public static readonly Color Raised = Color.FromArgb(52, 55, 80);
	public static readonly Color Border = Color.FromArgb(66, 70, 104);
	// The headband
	public static readonly Color Navy = Color.FromArgb(60, 64, 122);
	public static readonly Color Text = Color.FromArgb(236, 237, 245);
	public static readonly Color Muted = Color.FromArgb(160, 164, 188);
	// The ear cups
	public static readonly Color Cyan = Color.FromArgb(79, 209, 245);
	public static readonly Color Purple = Color.FromArgb(155, 108, 245);
	// The tongue, for being live
	public static readonly Color Live = Color.FromArgb(244, 98, 128);
	// Text on something switched off: dimmer than the notes, but still readable on a dark button
	public static readonly Color DisabledText = Color.FromArgb(100, 104, 134);
	// Every button's corners, the big gradient ones too
	public const float ButtonRadius = 12;

	// The controls already drawn by hand, so applying the theme twice doesn't hook them up twice
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> drawn = new();

	static Icon? appIcon;
	static Image? picture;

	// The raccoon as an icon (the window's and the tray's) and as a picture (the header)
	public static Icon AppIcon => appIcon ??= new Icon(Resource("raccoon.ico"));
	public static Image Picture => picture ??= Image.FromStream(Resource("raccoon.png"));

	static Stream Resource(string name) => typeof(Theme).Assembly.GetManifestResourceStream(name)
		?? throw new InvalidOperationException("The " + name + " picture wasn't built into the app.");

	// Colours a window, or anything added to one, and everything inside it
	public static void Apply(Control control)
	{
		switch(control)
		{
			case AccentButton:
				break;
			case Button button:
				button.ForeColor = Text;
				if(drawn.TryAdd(button, true)) Round(button, ButtonRadius, DisabledText);
				break;
			case TextBox box:
				box.BackColor = Surface;
				box.ForeColor = Text;
				box.BorderStyle = BorderStyle.FixedSingle;
				break;
			case ComboBox list:
				// Windows' own dark look for lists: a closed drop-down list ignores BackColor and stays white otherwise
				list.BackColor = Surface;
				list.ForeColor = Text;
				DarkTheme(list, "DarkMode_CFD");
				break;
			case CheckBox or RadioButton:
				control.ForeColor = Text;
				// Filled with the purple of the ear cups when on
				if(drawn.TryAdd(control, true)) Choice((ButtonBase)control, Purple, DisabledText);
				break;
			case Label label:
				// Notes and small print are grey; everything else is the main text colour, unless it chose its own
				if(label.ForeColor == SystemColors.GrayText) label.ForeColor = Muted;
				else if(label.ForeColor == SystemColors.ControlText) label.ForeColor = Text;
				break;
			default:
				control.BackColor = Background;
				control.ForeColor = Text;
				break;
		}
		if(control is ScrollableControl { AutoScroll: true }) DarkTheme(control, "DarkMode_Explorer");
		foreach(Control child in control.Controls) Apply(child);
	}

	// A dark title bar to match (Windows 11; older Windows keeps its own)
	public static void DarkTitleBar(Form form)
	{
		try
		{
			var on = 1;
			DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int));
		}
		catch(Exception)
		{
			// Only looks
		}
	}

	// Dark scroll bars and lists, the way Windows draws them in dark mode
	static void DarkTheme(Control control, string theme)
	{
		void dark()
		{
			try { SetWindowTheme(control.Handle, theme, null); } catch(Exception) { }
		}
		if(control.IsHandleCreated) dark();
		else control.HandleCreated += (_, _) => dark();
	}

	public static GraphicsPath Rounded(RectangleF area, float radius)
	{
		var path = new GraphicsPath();
		radius = Math.Max(1, Math.Min(radius, Math.Min(area.Width, area.Height) / 2));
		var d = radius * 2;
		path.AddArc(area.X, area.Y, d, d, 180, 90);
		path.AddArc(area.Right - d, area.Y, d, d, 270, 90);
		path.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
		path.AddArc(area.X, area.Bottom - d, d, d, 90, 90);
		path.CloseFigure();
		return path;
	}

	// --- Drawn by hand: rounded buttons and purple check boxes and radio buttons ---

	// Draws a plain button rounded, in the theme's colours, with disabled text that's still readable
	public static void Round(Button button, float radius, Color disabledText)
	{
		button.FlatStyle = FlatStyle.Flat;
		button.FlatAppearance.BorderSize = 0;
		button.Cursor = Cursors.Hand;
		var hovering = false;
		var pressed = false;
		button.MouseEnter += (_, _) => { hovering = true; button.Invalidate(); };
		button.MouseLeave += (_, _) => { hovering = false; pressed = false; button.Invalidate(); };
		button.MouseDown += (_, _) => { pressed = true; button.Invalidate(); };
		button.MouseUp += (_, _) => { pressed = false; button.Invalidate(); };
		button.Paint += (_, e) =>
		{
			var g = e.Graphics;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear(button.Parent?.BackColor ?? Background);
			var area = new RectangleF(0.5f, 0.5f, button.Width - 1.5f, button.Height - 1.5f);
			using var shape = Rounded(area, radius);
			var on = button.Enabled;
			using var fill = new SolidBrush(!on ? Surface : pressed ? Border : hovering ? Navy : Raised);
			using var edge = new Pen(on ? Border : Raised);
			g.FillPath(fill, shape);
			g.DrawPath(edge, shape);
			TextRenderer.DrawText(g, button.Text, button.Font, button.ClientRectangle, on ? Text : disabledText,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
		};
	}

	// Draws a check box or radio button with its box (or circle) filled in this colour when it's on
	public static void Choice(ButtonBase choice, Color fillColour, Color disabledText)
	{
		var radio = choice is RadioButton;
		var hovering = false;
		choice.Cursor = Cursors.Hand;
		choice.Padding = new Padding(8, 0, 0, 0);
		choice.MouseEnter += (_, _) => { hovering = true; choice.Invalidate(); };
		choice.MouseLeave += (_, _) => { hovering = false; choice.Invalidate(); };
		choice.Paint += (_, e) =>
		{
			var g = e.Graphics;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			g.Clear(choice.Parent?.BackColor ?? Background);
			var on = choice is CheckBox box ? box.Checked : ((RadioButton)choice).Checked;
			const float size = 16;
			var top = (choice.Height - size) / 2f;
			var area = new RectangleF(1, top, size, size);
			using var shape = radio ? Circle(area) : Rounded(area, 4);
			using var fill = new SolidBrush(on ? fillColour : Surface);
			using var edge = new Pen(on ? fillColour : hovering ? fillColour : Border, 1.5f);
			g.FillPath(fill, shape);
			g.DrawPath(edge, shape);
			if(on && radio)
			{
				using var dot = new SolidBrush(Color.White);
				g.FillEllipse(dot, area.X + 5, area.Y + 5, size - 10, size - 10);
			}
			else if(on)
			{
				using var tick = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
				g.DrawLines(tick, [new PointF(area.X + 4, area.Y + 8.5f), new PointF(area.X + 7, area.Y + 11.5f), new PointF(area.X + 12, area.Y + 5)]);
			}
			var text = new Rectangle((int)(area.Right + 7), 0, choice.Width - (int)(area.Right + 7), choice.Height);
			TextRenderer.DrawText(g, choice.Text, choice.Font, text, choice.Enabled ? Text : disabledText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
		};
	}

	static GraphicsPath Circle(RectangleF area)
	{
		var path = new GraphicsPath();
		path.AddEllipse(area);
		return path;
	}

	[DllImport("dwmapi.dll")]
	static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

	[DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
	static extern int SetWindowTheme(IntPtr window, string? appName, string? idList);
}

// The big button: rounded, in the ear cups' cyan-to-purple, or the tongue's pink while it means "Stop"
sealed class AccentButton : Button
{
	public bool Stopping { get; set; }
	// The corners' radius in pixels; below zero, a quarter of the height
	public float Radius { get; set; } = Theme.ButtonRadius;
	float Corner => Radius < 0 ? Height / 4f : Radius;

	public AccentButton()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
		FlatStyle = FlatStyle.Flat;
		FlatAppearance.BorderSize = 0;
		Cursor = Cursors.Hand;
		Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold);
	}

	bool hovering;
	protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(Parent?.BackColor ?? Theme.Background);
		var area = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
		using var shape = Theme.Rounded(area, Corner);
		var (from, to) = !Enabled ? (Theme.Raised, Theme.Raised) : Stopping ? (Theme.Live, Color.FromArgb(214, 80, 160)) : (Theme.Cyan, Theme.Purple);
		if(hovering && Enabled)
		{
			from = ControlPaint.Light(from, 0.15f);
			to = ControlPaint.Light(to, 0.15f);
		}
		using var fill = new LinearGradientBrush(area, from, to, LinearGradientMode.Horizontal);
		g.FillPath(fill, shape);
		TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Color.White : Theme.DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		if(Focused && ShowFocusCues)
		{
			using var ring = new Pen(Color.FromArgb(160, Color.White), 1.5f);
			using var inner = Theme.Rounded(RectangleF.Inflate(area, -3, -3), Math.Max(Corner - 3, 1));
			g.DrawPath(ring, inner);
		}
	}
}

// The strip between the two sides of the main window. A click folds the requests away or opens them again. The arrow
// points the way they go (open) or come back from (folded); folded, it also says how many are waiting.
sealed class FoldStrip : Control
{
	bool open = true;
	int waiting;
	bool hovering;
	public bool Shown { get; private set; }

	public FoldStrip()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
		Cursor = Cursors.Hand;
		Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8, FontStyle.Bold);
	}

	public void Show(bool open, int waiting)
	{
		this.open = open;
		this.waiting = waiting;
		Shown = true;
		Invalidate();
	}

	protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		using var fill = new SolidBrush(hovering ? Theme.Raised : Theme.Surface);
		g.FillRectangle(fill, ClientRectangle);
		// A chevron in the middle: > folds the requests away to the right, < brings them back
		float x = Width / 2f, y = Height / 2f, half = 6;
		using var pen = new Pen(hovering ? Theme.Cyan : Theme.Muted, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
		var tip = open ? x + half / 2 : x - half / 2;
		var back = open ? x - half / 2 : x + half / 2;
		g.DrawLines(pen, [new PointF(back, y - half), new PointF(tip, y), new PointF(back, y + half)]);
		if(!open && waiting > 0)
		{
			var badge = new RectangleF(2, y + 14, Width - 4, Width - 4);
			using var bubble = new LinearGradientBrush(badge, Theme.Cyan, Theme.Purple, LinearGradientMode.Vertical);
			g.FillEllipse(bubble, badge);
			TextRenderer.DrawText(g, waiting > 99 ? "99+" : waiting.ToString(), Font, Rectangle.Round(badge), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
	}
}

// A heading that opens and closes a section below it: a chevron and the words, with no box around them. The chevron
// points right when closed and down when open; on hover the words light up.
sealed class Expander : Control
{
	bool open;
	bool hovering;

	public bool Open { get => open; set { open = value; Invalidate(); } }

	public Expander()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
		Cursor = Cursors.Hand;
		Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold);
		Size = new Size(140, 28);
	}

	protected override void OnClick(EventArgs e) { Open = !Open; base.OnClick(e); }
	protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
	protected override void OnTextChanged(EventArgs e) { Width = TextRenderer.MeasureText(Text, Font).Width + 26; base.OnTextChanged(e); }

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(Parent?.BackColor ?? Theme.Background);
		var colour = hovering ? Theme.Cyan : Theme.Text;
		float x = 8, y = Height / 2f, half = 4.5f;
		using var pen = new Pen(colour, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
		if(open) g.DrawLines(pen, [new PointF(x - half, y - half / 2), new PointF(x, y + half / 2), new PointF(x + half, y - half / 2)]);
		else g.DrawLines(pen, [new PointF(x - half / 2, y - half), new PointF(x + half / 2, y), new PointF(x - half / 2, y + half)]);
		TextRenderer.DrawText(g, Text, Font, new Rectangle(20, 0, Width - 20, Height), colour, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
	}
}
