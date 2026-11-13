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
				button.FlatStyle = FlatStyle.Flat;
				button.BackColor = Raised;
				button.ForeColor = Text;
				button.FlatAppearance.BorderColor = Border;
				button.FlatAppearance.MouseOverBackColor = Navy;
				button.FlatAppearance.MouseDownBackColor = Border;
				button.Cursor = Cursors.Hand;
				break;
			case TextBox box:
				box.BackColor = Surface;
				box.ForeColor = Text;
				box.BorderStyle = BorderStyle.FixedSingle;
				break;
			case ComboBox list:
				list.FlatStyle = FlatStyle.Flat;
				list.BackColor = Surface;
				list.ForeColor = Text;
				break;
			case CheckBox or RadioButton:
				((ButtonBase)control).FlatStyle = FlatStyle.Flat;
				control.ForeColor = Text;
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
		if(control is ScrollableControl { AutoScroll: true }) DarkScrollBars(control);
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

	static void DarkScrollBars(Control control)
	{
		void dark()
		{
			try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch(Exception) { }
		}
		if(control.IsHandleCreated) dark();
		else control.HandleCreated += (_, _) => dark();
	}

	public static GraphicsPath Rounded(RectangleF area, float radius)
	{
		var path = new GraphicsPath();
		var d = radius * 2;
		path.AddArc(area.X, area.Y, d, d, 180, 90);
		path.AddArc(area.Right - d, area.Y, d, d, 270, 90);
		path.AddArc(area.Right - d, area.Bottom - d, d, d, 0, 90);
		path.AddArc(area.X, area.Bottom - d, d, d, 90, 90);
		path.CloseFigure();
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
		using var shape = Theme.Rounded(area, Height / 4f);
		var (from, to) = !Enabled ? (Theme.Raised, Theme.Raised) : Stopping ? (Theme.Live, Color.FromArgb(214, 80, 160)) : (Theme.Cyan, Theme.Purple);
		if(hovering && Enabled)
		{
			from = ControlPaint.Light(from, 0.15f);
			to = ControlPaint.Light(to, 0.15f);
		}
		using var fill = new LinearGradientBrush(area, from, to, LinearGradientMode.Horizontal);
		g.FillPath(fill, shape);
		TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? Color.White : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		if(Focused && ShowFocusCues)
		{
			using var ring = new Pen(Color.FromArgb(160, Color.White), 1.5f);
			using var inner = Theme.Rounded(RectangleF.Inflate(area, -3, -3), Height / 4f - 3);
			g.DrawPath(ring, inner);
		}
	}
}
