using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RaccoonAudio;

// What the app remembers between runs, in %AppData%\RaccoonAudio: a small settings file anyone could read, and,
// separately, this computer's key.
//
// The key is what lets this computer play audio as its owner, so it's never written as it is. Windows encrypts it
// for this Windows account (DPAPI), which means the file is useless copied to another computer or read by another
// account. It's never shown, logged or put in the settings file.
sealed class Settings
{
	public const string DefaultServer = "wss://raccoon-man-a29f1b3c7e03.herokuapp.com/stream";

	// Where raccoon man's stream server is. Only changed for testing against a copy on this computer.
	public string Server { get; set; } = DefaultServer;
	// The program to stream, by process name
	public string Program { get; set; } = "Spotify";
	// When the channel votes to skip: skip the song, or only say so
	public bool SkipAutomatically { get; set; } = true;
	public string JamLink { get; set; } = "";

	static readonly string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaccoonAudio");
	static readonly string settingsFile = Path.Combine(folder, "settings.json");
	static readonly string keyFile = Path.Combine(folder, "key.bin");
	// Mixed in when encrypting, so another program's DPAPI data can't be passed off as this one's
	static readonly byte[] purpose = Encoding.UTF8.GetBytes("RaccoonAudio device key v1");

	public static Settings Load()
	{
		try
		{
			if(File.Exists(settingsFile)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile)) ?? new Settings();
		}
		catch(Exception)
		{
			// A damaged file: start from the defaults rather than not starting
		}
		return new Settings();
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(folder);
			File.WriteAllText(settingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch(Exception)
		{
			// Not being able to save a preference isn't worth stopping for
		}
	}

	// The server address if it's one the app can use, or null. Encrypted only, except for a copy of the server
	// running on this same computer, which is how it's tested.
	public Uri? ServerUri()
	{
		if(!Uri.TryCreate(Server.Trim(), UriKind.Absolute, out var url)) return null;
		if(url.Scheme == "wss") return url;
		return url.Scheme == "ws" && url.IsLoopback ? url : null;
	}

	public static string? LoadKey()
	{
		try
		{
			if(!File.Exists(keyFile)) return null;
			var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyFile), purpose, DataProtectionScope.CurrentUser));
			return key.Length is > 0 and <= 64 ? key : null;
		}
		catch(Exception)
		{
			// Encrypted for another account or computer, or damaged: as good as not paired
			return null;
		}
	}

	public static void SaveKey(string key)
	{
		Directory.CreateDirectory(folder);
		File.WriteAllBytes(keyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), purpose, DataProtectionScope.CurrentUser));
	}

	public static void ForgetKey()
	{
		try { File.Delete(keyFile); } catch(Exception) { }
	}

	// --- Starting with Windows ---

	const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string runName = "RaccoonAudio";

	public static bool StartsWithWindows
	{
		get
		{
			using var key = Registry.CurrentUser.OpenSubKey(runKey);
			return key?.GetValue(runName) is string;
		}
		set
		{
			using var key = Registry.CurrentUser.CreateSubKey(runKey);
			if(value) key.SetValue(runName, "\"" + Environment.ProcessPath + "\" --tray");
			else key.DeleteValue(runName, false);
		}
	}
}
