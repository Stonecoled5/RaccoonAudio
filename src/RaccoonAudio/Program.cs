using RaccoonAudio.Audio;

namespace RaccoonAudio;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		// For checking capture on a real computer without Discord: RaccoonAudio --capture-test <process id> <seconds> <report file>
		if(args.Length == 4 && args[0] == "--capture-test") return CaptureTest(int.Parse(args[1]), int.Parse(args[2]), args[3], null);
		// The same, with the program turned down to almost nothing on this computer meanwhile, the way a stream does:
		// RaccoonAudio --capture-test <process id> <seconds> <report file> --quiet <program name>
		if(args.Length == 6 && args[0] == "--capture-test" && args[4] == "--quiet") return CaptureTest(int.Parse(args[1]), int.Parse(args[2]), args[3], args[5]);
		// Which programs have sound open: RaccoonAudio --list-programs <report file>
		if(args.Length == 2 && args[0] == "--list-programs")
		{
			File.WriteAllLines(args[1], AudioPrograms.Find().Select((p) => p.ProcessId + "\t" + p.Name + "\t" + p.Title + "\t" + (p.Playing ? "playing" : "quiet")));
			return 0;
		}
		// The whole conversation with a stream server, with a test tone instead of captured sound:
		// RaccoonAudio --connect-test <ws address> <pairing code> <seconds> <report file>. It never touches the saved key.
		if(args.Length == 5 && args[0] == "--connect-test") return ConnectTest(args[1], args[2], int.Parse(args[3]), args[4]).GetAwaiter().GetResult();

		// For trying the real app without touching the real pairing: RaccoonAudio --profile <folder> keeps its settings
		// and key in that folder instead, and can run beside the ordinary copy.
		var profile = Array.IndexOf(args, "--profile");
		var separate = "";
		if(profile >= 0 && profile + 1 < args.Length)
		{
			Settings.Folder = Path.GetFullPath(args[profile + 1]);
			separate = "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Settings.Folder.ToLowerInvariant())))[..16];
		}

		// One copy at a time: two would fight over the same connection to raccoon man
		using var single = new Mutex(true, "RaccoonAudio-one-at-a-time" + separate, out var first);
		if(!first)
		{
			MessageBox.Show("Raccoon Audio is already running. Look for its icon by the clock.", "Raccoon Audio");
			return 0;
		}
		ApplicationConfiguration.Initialize();
#if DEBUG
		// In a development build, touching the window from the wrong thread fails loudly instead of now and then
		// freezing it
		Control.CheckForIllegalCrossThreadCalls = true;
#endif
		// Started with Windows, it sits by the clock without opening its window
		Application.Run(new TrayApp(startHidden: args.Contains("--tray")));
		return 0;
	}

	// Pairs with a code, signs in again with the key it got, goes live, streams a tone at the right pace, says what's
	// "playing", stops, and writes down everything the server said. For trying the app against a copy of the bot's
	// server on this computer.
	static async Task<int> ConnectTest(string address, string code, int seconds, string reportFile)
	{
		var lines = new List<string>();
		void Note(string line) { lock(lines) lines.Add(line); }
		string? key = null;
		var client = new Core.StreamClient(new Uri(address), null);
		client.StateChanged += (state) => Note("state: " + state);
		client.Problem += (text) => Note("problem: " + text);
		client.Said += (message) => Note("bot: " + message.Type + " " + string.Join(", ", message.Fields.Select((f) => f.Key + "=" + f.Value)));
		client.Paired += (given) => { key = given; Note("paired: got a key of " + given.Length + " characters"); };
		async Task<bool> Until(Func<bool> happened, int withinSeconds = 15)
		{
			for(var i = 0; i < withinSeconds * 20 && !happened(); i++) await Task.Delay(50);
			return happened();
		}
		var ok = true;
		client.Pair(code, Environment.MachineName + " (connect test)");
		ok &= await Until(() => client.State == Core.ClientState.Connected);
		Note("paired and signed in: " + ok);
		await client.StopAsync();

		// Again, the way it connects every other time: with the key
		var again = new Core.StreamClient(new Uri(address), key);
		again.StateChanged += (state) => Note("state: " + state);
		again.Problem += (text) => Note("problem: " + text);
		again.Said += (message) => Note("bot: " + message.Type + " " + string.Join(", ", message.Fields.Select((f) => f.Key + "=" + f.Value)));
		again.Start();
		again.GoLive();
		var wentLive = await Until(() => again.State == Core.ClientState.Live);
		Note("signed in with the key and went live: " + wentLive);
		ok &= wentLive;
		if(wentLive)
		{
			again.SendNow("Test tone", "Raccoon Audio", true);
			var framer = new Core.OpusFramer();
			var sent = 0;
			// 20 ms of a 440 Hz tone at a time, paced by the clock like real sound
			var piece = new byte[Core.Protocol.SamplesPerFrame * Core.Protocol.Channels * 2];
			var started = Environment.TickCount64;
			for(var frame = 0; frame < seconds * 1000 / Core.Protocol.FrameMs; frame++)
			{
				for(var i = 0; i < Core.Protocol.SamplesPerFrame; i++)
				{
					var value = (short)(Math.Sin(2 * Math.PI * 440 * (frame * Core.Protocol.SamplesPerFrame + i) / Core.Protocol.SampleRate) * 8000);
					for(var channel = 0; channel < Core.Protocol.Channels; channel++)
					{
						piece[(i * Core.Protocol.Channels + channel) * 2] = (byte)(value & 0xff);
						piece[(i * Core.Protocol.Channels + channel) * 2 + 1] = (byte)(value >> 8);
					}
				}
				framer.Push(piece, (packet) => { again.SendAudio(packet); sent++; });
				var due = started + (frame + 1) * Core.Protocol.FrameMs;
				var early = due - Environment.TickCount64;
				if(early > 0) await Task.Delay((int)early);
			}
			Note("packets sent: " + sent);
			again.StopLive();
			ok &= await Until(() => again.State == Core.ClientState.Connected);
		}
		await again.StopAsync();
		Note(ok ? "OK" : "FAILED");
		File.WriteAllLines(reportFile, lines);
		return ok ? 0 : 1;
	}

	// How much of the last second of sound isn't a 220 Hz tone, compared with how much is: for checking the capture
	// against a test tone. Very negative is clean; near zero means it isn't that tone at all.
	static double Noise(List<short> samples)
	{
		var take = Math.Min(samples.Count, 48000);
		if(take < 4800) return 0;
		var from = samples.Count - take;
		double sine = 0, cosine = 0;
		for(var i = 0; i < take; i++)
		{
			var angle = 2 * Math.PI * 220 * i / 48000;
			sine += samples[from + i] * Math.Sin(angle);
			cosine += samples[from + i] * Math.Cos(angle);
		}
		sine *= 2.0 / take;
		cosine *= 2.0 / take;
		double tone = 0, rest = 0;
		for(var i = 0; i < take; i++)
		{
			var angle = 2 * Math.PI * 220 * i / 48000;
			var fitted = sine * Math.Sin(angle) + cosine * Math.Cos(angle);
			tone += fitted * fitted;
			rest += (samples[from + i] - fitted) * (samples[from + i] - fitted);
		}
		return tone == 0 ? 0 : 10 * Math.Log10(Math.Max(rest, 1e-9) / tone);
	}

	// Captures a program's sound for a few seconds, encodes it, and writes down what it got. Sends nothing anywhere.
	static int CaptureTest(int processId, int seconds, string reportFile, string? quietProgram)
	{
		var lines = new List<string> { "Windows build " + Environment.OSVersion.Version.Build + ", per-program capture supported: " + ProcessLoopbackCapture.Supported };
		long bytes = 0;
		long loudest = 0;
		long clipped = 0;
		var left = new List<short>();
		var packets = 0;
		var biggest = 0;
		Exception? failure = null;
		var framer = new Core.OpusFramer();
		using var capture = new ProcessLoopbackCapture();
		capture.Sound += (buffer, count) =>
		{
			bytes += count;
			for(var i = 0; i + 1 < count; i += 2)
			{
				var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
				loudest = Math.Max(loudest, Math.Abs((int)sample));
				if(Math.Abs((int)sample) >= 32767) clipped++;
				if(i % 4 == 0) left.Add(sample);
			}
			framer.Push(buffer.AsSpan(0, count), (packet) => { packets++; biggest = Math.Max(biggest, packet.Length); });
		};
		capture.Failed += (err) => failure = err;
		float? was = null;
		try
		{
			capture.Start(processId);
			if(quietProgram != null)
			{
				was = AudioPrograms.TurnDown(quietProgram, TrayApp.QuietLevel);
				if(AudioPrograms.TurnedDown(quietProgram, TrayApp.QuietLevel)) capture.QuietLevel = TrayApp.QuietLevel;
			}
			Thread.Sleep(seconds * 1000);
		}
		finally
		{
			if(quietProgram != null && was != null) AudioPrograms.TurnBackUp(quietProgram, TrayApp.QuietLevel, was.Value);
		}
		var stillRunning = capture.Running;
		capture.Stop();
		if(quietProgram != null) lines.Add(was == null ? "Nothing to turn down: " + quietProgram + " has no sound open, or was already that quiet." : "Turned down on this computer meanwhile, from " + was.Value.ToString("0.00") + " and back.");
		lines.Add("Samples at the loudest possible value: " + clipped + ", noise next to a 220 Hz test tone: " + Noise(left).ToString("0.0") + " dB");
		lines.Add("Capture thread still running after " + seconds + " s: " + stillRunning);
		lines.Add("PCM bytes captured: " + bytes + " (" + (bytes / 192000.0).ToString("0.00") + " s of sound)");
		lines.Add("Loudest sample: " + loudest + " of 32767");
		lines.Add("Opus packets: " + packets + ", biggest " + biggest + " bytes");
		lines.Add(failure == null ? "No error." : "FAILED: " + failure.Message + (failure.InnerException != null ? " / " + failure.InnerException.Message : ""));
		File.WriteAllLines(reportFile, lines);
		return failure == null ? 0 : 1;
	}
}
