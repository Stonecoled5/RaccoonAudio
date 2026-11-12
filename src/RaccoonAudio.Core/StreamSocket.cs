using System.Net.WebSockets;
using System.Text;

namespace RaccoonAudio.Core;

// One frame from the bot: text (a message), or binary (it never sends any, but it's told apart)
public readonly record struct SocketFrame(bool IsText, ReadOnlyMemory<byte> Data);

// The connection to the bot, as little as the client needs of it, so the client's logic can be tested without one
public interface IStreamSocket : IDisposable
{
	Task ConnectAsync(Uri server, CancellationToken cancel);
	Task SendTextAsync(string message, CancellationToken cancel);
	Task SendBinaryAsync(ReadOnlyMemory<byte> packet, CancellationToken cancel);
	// The next frame, or null once the bot has closed the connection. Throws if it breaks.
	Task<SocketFrame?> ReceiveAsync(CancellationToken cancel);
	// Why the bot closed it, if it said: the error code is sent as the close reason
	string? CloseReason { get; }
}

// The real thing. It's a program's connection, not a web page's: no Origin header is ever sent, and the bot refuses
// any connection that has one.
public sealed class WebSocketStreamSocket : IStreamSocket
{
	readonly ClientWebSocket socket = new();
	readonly byte[] buffer = new byte[16 * 1024];

	public string? CloseReason => socket.CloseStatusDescription;

	public WebSocketStreamSocket()
	{
		// The bot pings every 30 seconds and this answers on its own. Its own pings on top keep a quiet connection
		// from being dropped by anything in between.
		socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
	}

	public Task ConnectAsync(Uri server, CancellationToken cancel) => socket.ConnectAsync(server, cancel);

	public Task SendTextAsync(string message, CancellationToken cancel) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancel);

	public Task SendBinaryAsync(ReadOnlyMemory<byte> packet, CancellationToken cancel) =>
		socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancel).AsTask();

	public async Task<SocketFrame?> ReceiveAsync(CancellationToken cancel)
	{
		using var whole = new MemoryStream();
		while(true)
		{
			var result = await socket.ReceiveAsync(buffer, cancel);
			if(result.MessageType == WebSocketMessageType.Close) return null;
			whole.Write(buffer, 0, result.Count);
			// Nothing the bot sends is anywhere near this big
			if(whole.Length > 64 * 1024) throw new InvalidOperationException("The bot sent something far too big.");
			if(result.EndOfMessage) return new SocketFrame(result.MessageType == WebSocketMessageType.Text, whole.ToArray());
		}
	}

	public void Dispose() => socket.Dispose();
}
