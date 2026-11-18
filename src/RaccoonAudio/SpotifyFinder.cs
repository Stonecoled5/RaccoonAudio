using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RaccoonAudio;

// Works out how Spotify is installed on this computer, for the requests' Search button. Nothing here is particular
// to one computer: it looks where each kind of install lives, every time it's asked.
static class SpotifyFinder
{
	// Spotify.exe from the ordinary installer: the one that's running, where it installs itself, or wherever the
	// spotify: link says it is. Null if there isn't one, which includes the Microsoft Store version: its program sits
	// in a protected folder and can't be started by its path.
	public static string? Program()
	{
		foreach(var path in Candidates())
		{
			try
			{
				if(!string.IsNullOrWhiteSpace(path) && !InStoreFolder(path) && File.Exists(path)) return path;
			}
			catch(Exception)
			{
				// A path Windows won't look at is just not one to use
			}
		}
		return null;
	}

	// Whether anything on this computer says it opens spotify: links: the ordinary installer (a command that points
	// at a program that's still there) or the Store version (an app id).
	public static bool LinkOpens()
	{
		var command = Query(AssocCommand);
		if(command != null)
		{
			var program = Query(AssocExecutable);
			if(program != null && File.Exists(program)) return true;
		}
		return Query(AssocAppId) != null;
	}

	static IEnumerable<string?> Candidates()
	{
		foreach(var running in Running()) yield return running;
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
		yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotify", "Spotify.exe");
		yield return Query(AssocExecutable);
	}

	static List<string> Running()
	{
		var paths = new List<string>();
		foreach(var process in Process.GetProcessesByName("Spotify"))
		{
			try
			{
				if(process.MainModule?.FileName is { } path) paths.Add(path);
			}
			catch(Exception)
			{
				// One it isn't allowed to look inside
			}
			finally
			{
				process.Dispose();
			}
		}
		return paths;
	}

	static bool InStoreFolder(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

	// What Windows has on record for the spotify: link, or null if nothing
	static string? Query(int what)
	{
		var text = new StringBuilder(1024);
		uint length = (uint)text.Capacity;
		var result = AssocQueryString(AssocIsProtocol, what, "spotify", null, text, ref length);
		return result == 0 && text.Length > 0 ? text.ToString() : null;
	}

	const int AssocIsProtocol = 0x1000;
	const int AssocCommand = 1;
	const int AssocExecutable = 2;
	const int AssocAppId = 21;

	[DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
	static extern int AssocQueryString(int flags, int what, string association, string? extra, StringBuilder result, ref uint length);
}
