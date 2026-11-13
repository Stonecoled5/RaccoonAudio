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
	// Buttons, text fields and drop-down lists are all this tall, so a row of them lines up
	public const int ControlHeight = 36;

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
				// Rounded like the buttons: the box loses its own border and sits in a rounded field
				if(box.Parent is not RoundField && box.Parent != null) RoundField.Around(box);
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
		// A copy: wrapping a text box in its field changes the list
		foreach(var child in control.Controls.Cast<Control>().ToArray()) Apply(child);
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
		button.MinimumSize = new Size(button.MinimumSize.Width, ControlHeight);
		if(button.AutoSize) button.Padding = new Padding(8, 0, 8, 0);
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
		choice.Margin = new Padding(choice.Margin.Left, 4, choice.Margin.Right, 4);
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

	// Leaves a pop-up window's shape and border to the pop-up itself (Windows 11 would round and outline it its own way)
	public static void RoundPopup(IntPtr window)
	{
		try
		{
			// Windows' own rounding is a fixed 8px, so it's turned off, along with its border; the list rounds itself
			var square = 1;
			DwmSetWindowAttribute(window, 33, ref square, sizeof(int));
			var none = unchecked((int)0xFFFFFFFE);
			DwmSetWindowAttribute(window, 34, ref none, sizeof(int));
		}
		catch(Exception)
		{
			// Only looks
		}
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

// The strip between the two sides of the main window: a chevron, with a thin line running up and down from it. A click
// folds the requests away or opens them again. The chevron points the way they go (open) or come back from (folded);
// folded, the count of waiting requests sits under it.
sealed class FoldStrip : Control
{
	bool open = true;
	int waiting;
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

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(Parent?.BackColor ?? Theme.Background);
		float x = Width / 2f, y = Height / 2f, half = 6, gap = 16;
		using var line = new Pen(Theme.Border, 1);
		g.DrawLine(line, x, 0, x, y - gap);
		var below = !open && waiting > 0 ? y + gap + Width + 4 : y + gap;
		g.DrawLine(line, x, below, x, Height);
		using var pen = new Pen(Theme.Muted, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
		var tip = open ? x + half / 2 : x - half / 2;
		var back = open ? x - half / 2 : x + half / 2;
		g.DrawLines(pen, [new PointF(back, y - half), new PointF(tip, y), new PointF(back, y + half)]);
		if(!open && waiting > 0)
		{
			var badge = new RectangleF(2, y + gap, Width - 4, Width - 4);
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

// A text box with rounded corners, like the buttons. Windows can't round a text box, so the box loses its border and
// sits inside this field, which draws the rounded edge: the theme's border, and the ear cups' purple while typing.
sealed class RoundField : Panel
{
	readonly TextBox box;
	bool hovering;

	RoundField(TextBox box)
	{
		this.box = box;
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
		Cursor = Cursors.IBeam;
		Margin = box.Margin;
		box.Margin = new Padding(0);
		box.BorderStyle = BorderStyle.None;
		box.BackColor = Theme.Surface;
		box.ForeColor = Theme.Text;
		Size = new Size(box.Width, Math.Max(Theme.ControlHeight, box.PreferredHeight + 14));
		Controls.Add(box);
		box.SizeChanged += (_, _) => PerformLayout();
		box.GotFocus += (_, _) => Invalidate();
		box.LostFocus += (_, _) => Invalidate();
		box.MouseEnter += (_, _) => { hovering = true; Invalidate(); };
		box.MouseLeave += (_, _) => { hovering = false; Invalidate(); };
		box.EnabledChanged += (_, _) => Invalidate();
	}

	// Puts the box in a field where it was, in the same place among its neighbours
	public static void Around(TextBox box)
	{
		var parent = box.Parent!;
		var index = parent.Controls.GetChildIndex(box);
		var field = new RoundField(box);
		parent.Controls.Add(field);
		parent.Controls.SetChildIndex(field, index);
	}

	// The box sits in the middle, with room either side for the rounded corners
	protected override void OnLayout(LayoutEventArgs e)
	{
		base.OnLayout(e);
		box.Width = Width - 20;
		box.Location = new Point(10, (Height - box.Height) / 2);
	}

	protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
	protected override void OnClick(EventArgs e) { box.Focus(); base.OnClick(e); }

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(Parent?.BackColor ?? Theme.Background);
		var area = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
		using var shape = Theme.Rounded(area, Theme.ButtonRadius);
		using var fill = new SolidBrush(Theme.Surface);
		using var edge = new Pen(box.Focused ? Theme.Purple : hovering ? Theme.Navy : Theme.Border, box.Focused ? 1.5f : 1);
		g.FillPath(fill, shape);
		g.DrawPath(edge, shape);
	}
}

// A drop-down list drawn to match: a rounded field with the choice and a chevron, opening a dark list of the
// choices with rounded corners. It stands in for a ComboBox, with the parts of one the window uses.
sealed class DropList : Control
{
	public List<object> Items { get; } = [];
	object? selected;
	bool hovering;
	ToolStripDropDown? menu;
	// The list was just closed by pressing on this field: the click that follows mustn't open it again
	bool closedByThisClick;

	// About to open: the list can be brought up to date first
	public event EventHandler? DropDown;
	public event EventHandler? SelectedIndexChanged;

	public DropList()
	{
		SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
		Cursor = Cursors.Hand;
		TabStop = true;
		Height = Theme.ControlHeight;
	}

	public object? SelectedItem
	{
		get => selected;
		set
		{
			if(Equals(selected, value)) return;
			selected = value;
			Invalidate();
			SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	public bool DroppedDown => menu is { Visible: true };

	public string GetItemText(object item) => item.ToString() ?? "";

	protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
	protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
	protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
	protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
	protected override void OnClick(EventArgs e)
	{
		Focus();
		// Pressing on the field while the list is open closes it (Windows closes a menu on any press outside it), and
		// the click it ends in leaves it closed: the arrow opens and closes the list
		if(closedByThisClick) closedByThisClick = false;
		else Open();
		base.OnClick(e);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if(e.KeyCode is Keys.Space or Keys.Enter || (e.Alt && e.KeyCode == Keys.Down)) Open();
		else if(e.KeyCode is Keys.Down or Keys.Up && Items.Count > 0)
		{
			var at = Items.IndexOf(selected!) + (e.KeyCode == Keys.Down ? 1 : -1);
			SelectedItem = Items[Math.Clamp(at, 0, Items.Count - 1)];
		}
		base.OnKeyDown(e);
	}

	protected override bool IsInputKey(Keys key) => key is Keys.Up or Keys.Down || base.IsInputKey(key);

	void Open()
	{
		if(DroppedDown) return;
		DropDown?.Invoke(this, EventArgs.Empty);
		menu?.Dispose();
		menu = new ToolStripDropDown { Renderer = new DarkMenu(), Padding = new Padding(5), BackColor = Theme.Surface, AutoClose = true };
		// Cut to the same rounded corners as the fields; the renderer draws the outline just inside
		var list = menu;
		list.SizeChanged += (_, _) =>
		{
			using var outline = Theme.Rounded(new RectangleF(0, 0, list.Width, list.Height), Theme.ButtonRadius);
			list.Region = new Region(outline);
		};
		foreach(var item in Items)
		{
			var choice = item;
			var picked = Equals(item, selected);
			// The one picked now is in purple
			var row = new ToolStripMenuItem(GetItemText(item)) { AutoSize = false, Width = Width - 10, Height = 32, TextAlign = ContentAlignment.MiddleLeft,
				ForeColor = picked ? Theme.Purple : Theme.Text, Font = picked ? new Font(Font, FontStyle.Bold) : Font };
			row.Click += (_, _) => SelectedItem = choice;
			menu.Items.Add(row);
		}
		menu.HandleCreated += (_, _) => Theme.RoundPopup(menu.Handle);
		menu.Closing += (_, closing) =>
		{
			if(closing.CloseReason == ToolStripDropDownCloseReason.AppClicked && ClientRectangle.Contains(PointToClient(Cursor.Position))) closedByThisClick = true;
		};
		menu.Closed += (_, _) => Invalidate();
		menu.Show(this, new Point(0, Height + 2));
		Invalidate();
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.Clear(Parent?.BackColor ?? Theme.Background);
		var area = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
		using var shape = Theme.Rounded(area, Theme.ButtonRadius);
		using var fill = new SolidBrush(Theme.Surface);
		var lit = Focused || DroppedDown;
		using var edge = new Pen(lit ? Theme.Purple : hovering ? Theme.Navy : Theme.Border, lit ? 1.5f : 1);
		g.FillPath(fill, shape);
		g.DrawPath(edge, shape);
		var text = new Rectangle(10, 0, Width - 40, Height);
		TextRenderer.DrawText(g, selected == null ? "" : GetItemText(selected), Font, text, Enabled ? Theme.Text : Theme.DisabledText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
		float x = Width - 18, y = Height / 2f, half = 4.5f;
		using var pen = new Pen(hovering || lit ? Theme.Text : Theme.Muted, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
		// Pointing up while the list is open, down while it's closed
		var tip = DroppedDown ? -half / 2 : half / 2;
		g.DrawLines(pen, [new PointF(x - half, y - tip), new PointF(x, y + tip), new PointF(x + half, y - tip)]);
	}
}

// The open list of a DropList: dark, the picked one marked in purple, the one under the mouse in navy
sealed class DarkMenu : ToolStripRenderer
{
	protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
	{
		using var fill = new SolidBrush(Theme.Surface);
		e.Graphics.FillRectangle(fill, e.AffectedBounds);
	}

	protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
	{
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		using var shape = Theme.Rounded(new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f), Theme.ButtonRadius);
		using var edge = new Pen(Theme.Border);
		g.DrawPath(edge, shape);
	}

	protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
	{
		if(!e.Item.Selected) return;
		var g = e.Graphics;
		g.SmoothingMode = SmoothingMode.AntiAlias;
		using var shape = Theme.Rounded(new RectangleF(1, 1, e.Item.Width - 3, e.Item.Height - 3), 8);
		using var fill = new SolidBrush(Theme.Navy);
		g.FillPath(fill, shape);
	}

	protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
	{
		e.TextColor = e.Item.ForeColor;
		e.TextRectangle = new Rectangle(10, 0, e.Item.Width - 14, e.Item.Height);
		e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
		base.OnRenderItemText(e);
	}
}
