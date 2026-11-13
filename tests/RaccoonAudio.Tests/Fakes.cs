using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RaccoonAudio.Core;

namespace RaccoonAudio.Tests;

// A connection that goes nowhere: it records what the client sends, and the test plays the bot
sealed class FakeSocket : IStreamSocket
{
	readonly Channel<SocketFrame?> incoming = Channel.CreateUnbounded<SocketFrame?>();
	readonly object gate = new();
	readonly List<string> texts = [];
	readonly List<byte[]> packets = [];

	public Exception? FailConnect;
	// While set, audio sends hang, as on a connection that can't keep up
	public TaskCompletionSource? HoldAudio;
	public bool Connected;
	public bool Disposed;
	public string? CloseReason => null;

	public string[] Texts { get { lock(gate) return texts.ToArray(); } }
	public byte[][] Packets { get { lock(gate) return packets.ToArray(); } }
	// The types of the messages it was sent, in order
	public string[] Types => Texts.Select((text) => JsonDocument.Parse(text).RootElement.GetProperty("type").GetString()!).ToArray();

	public Task ConnectAsync(Uri server, CancellationToken cancel)
	{
		if(FailConnect != null) return Task.FromException(FailConnect);
		Connected = true;
		return Task.CompletedTask;
	}

	public Task SendTextAsync(string message, CancellationToken cancel)
	{
		lock(gate) texts.Add(message);
		return Task.CompletedTask;
	}

	public async Task SendBinaryAsync(ReadOnlyMemory<byte> packet, CancellationToken cancel)
	{
		if(HoldAudio != null) await HoldAudio.Task.WaitAsync(cancel);
		lock(gate) packets.Add(packet.ToArray());
	}

	public async Task<SocketFrame?> ReceiveAsync(CancellationToken cancel) => await incoming.Reader.ReadAsync(cancel);

	// The bot says something
	public void Say(object message) => incoming.Writer.TryWrite(new SocketFrame(true, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message))));
	public void SayRaw(string text) => incoming.Writer.TryWrite(new SocketFrame(true, Encoding.UTF8.GetBytes(text)));
	// The bot closes the connection
	public void Close() => incoming.Writer.TryWrite(null);

	public void Dispose() => Disposed = true;
}

// A client wired to fake connections, with waits that take no time but are written down
sealed class Rig : IDisposable
{
	public readonly List<FakeSocket> Sockets = [];
	public readonly List<TimeSpan> Waits = [];
	public readonly List<ClientState> States = [];
	public readonly List<ServerMessage> Said = [];
	public readonly List<string> Problems = [];
	public readonly List<string> Keys = [];
	public int KeyRejections;
	// How many of the next connections fail to connect at all
	public int FailNext;
	public readonly StreamClient Client;

	public Rig(string? token = "key-1")
	{
		Client = new StreamClient(new Uri("ws://127.0.0.1:1/stream"), token, () =>
		{
			var socket = new FakeSocket();
			lock(Sockets)
			{
				if(FailNext > 0)
				{
					FailNext--;
					socket.FailConnect = new IOException("no route");
				}
				Sockets.Add(socket);
			}
			return socket;
		}, (delay, cancel) =>
		{
			lock(Waits) Waits.Add(delay);
			return Task.CompletedTask;
		});
		Client.StateChanged += (state) => { lock(States) States.Add(state); };
		Client.Said += (message) => { lock(Said) Said.Add(message); };
		Client.Problem += (text) => { lock(Problems) Problems.Add(text); };
		Client.Paired += (key) => { lock(Keys) Keys.Add(key); };
		Client.KeyRejected += () => Interlocked.Increment(ref KeyRejections);
	}

	public int SocketCount { get { lock(Sockets) return Sockets.Count; } }

	// The nth connection it made (from 1), once it's connected
	public async Task<FakeSocket> SocketAsync(int number)
	{
		await Until("connection " + number, () => SocketCount >= number && Sockets[number - 1].Connected);
		return Sockets[number - 1];
	}

	// Signs the nth connection in, the way the bot would
	public async Task<FakeSocket> SignedInAsync(int number = 1)
	{
		var socket = await SocketAsync(number);
		await Until("hello on connection " + number, () => socket.Types.Length >= 1);
		socket.Say(new { type = "welcome", v = Protocol.Version, name = "Raccoon Man" });
		await Until("being signed in", () => Client.State is ClientState.Connected or ClientState.Live || socket.Types.Contains("live"));
		return socket;
	}

	public static async Task Until(string what, Func<bool> happened, int seconds = 20)
	{
		var deadline = DateTime.UtcNow.AddSeconds(seconds);
		while(!happened())
		{
			if(DateTime.UtcNow > deadline) Assert.Fail("still waiting after " + seconds + " seconds for: " + what);
			await Task.Delay(5);
		}
	}

	public void Dispose()
	{
		Client.StopAsync().Wait(TimeSpan.FromSeconds(5));
		Client.Dispose();
	}
}
