using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RaccoonAudio.Audio;

// A program that can be streamed: its process name (what's remembered between runs), what to call it, the process
// to capture, and whether it's making sound right now
sealed record AudioProgram(string Name, string Title, int ProcessId, bool Playing)
{
	public override string ToString() => Title + (Playing ? "  (playing now)" : "");
}

// The programs that have sound open on the default speakers: the same list as Windows' volume mixer
static class AudioPrograms
{
	public static List<AudioProgram> Find()
	{
		var found = new Dictionary<string, AudioProgram>(StringComparer.OrdinalIgnoreCase);
		try
		{
			using var enumerator = new MMDeviceEnumerator();
			using var speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
			var sessions = speakers.AudioSessionManager.Sessions;
			for(var i = 0; i < sessions.Count; i++)
			{
				var session = sessions[i];
				var id = (int)session.GetProcessID;
				// Process 0 is Windows' own sounds, and this app isn't a thing to stream
				if(id == 0 || id == Environment.ProcessId || session.IsSystemSoundsSession) continue;
				try
				{
					using var process = Process.GetProcessById(id);
					var playing = session.State == AudioSessionState.AudioSessionStateActive;
					var name = process.ProcessName;
					// A program with several processes shows once, playing if any of them is
					if(found.TryGetValue(name, out var already)) found[name] = already with { Playing = already.Playing || playing };
					else found[name] = new AudioProgram(name, TitleOf(process), RootOf(process), playing);
				}
				catch(Exception)
				{
					// It closed while the list was being made
				}
			}
		}
		catch(Exception)
		{
			// No speakers, or Windows wouldn't say: an empty list, and the window says none were found
		}
		return found.Values.OrderByDescending((p) => p.Playing).ThenBy((p) => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
	}

	// The process to capture for a program by name, or null if it isn't running. Its sound is captured together with
	// every process it started, so this is the one at the top.
	public static int? ProcessFor(string name)
	{
		var running = Process.GetProcessesByName(name);
		try
		{
			return running.Length == 0 ? null : RootOf(running.OrderBy((p) => StartOf(p)).First());
		}
		finally
		{
			foreach(var process in running) process.Dispose();
		}
	}

	static DateTime StartOf(Process process)
	{
		try { return process.StartTime; } catch(Exception) { return DateTime.MaxValue; }
	}

	// What the program calls itself ("Spotify", "Google Chrome"), or its process name if Windows won't say
	static string TitleOf(Process process)
	{
		try
		{
			var described = process.MainModule?.FileVersionInfo.FileDescription;
			if(!string.IsNullOrWhiteSpace(described)) return described.Trim();
		}
		catch(Exception)
		{
			// Not allowed to look at that process
		}
		return process.ProcessName;
	}

	// The top process of the program: its parent, and that one's parent, for as long as they're the same program
	static int RootOf(Process process)
	{
		var id = process.Id;
		var name = process.ProcessName;
		for(var steps = 0; steps < 8; steps++)
		{
			var parentId = ParentOf(id);
			if(parentId == null) break;
			try
			{
				using var parent = Process.GetProcessById(parentId.Value);
				if(!string.Equals(parent.ProcessName, name, StringComparison.OrdinalIgnoreCase)) break;
				id = parent.Id;
			}
			catch(Exception)
			{
				break;
			}
		}
		return id;
	}

	static int? ParentOf(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			var info = new PROCESS_BASIC_INFORMATION();
			return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0 ? (int)info.InheritedFromUniqueProcessId : null;
		}
		catch(Exception)
		{
			return null;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	struct PROCESS_BASIC_INFORMATION
	{
		public IntPtr ExitStatus;
		public IntPtr PebBaseAddress;
		public IntPtr AffinityMask;
		public IntPtr BasePriority;
		public IntPtr UniqueProcessId;
		public IntPtr InheritedFromUniqueProcessId;
	}

	[DllImport("ntdll.dll")]
	static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref PROCESS_BASIC_INFORMATION information, int length, out int returned);
}
