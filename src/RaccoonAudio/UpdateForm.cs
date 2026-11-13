using RaccoonAudio.Core;

namespace RaccoonAudio;

// The window that says a newer version is out, in front of everything else so it isn't missed (a pop-up by the clock
// was easy to miss). Update now starts the update; Later closes it, and the update still waits in the main window and
// the icon's menu.
sealed class UpdateForm : Form
{
	// Update now was pressed
	public event Action? UpdateNow;

	public UpdateForm(AppVersion newer, AppVersion running, string why)
	{
		Text = "Raccoon Audio update";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = false;
		MinimizeBox = false;
		ShowInTaskbar = true;
		TopMost = true;
		StartPosition = FormStartPosition.CenterScreen;
		AutoSize = true;
		AutoSizeMode = AutoSizeMode.GrowAndShrink;
		Padding = new Padding(16);

		var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
		page.Controls.Add(new Label
		{
			Text = "Raccoon Audio version " + newer + " is out",
			AutoSize = true,
			UseMnemonic = false,
			Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12, FontStyle.Bold),
			Margin = new Padding(3, 0, 3, 6)
		});
		page.Controls.Add(new Label
		{
			Text = "You have " + running + ". Updating takes about a minute: the app downloads the new version, closes, and opens it.",
			AutoSize = true,
			UseMnemonic = false,
			MaximumSize = new Size(380, 0)
		});
		if(why != "") page.Controls.Add(new Label { Text = why, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(380, 0), Margin = new Padding(3, 8, 3, 3) });

		var update = new Button { Text = "Update now", AutoSize = true };
		var later = new Button { Text = "Later", AutoSize = true, DialogResult = DialogResult.Cancel };
		var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
		buttons.Controls.Add(update);
		buttons.Controls.Add(later);
		page.Controls.Add(buttons);
		Controls.Add(page);
		AcceptButton = update;
		CancelButton = later;

		update.Click += (_, _) =>
		{
			Close();
			UpdateNow?.Invoke();
		};
		later.Click += (_, _) => Close();
		// In front and focused when it appears, even when the app was in the tray
		Shown += (_, _) => Activate();
	}
}
