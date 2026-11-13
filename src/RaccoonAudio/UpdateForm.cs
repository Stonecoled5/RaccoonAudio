using RaccoonAudio.Core;

namespace RaccoonAudio;

// The window that says a newer version is out, in front of everything else so it isn't missed (a pop-up by the clock
// was easy to miss). Update now starts the update; Later closes it, and the update still waits in the main window and
// the icon's menu. It looks like every other pop-up (RaccoonDialog), and opens in the middle of the screen, since the
// app's window may not be open.
sealed class UpdateForm : RaccoonDialog
{
	// Update now was pressed
	public event Action? UpdateNow;

	public UpdateForm(AppVersion newer, AppVersion running, string why)
		: base("Raccoon Audio update", "Raccoon Audio version " + newer + " is out",
			"You have " + running + ". Updating takes about a minute: the app downloads the new version, closes, and opens it.", why)
	{
		ShowInTaskbar = true;
		TopMost = true;
		StartPosition = FormStartPosition.CenterScreen;
		var update = Primary("Update now", DialogResult.None);
		var later = Secondary("Later", DialogResult.None);
		Finish();
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
