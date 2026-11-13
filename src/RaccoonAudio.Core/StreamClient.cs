using System.Text;
using System.Threading.Channels;

namespace RaccoonAudio.Core;

public enum ClientState
{
	// Not running
	Off,
	// No key yet: waiting for a pairing code
	NeedsPairing,
	Connecting,
	SigningIn,
	// Signed in, not streaming
	Connected,
	// Streaming
	Live,
	// The connection dropped, or couldn't be made: trying again shortly
	Reconnecting,
	// The bot is newer than this app. Nothing works until the app is updated.
	NeedsUpdate
}

// The app's side of the conversation with the bot: connecting, saying who it is (a paired computer's key, or a
// pairing code for a new one), going live, sending the audio, and coming back by itself when the connection drops.
//
// The bot restarts at least once a day, so reconnecting is ordinary, not an error. If it was live when the connection
// went, it signs in again and goes live again without anyone touching anything; the bot holds the stream open for 30
// seconds for exactly that.
//
// Everything here is plain logic over an IStreamSocket, so it's tested with a fake one. Nothing in it touches Windows.
public sealed class StreamClient : IDisposable
{
	// How long to wait before each try: 1, 2, 5, then 10 seconds for as long as it takes
	public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
	// After too many wrong codes or keys the bot shuts this address out for ten minutes, so there's no point sooner
	public static readonly TimeSpan ShutOutDelay = TimeSpan.FromMinutes(10);
	// After a drop mid-stream, the bot may be back before it's ready to play (it restarts at least daily, and its
	// stream server comes up before the rest of it). So when it won't take the stream back, it's asked again a few
	// times before giving up: this often, this many times.
	public static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(3);
	public const int ResumeTries = 10;
	// Audio waiting to go out. Past this the oldest is dropped: sound that's late is worse than sound that's missing.
	public const int MaxWaitingPackets = 15;

	readonly Func<IStreamSocket> makeSocket;
	readonly Func<TimeSpan, CancellationToken, Task> wait;
	readonly object gate = new();
	CancellationTokenSource? running;
	Task? loop;
	Channel<Outgoing>? outbox;
	int waitingPackets;
	string? token;
	(string Code, string Device)? pairing;
	bool wantLive;
	bool askedLive;
	// The connection dropped while live, and the stream hasn't been picked back up yet
	bool resuming;
	int resumeTries;
	// Counts connections, so something scheduled on one never acts on the next
	int connection;
	// Waiting for the bot to confirm an unpair: true once it has, false if it said no
	TaskCompletionSource<bool>? unpairing;
	// Set when a key or code arrives while it's waiting for one
	TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

	// What the bot is called until it says otherwise. It can be renamed, and says its name when the app signs in.
	public const string DefaultBotName = "Raccoon Man";

	public Uri Server { get; set; }
	// The bot's name as it gave it when this app last signed in, or null before then
	public string? BotName { get; private set; }
	public ClientState State { get; private set; } = ClientState.Off;
	public bool WantsLive => wantLive;

	public event Action<ClientState>? StateChanged;
	// pause, resume, skip, request, notify and ended: things for the app to act on or show
	public event Action<ServerMessage>? Said;
	// Something to tell the person, in the bot's words or the app's
	public event Action<string>? Problem;
	// A new computer was paired: the key to keep (protected) and show nobody
	public event Action<string>? Paired;
	// The bot no longer knows this computer's key. The app should forget it.
	public event Action? KeyRejected;
	// The bot gave a name different from the one before (it was renamed, or this is the first sign-in)
	public event Action<string>? NameChanged;

	readonly record struct Outgoing(string? Text, ReadOnlyMemory<byte> Packet);

	public StreamClient(Uri server, string? token, Func<IStreamSocket>? makeSocket = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
	{
		Server = server;
		this.token = token;
		this.makeSocket = makeSocket ?? (() => new WebSocketStreamSocket());
		this.wait = wait ?? Task.Delay;
	}

	public bool HasKey => token != null;

	// Starts connecting, and keeps a connection up until Stop
	public void Start()
	{
		lock(gate)
		{
			if(loop != null) return;
			running = new CancellationTokenSource();
			loop = Task.Run(() => RunAsync(running.Token));
		}
	}

	// Disconnects and stays off. An open stream is ended first, so the bot leaves voice at once instead of waiting.
	public async Task StopAsync()
	{
		Task? ending;
		lock(gate)
		{
			ending = loop;
			if(ending == null) return;
			wantLive = false;
			if(State == ClientState.Live) outbox?.Writer.TryWrite(new Outgoing(Protocol.Stop(), default));
		}
		// A moment for the stop to go out before the connection is cut
		await Task.Delay(150);
		running?.Cancel();
		try { await ending; } catch(OperationCanceledException) { }
		lock(gate)
		{
			loop = null;
			running?.Dispose();
			running = null;
		}
		SetState(ClientState.Off);
	}

	// Pairs this computer with a code from /me. The answer comes as Paired, or as a Problem.
	public void Pair(string code, string device)
	{
		lock(gate)
		{
			pairing = (code, device);
			wake.TrySetResult();
		}
		Start();
	}

	// Tries again now, after it stopped trying (it was replaced by another copy, or had nothing to sign in with)
	public void Retry()
	{
		lock(gate) wake.TrySetResult();
		Start();
	}

	// Asks the bot to forget this computer, so it comes off the list in /me as well. True once the bot confirms (the
	// key is forgotten here too, and it goes back to waiting for a pairing code); false if it isn't connected or doesn't
	// answer in time, in which case only /me can remove it from the bot's side.
	public async Task<bool> UnpairAsync(TimeSpan timeout)
	{
		TaskCompletionSource<bool> answer;
		lock(gate)
		{
			if(State != ClientState.Connected && State != ClientState.Live) return false;
			answer = unpairing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			SendText(Protocol.Unpair());
		}
		using var giveUp = new CancellationTokenSource();
		// A real clock, not the retry waits (which tests make instant)
		var timer = Task.Delay(timeout, giveUp.Token);
		var first = await Task.WhenAny(answer.Task, timer);
		giveUp.Cancel();
		lock(gate) if(unpairing == answer) unpairing = null;
		return first == answer.Task && answer.Task.Result;
	}

	// Forgets this computer's key, here only. The bot's side is removed by UnpairAsync, or from /radio's Audio app page in Discord.
	public void ForgetKey()
	{
		lock(gate)
		{
			token = null;
			wantLive = false;
		}
	}

	// Go live: now if it's connected, or as soon as it is
	public void GoLive()
	{
		lock(gate)
		{
			wantLive = true;
			if(State == ClientState.Connected) AskLive();
		}
	}

	// Stop streaming, and stay connected
	public void StopLive()
	{
		lock(gate)
		{
			wantLive = false;
			resuming = false;
			if(State == ClientState.Live || askedLive) SendText(Protocol.Stop());
			askedLive = false;
		}
	}

	// One Opus packet. Dropped unless it's live: nothing is sent to the bot that it didn't ask for.
	public void SendAudio(ReadOnlyMemory<byte> packet)
	{
		if(State != ClientState.Live || packet.Length == 0 || packet.Length > Protocol.MaxAudioFrameBytes) return;
		var box = outbox;
		if(box == null) return;
		// The connection has fallen behind: this packet is dropped rather than queued behind stale ones
		if(Interlocked.Increment(ref waitingPackets) > MaxWaitingPackets)
		{
			Interlocked.Decrement(ref waitingPackets);
			return;
		}
		if(!box.Writer.TryWrite(new Outgoing(null, packet))) Interlocked.Decrement(ref waitingPackets);
	}

	// What's playing, while live
	public void SendNow(string title, string artist, bool playing)
	{
		if(State == ClientState.Live) SendText(Protocol.Now(title, artist, playing));
	}

	// The streamer's Spotify Jam link, while live. False if it isn't one the bot would post.
	public bool SendJam(string url)
	{
		if(!Protocol.IsJamLink(url)) return false;
		if(State == ClientState.Live) SendText(Protocol.Jam(url.Trim()));
		return true;
	}

	// A song request has been dealt with, while live. False if it couldn't be said right now (not live): the bot only
	// keeps requests for the stream that's on, so it's said again later or not at all.
	public bool SendDone(long id)
	{
		if(State != ClientState.Live) return false;
		SendText(Protocol.Done(id));
		return true;
	}

	// Every song request has been dealt with, while live
	public bool SendClear()
	{
		if(State != ClientState.Live) return false;
		SendText(Protocol.Clear());
		return true;
	}

	void AskLive()
	{
		askedLive = true;
		SendText(Protocol.Live());
	}

	// Asks to go live again after a pause, if it's still the same connection and still wanted
	void AskLiveLater(int onConnection)
	{
		var cancel = running?.Token ?? CancellationToken.None;
		_ = Task.Run(async () =>
		{
			try { await wait(ResumeDelay, cancel); } catch(OperationCanceledException) { return; }
			lock(gate)
			{
				if(connection == onConnection && wantLive && State == ClientState.Connected && !askedLive) AskLive();
			}
		});
	}

	void SendText(string message) => outbox?.Writer.TryWrite(new Outgoing(message, default));

	void SetState(ClientState state)
	{
		if(State == state) return;
		State = state;
		StateChanged?.Invoke(state);
	}

	async Task RunAsync(CancellationToken cancel)
	{
		var tries = 0;
		while(!cancel.IsCancellationRequested)
		{
			string? key;
			(string Code, string Device)? code;
			Task woken;
			lock(gate)
			{
				key = token;
				code = pairing;
				pairing = null;
				if(wake.Task.IsCompleted) wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				woken = wake.Task;
			}
			if(key == null && code == null)
			{
				// Nothing to sign in with until someone types a pairing code
				SetState(ClientState.NeedsPairing);
				await woken.WaitAsync(cancel).ContinueWith((_) => { }, TaskScheduler.Default);
				continue;
			}

			var after = await ConnectOnceAsync(key, code, cancel);
			if(cancel.IsCancellationRequested) break;
			if(after == After.Stay) continue;
			if(after == After.Halt)
			{
				// Nothing more to try until something changes: an update, or (after being replaced) the person
				// starting it again
				await woken.WaitAsync(cancel).ContinueWith((_) => { }, TaskScheduler.Default);
				continue;
			}
			tries = after == After.RetrySoon ? 0 : tries + 1;
			SetState(ClientState.Reconnecting);
			var delay = after == After.RetryMuchLater ? ShutOutDelay : RetryDelays[Math.Min(Math.Max(tries - 1, 0), RetryDelays.Length - 1)];
			try { await wait(delay, cancel); } catch(OperationCanceledException) { break; }
		}
	}

	enum After
	{
		// It was signed in and then dropped: try again straight away, from the shortest wait
		RetrySoon,
		// It couldn't connect or sign in: try again after the next wait
		Retry,
		// The bot has shut this address out for a while
		RetryMuchLater,
		// Go round again without waiting (pairing failed: back to asking for a code)
		Stay,
		// Stop trying until told otherwise
		Halt
	}

	async Task<After> ConnectOnceAsync(string? key, (string Code, string Device)? code, CancellationToken cancel)
	{
		var signedIn = false;
		var outcome = After.Retry;
		using var socket = makeSocket();
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
		var box = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
		Task? sending = null;
		try
		{
			SetState(ClientState.Connecting);
			await socket.ConnectAsync(Server, linked.Token);
			SetState(ClientState.SigningIn);
			lock(gate)
			{
				outbox = box;
				waitingPackets = 0;
				askedLive = false;
				connection++;
			}
			sending = SendLoopAsync(socket, box.Reader, linked.Token);
			SendText(code is { } typed ? Protocol.Pair(typed.Code, typed.Device) : Protocol.Hello(key!));

			while(true)
			{
				var frame = await socket.ReceiveAsync(linked.Token);
				if(frame == null) break;
				if(!frame.Value.IsText) continue;
				var message = Protocol.ReadServer(Encoding.UTF8.GetString(frame.Value.Data.Span));
				// Something this app doesn't understand: most likely a newer bot. Ignored rather than guessed at.
				if(message == null) continue;
				var decided = Handle(message, ref signedIn);
				if(decided is After final)
				{
					outcome = final;
					break;
				}
			}
			if(signedIn && outcome == After.Retry) outcome = After.RetrySoon;
		}
		catch(OperationCanceledException) when(cancel.IsCancellationRequested)
		{
			return After.Halt;
		}
		catch(Exception)
		{
			// The connection couldn't be made, or broke. Either way: try again.
			if(signedIn) outcome = After.RetrySoon;
		}
		finally
		{
			lock(gate)
			{
				outbox = null;
				// Live when it went: the stream is to be picked back up, not started as if new
				if(State == ClientState.Live)
				{
					resuming = true;
					resumeTries = 0;
				}
			}
			box.Writer.TryComplete();
			linked.Cancel();
			if(sending != null) try { await sending; } catch(Exception) { }
		}
		return outcome;
	}

	// What to do about one message. Returns how to carry on if the connection is over, or null to keep listening.
	After? Handle(ServerMessage message, ref bool signedIn)
	{
		switch(message.Type)
		{
			case "paired":
				lock(gate) token = message.Text("token");
				Paired?.Invoke(message.Text("token"));
				return null;
			case "welcome":
				signedIn = true;
				var name = message.Text("name").Trim();
				if(name != "" && name != BotName)
				{
					BotName = name;
					NameChanged?.Invoke(name);
				}
				lock(gate)
				{
					SetState(ClientState.Connected);
					// It was live when the connection dropped, or Go live was pressed while it was still connecting
					if(wantLive) AskLive();
				}
				return null;
			case "live":
				lock(gate)
				{
					askedLive = false;
					resuming = false;
				}
				SetState(ClientState.Live);
				return null;
			case "ended":
				lock(gate)
				{
					wantLive = false;
					askedLive = false;
					resuming = false;
				}
				SetState(ClientState.Connected);
				Said?.Invoke(message);
				return null;
			case "unpaired":
				// The bot has forgotten this computer: so does the app, and it goes back to asking for a code
				lock(gate)
				{
					token = null;
					wantLive = false;
					unpairing?.TrySetResult(true);
				}
				return After.Stay;
			case "error":
				lock(gate) unpairing?.TrySetResult(false);
				return HandleError(message.Text("code"), message.Text("message"), signedIn);
			default:
				Said?.Invoke(message);
				return null;
		}
	}

	After? HandleError(string code, string words, bool signedIn)
	{
		switch(code)
		{
			case "unavailable" when signedIn:
				lock(gate)
				{
					// Picking a stream back up after a drop: he may just not be ready yet, so ask again shortly
					if(resuming && wantLive && resumeTries < ResumeTries)
					{
						resumeTries++;
						askedLive = false;
						AskLiveLater(connection);
						return null;
					}
					// He couldn't start the stream (not in voice, someone else is streaming...). Still connected; the
					// person reads why and tries again when they're ready.
					wantLive = false;
					askedLive = false;
					resuming = false;
				}
				SetState(ClientState.Connected);
				Problem?.Invoke(words);
				return null;
			case "update":
				SetState(ClientState.NeedsUpdate);
				Problem?.Invoke(words);
				return After.Halt;
			case "bad-token":
				lock(gate)
				{
					token = null;
					wantLive = false;
				}
				KeyRejected?.Invoke();
				Problem?.Invoke(words);
				return After.Stay;
			case "bad-code":
			case "too-many-devices":
				Problem?.Invoke(words);
				// With a key it goes back to signing in with that; without one, back to asking for a code
				return After.Stay;
			case "too-many-tries":
				Problem?.Invoke(words);
				return After.RetryMuchLater;
			case "replaced":
				// This computer connected again from somewhere else (a second copy of the app). Fighting over the
				// connection would bounce both forever, so this one stops.
				lock(gate) wantLive = false;
				SetState(ClientState.Off);
				Problem?.Invoke("This computer connected to " + (BotName ?? DefaultBotName) + " from another copy of the app, so this one stopped.");
				return After.Halt;
			default:
				// busy, timeout, too-fast, bad-message, or the bot's side failing: try again
				if(!string.IsNullOrEmpty(words)) Problem?.Invoke(words);
				return After.Retry;
		}
	}

	// Everything goes out through here, one at a time, in order: a WebSocket takes one send at a time
	async Task SendLoopAsync(IStreamSocket socket, ChannelReader<Outgoing> reader, CancellationToken cancel)
	{
		await foreach(var item in reader.ReadAllAsync(cancel))
		{
			if(item.Text != null) await socket.SendTextAsync(item.Text, cancel);
			else
			{
				Interlocked.Decrement(ref waitingPackets);
				await socket.SendBinaryAsync(item.Packet, cancel);
			}
		}
	}

	public void Dispose()
	{
		running?.Cancel();
		running?.Dispose();
	}
}
