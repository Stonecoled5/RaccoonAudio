namespace RaccoonAudio.Core;

// Why an update can't start, worked out before anything is downloaded, so the person is told plainly instead of
// the update failing part way or doing nothing at all.
public enum UpdateBlock
{
	None,
	// A development build, with its parts beside it
	Development,
	// Started from inside the zip without unzipping it: Windows (or a zip program) unpacked it to a temporary folder
	InsideZip,
	// Unzipped, but into a temporary folder that gets emptied
	TempFolder,
	// The folder it's in can't be written to, like Program Files
	ReadOnlyFolder,
	// Live: updating closes the app, which would end the stream
	Live
}

// Where the running app is, and what that means for updating it
public sealed record UpdatePlace(string Exe, string Folder, bool Writable, bool InsideZip, bool InTemp)
{
	public override string ToString() => "running from " + Folder + " (" + (Writable ? "writable" : "NOT writable") + (InsideZip ? ", inside a zip" : "") + (InTemp ? ", a temporary folder" : "") + ")";
}

public static class UpdateChecks
{
	// The folders Windows and zip programs unpack a program to when it's started from inside a zip without
	// unzipping it: Explorer's Temp1_<name>.zip, 7-Zip's 7zO…, WinRAR's Rar$EX…, or anything named like a zip
	public static bool LooksInsideZip(string folder)
	{
		foreach(var part in folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
		{
			if(part.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return true;
			if(part.StartsWith("Temp1_", StringComparison.OrdinalIgnoreCase) || part.StartsWith("7zO", StringComparison.Ordinal) || part.StartsWith("Rar$", StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	// Whether folder is parent or somewhere inside it
	public static bool IsUnder(string folder, string parent)
	{
		if(string.IsNullOrWhiteSpace(parent)) return false;
		var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
		var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
		return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	// Whether a file can be made in the folder: what an update needs, since it downloads next to the app and swaps
	// the app's file. Tried for real, with a file that's gone the moment it's closed.
	public static bool CanWrite(string folder)
	{
		try
		{
			using var probe = new FileStream(Path.Combine(folder, "RaccoonAudio.write-check-" + Guid.NewGuid().ToString("N")[..8] + ".tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
			probe.WriteByte(0);
			return true;
		}
		catch(Exception err) when(err is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return false;
		}
	}

	// Looks at where the app at exe is. tempFolders: the temporary folders to count as temporary (more than one,
	// because Windows can give the same folder by its short and its long name).
	public static UpdatePlace Look(string exe, IEnumerable<string> tempFolders)
	{
		var folder = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "";
		return new UpdatePlace(exe, folder, CanWrite(folder), LooksInsideZip(folder), tempFolders.Any((t) => IsUnder(folder, t)));
	}

	// What stands in the way, the most lasting thing first: there's no point stopping a stream only to be told the app
	// has to be unzipped.
	public static UpdateBlock WhatBlocks(UpdatePlace? place, bool published, bool live)
	{
		if(!published || place == null) return UpdateBlock.Development;
		if(place.InsideZip) return UpdateBlock.InsideZip;
		if(place.InTemp) return UpdateBlock.TempFolder;
		if(!place.Writable) return UpdateBlock.ReadOnlyFolder;
		if(live) return UpdateBlock.Live;
		return UpdateBlock.None;
	}

	// A heading and whole sentences for the person, for whatever stands in the way
	public static (string Heading, string Message) Explain(UpdateBlock block, UpdatePlace? place) => block switch
	{
		UpdateBlock.Development => ("This copy doesn't update itself", "This copy was built for development, so it doesn't update itself."),
		UpdateBlock.InsideZip => ("Unzip the app first", "Raccoon Audio is running from inside the zip it came in, so Windows put it in a temporary folder (" + place?.Folder +
			") and an update there would be lost. Close the app, right-click the zip, choose Extract All, and run RaccoonAudio.exe from the folder you extracted it to. Then update."),
		UpdateBlock.TempFolder => ("Move the app out of the temporary folder", "Raccoon Audio is running from a temporary folder (" + place?.Folder +
			"), which Windows empties now and then, so an update there would be lost. Close the app, move RaccoonAudio.exe to a folder of its own, like Documents\\Raccoon Audio, and run it from there. Then update."),
		UpdateBlock.ReadOnlyFolder => ("The app's folder can't be changed", "Raccoon Audio can't save files in the folder it's in (" + place?.Folder +
			"), so it can't replace itself there. Folders like Program Files need an administrator. Close the app, move RaccoonAudio.exe to a folder of its own, like Documents\\Raccoon Audio, and run it from there. Then update."),
		UpdateBlock.Live => ("Stop the stream to update", "You're live. Updating closes the app and opens the new version, which would end the stream for everyone in the channel. Stop the stream, then update: it takes about a minute."),
		_ => ("", "")
	};
}

// A short record of every update the app tries: each step, and what went wrong, so a person can send it to whoever is
// helping them. It's kept small: once it passes its limit, the oldest half goes. It never holds the computer's key.
public sealed class UpdateLog(string path, long limit = 64 * 1024)
{
	public string Path { get; } = path;
	public long Limit { get; } = limit;

	public void Write(string line)
	{
		try
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
			Trim();
			File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line.Replace("\r", "").Replace("\n", Environment.NewLine + "    ") + Environment.NewLine);
		}
		catch(Exception)
		{
			// The log helps; it's never a reason for an update to fail
		}
	}

	// Once it's past its limit, only the newest half is kept, starting at a whole line
	void Trim()
	{
		var file = new FileInfo(Path);
		if(!file.Exists || file.Length <= Limit) return;
		var bytes = File.ReadAllBytes(Path);
		var from = bytes.Length - (int)(Limit / 2);
		while(from < bytes.Length && bytes[from - 1] != '\n') from++;
		File.WriteAllBytes(Path, bytes[from..]);
	}
}
