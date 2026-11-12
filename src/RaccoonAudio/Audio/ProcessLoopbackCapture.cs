using System.Runtime.InteropServices;

namespace RaccoonAudio.Audio;

// Captures the sound of ONE program (and the programs it started), not the whole computer. Windows 11 can do this:
// an audio client is "activated" on a virtual device called VAD\Process_Loopback with the program's process id, and
// from then on it hands over exactly what that program plays. Notifications, calls, games, other browser tabs in
// another program: none of that is in it.
//
// It asks for 48 kHz, 16-bit stereo, which is what gets encoded, and Windows converts whatever the program is
// actually playing to that. When the program is silent Windows hands over nothing at all, so nothing is sent.
//
// This is the same sequence as Microsoft's ApplicationLoopback sample, written against the Windows audio API
// directly, because the audio library the app uses for everything else has no call for it.
sealed class ProcessLoopbackCapture : IDisposable
{
	// Sound, as 16-bit samples, left then right. The buffer is reused: copy what's needed before returning.
	public event Action<byte[], int>? Sound;
	// Capturing stopped because something went wrong (not because Stop was called)
	public event Action<Exception>? Failed;

	Thread? thread;
	volatile bool stopping;

	public bool Running => thread is { IsAlive: true };

	// Per-program capture arrived in Windows build 20348 (Windows 11, and Server 2022)
	public static bool Supported => Environment.OSVersion.Version.Build >= 20348;

	public void Start(int processId)
	{
		Stop();
		stopping = false;
		thread = new Thread(() => Run(processId)) { IsBackground = true, Name = "Raccoon Audio capture" };
		// Not the window's thread: the activation call must not be made from a single-threaded apartment that's
		// busy pumping the window's messages
		thread.SetApartmentState(ApartmentState.MTA);
		thread.Start();
	}

	public void Stop()
	{
		stopping = true;
		var ending = thread;
		thread = null;
		if(ending != null && ending != Thread.CurrentThread) ending.Join(2000);
	}

	public void Dispose() => Stop();

	void Run(int processId)
	{
		IAudioClient? client = null;
		IAudioCaptureClient? capture = null;
		var paramsPointer = IntPtr.Zero;
		var variantPointer = IntPtr.Zero;
		var formatPointer = IntPtr.Zero;
		using var ready = new AutoResetEvent(false);
		try
		{
			// Which program, and that its child processes count too (a browser or Spotify plays from several)
			var activation = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = ActivationTypeProcessLoopback, TargetProcessId = (uint)processId, ProcessLoopbackMode = IncludeProcessTree };
			paramsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
			Marshal.StructureToPtr(activation, paramsPointer, false);
			var variant = new PROPVARIANT { vt = VT_BLOB, blobSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(), blobData = paramsPointer };
			variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
			Marshal.StructureToPtr(variant, variantPointer, false);

			var handler = new ActivationHandler();
			var audioClientId = typeof(IAudioClient).GUID;
			Check(ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref audioClientId, variantPointer, handler, out var operation), "asking Windows for the program's sound");
			if(!handler.Done.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Windows didn't answer when asked for the program's sound.");
			Check(operation.GetActivateResult(out var result, out var activated), "reading Windows' answer");
			Check(result, "getting the program's sound");
			client = (IAudioClient)activated;

			var format = new WAVEFORMATEX { wFormatTag = 1, nChannels = Channels, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = Channels * 2, nAvgBytesPerSec = SampleRate * Channels * 2, cbSize = 0 };
			formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
			Marshal.StructureToPtr(format, formatPointer, false);
			// Shared, loopback, woken by an event, and converted by Windows to the format asked for
			Check(client.Initialize(0, StreamLoopback | StreamEventCallback | StreamAutoConvert | StreamDefaultQuality, 200_000, 0, formatPointer, IntPtr.Zero), "setting up the capture");
			Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "setting up the capture");
			var captureId = typeof(IAudioCaptureClient).GUID;
			Check(client.GetService(ref captureId, out var service), "setting up the capture");
			capture = (IAudioCaptureClient)service;
			Check(client.Start(), "starting the capture");

			var buffer = new byte[SampleRate * Channels * 2 / 5];
			while(!stopping)
			{
				// Woken when there's sound. The timeout is only so Stop is noticed while the program is silent.
				ready.WaitOne(100);
				while(!stopping)
				{
					Check(capture.GetNextPacketSize(out var waiting), "reading the sound");
					if(waiting == 0) break;
					Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _), "reading the sound");
					var bytes = (int)frames * Channels * 2;
					if(bytes > buffer.Length) buffer = new byte[bytes];
					if((flags & BufferSilent) != 0) Array.Clear(buffer, 0, bytes);
					else Marshal.Copy(data, buffer, 0, bytes);
					Check(capture.ReleaseBuffer(frames), "reading the sound");
					if(bytes > 0) Sound?.Invoke(buffer, bytes);
				}
			}
			client.Stop();
		}
		catch(Exception err)
		{
			if(!stopping) Failed?.Invoke(err);
		}
		finally
		{
			if(capture != null) Marshal.ReleaseComObject(capture);
			if(client != null) Marshal.ReleaseComObject(client);
			if(formatPointer != IntPtr.Zero) Marshal.FreeHGlobal(formatPointer);
			if(variantPointer != IntPtr.Zero) Marshal.FreeHGlobal(variantPointer);
			if(paramsPointer != IntPtr.Zero) Marshal.FreeHGlobal(paramsPointer);
		}
	}

	static void Check(int result, string doing)
	{
		if(result < 0) throw new InvalidOperationException("Something went wrong " + doing + " (Windows error 0x" + result.ToString("X8") + ").", Marshal.GetExceptionForHR(result));
	}

	// --- The Windows audio API, as much of it as this needs ---

	const int SampleRate = 48000;
	const ushort Channels = 2;
	const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
	const int ActivationTypeProcessLoopback = 1;
	const int IncludeProcessTree = 0;
	const ushort VT_BLOB = 65;
	const uint StreamLoopback = 0x00020000;
	const uint StreamEventCallback = 0x00040000;
	const uint StreamAutoConvert = 0x80000000;
	const uint StreamDefaultQuality = 0x08000000;
	const uint BufferSilent = 0x2;

	[StructLayout(LayoutKind.Sequential)]
	struct AUDIOCLIENT_ACTIVATION_PARAMS
	{
		public int ActivationType;
		public uint TargetProcessId;
		public int ProcessLoopbackMode;
	}

	// A PROPVARIANT holding a blob: its type, six unused bytes, then the blob's size and address
	[StructLayout(LayoutKind.Sequential)]
	struct PROPVARIANT
	{
		public ushort vt;
		public ushort reserved1;
		public ushort reserved2;
		public ushort reserved3;
		public uint blobSize;
		public IntPtr blobData;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 2)]
	struct WAVEFORMATEX
	{
		public ushort wFormatTag;
		public ushort nChannels;
		public uint nSamplesPerSec;
		public uint nAvgBytesPerSec;
		public ushort nBlockAlign;
		public ushort wBitsPerSample;
		public ushort cbSize;
	}

	[DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
	static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid interfaceId, IntPtr activationParams,
		IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

	[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface IActivateAudioInterfaceCompletionHandler
	{
		[PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
	}

	[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface IActivateAudioInterfaceAsyncOperation
	{
		[PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
	}

	// Windows calls the handler back on a thread of its own, and only accepts one that says it can be called from any
	// thread. This empty interface is how an object says so.
	[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface IAgileObject { }

	sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
	{
		public readonly ManualResetEvent Done = new(false);

		public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
		{
			Done.Set();
			return 0;
		}
	}

	[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface IAudioClient
	{
		[PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
		[PreserveSig] int GetBufferSize(out uint bufferSize);
		[PreserveSig] int GetStreamLatency(out long latency);
		[PreserveSig] int GetCurrentPadding(out uint padding);
		[PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
		[PreserveSig] int GetMixFormat(out IntPtr format);
		[PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
		[PreserveSig] int Start();
		[PreserveSig] int Stop();
		[PreserveSig] int Reset();
		[PreserveSig] int SetEventHandle(IntPtr eventHandle);
		[PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
	}

	[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	interface IAudioCaptureClient
	{
		[PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong performanceCounterPosition);
		[PreserveSig] int ReleaseBuffer(uint frames);
		[PreserveSig] int GetNextPacketSize(out uint frames);
	}
}
