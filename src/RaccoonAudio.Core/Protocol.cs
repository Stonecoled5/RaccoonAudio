using System.Text;
using System.Text.Json;

namespace RaccoonAudio.Core;

// What the app and the bot say to each other. All of it comes from protocol/stream-protocol.json, which is the bot's
// own file copied into this repository: the version, the path, the audio format, how long each field can be, every
// message either side sends, and the error codes. Nothing about the protocol is written down a second time in code,
// so the app can't quietly disagree with the copy it ships with.
//
// Text frames are JSON messages with a "type". Binary frames are audio: one Opus packet each.

// A message from the bot, already checked against the protocol
public sealed record ServerMessage(string Type, IReadOnlyDictionary<string, object> Fields)
{
	public string Text(string name) => Fields.TryGetValue(name, out var value) ? value as string ?? "" : "";
	public long Number(string name) => Fields.TryGetValue(name, out var value) && value is long number ? number : 0;
}

public static class Protocol
{
	public static int Version { get; }
	public static string Path { get; }
	public static int SampleRate { get; }
	public static int Channels { get; }
	public static int FrameMs { get; }
	public static IReadOnlyDictionary<string, int> Limits { get; }
	// Each message's fields and their kinds: "number", "string" or "boolean"
	public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Client { get; }
	public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Server { get; }
	public static IReadOnlyDictionary<string, string> Errors { get; }

	// 20 ms of 48 kHz audio: what goes in one packet
	public static int SamplesPerFrame => SampleRate * FrameMs / 1000;
	public static int MaxAudioFrameBytes => Limits["maxAudioFrameBytes"];

	static Protocol()
	{
		using var stream = typeof(Protocol).Assembly.GetManifestResourceStream("stream-protocol.json")
			?? throw new InvalidOperationException("The protocol file wasn't built into the app.");
		using var document = JsonDocument.Parse(stream);
		var root = document.RootElement;
		Version = root.GetProperty("version").GetInt32();
		Path = root.GetProperty("path").GetString()!;
		var audio = root.GetProperty("audio");
		if(audio.GetProperty("codec").GetString() != "opus") throw new InvalidOperationException("This app only speaks Opus.");
		SampleRate = audio.GetProperty("sampleRate").GetInt32();
		Channels = audio.GetProperty("channels").GetInt32();
		FrameMs = audio.GetProperty("frameMs").GetInt32();
		Limits = root.GetProperty("limits").EnumerateObject().ToDictionary((p) => p.Name, (p) => p.Value.GetInt32());
		Client = Shapes(root.GetProperty("client"));
		Server = Shapes(root.GetProperty("server"));
		Errors = root.GetProperty("errors").EnumerateObject().ToDictionary((p) => p.Name, (p) => p.Value.GetString()!);
	}

	static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Shapes(JsonElement messages) =>
		messages.EnumerateObject().ToDictionary((m) => m.Name,
			(m) => (IReadOnlyDictionary<string, string>)m.Value.EnumerateObject().ToDictionary((f) => f.Name, (f) => f.Value.GetString()!));

	// The longest a text field can be. Fields the protocol gives no limit for can be as long as a whole message.
	public static int LimitFor(string field) => Limits.TryGetValue(field, out var limit) ? limit : Limits["maxTextBytes"];

	// Text cut to what a field allows
	public static string Fit(string field, string text)
	{
		var limit = LimitFor(field);
		return text.Length <= limit ? text : text[..limit];
	}

	// A message for the bot. Throws if it isn't exactly one the protocol lists: an unknown type, a missing or extra
	// field, the wrong kind of value, or text over its limit. Nothing leaves the app that the bot would refuse.
	public static string Build(string type, params (string Name, object Value)[] fields)
	{
		if(!Client.TryGetValue(type, out var shape)) throw new ArgumentException("The app doesn't send \"" + type + "\" messages.");
		if(fields.Length != shape.Count) throw new ArgumentException("\"" + type + "\" takes " + shape.Count + " fields, not " + fields.Length + ".");
		using var buffer = new MemoryStream();
		using(var json = new Utf8JsonWriter(buffer))
		{
			json.WriteStartObject();
			json.WriteString("type", type);
			foreach(var (name, value) in fields)
			{
				if(!shape.TryGetValue(name, out var kind)) throw new ArgumentException("\"" + type + "\" has no field \"" + name + "\".");
				switch(kind)
				{
					case "string" when value is string text:
						if(text.Length > LimitFor(name)) throw new ArgumentException("\"" + name + "\" is longer than " + LimitFor(name) + " characters.");
						json.WriteString(name, text);
						break;
					case "number" when value is int number:
						json.WriteNumber(name, number);
						break;
					case "boolean" when value is bool flag:
						json.WriteBoolean(name, flag);
						break;
					default:
						throw new ArgumentException("\"" + name + "\" has to be a " + kind + ".");
				}
			}
			json.WriteEndObject();
		}
		var message = Encoding.UTF8.GetString(buffer.ToArray());
		if(Encoding.UTF8.GetByteCount(message) > Limits["maxTextBytes"]) throw new ArgumentException("That message is too big to send.");
		return message;
	}

	public static string Hello(string token) => Build("hello", ("v", Version), ("token", token));
	public static string Pair(string code, string device) => Build("pair", ("v", Version), ("code", TidyCode(code)), ("device", Fit("device", device)));
	public static string Live() => Build("live");
	public static string Stop() => Build("stop");
	public static string Now(string title, string artist, bool playing) => Build("now", ("title", Fit("title", title)), ("artist", Fit("artist", artist)), ("playing", playing));
	public static string Jam(string url) => Build("jam", ("url", url));

	// A pairing code as someone typed it: upper case, without the spaces or dashes people add
	public static string TidyCode(string typed) => new string(typed.Where((c) => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

	// Whether it could be a pairing code at all: six letters or digits. Whether it's the right one is the bot's call.
	public static bool LooksLikeCode(string typed)
	{
		var code = TidyCode(typed);
		return code.Length == 6 && code.All((c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
	}

	// A message from the bot, or null if it isn't one the protocol lists with every field of the right kind. A field
	// the app doesn't know is ignored, so a bot that adds one doesn't break an app that hasn't caught up; a type it
	// doesn't know is refused.
	public static ServerMessage? ReadServer(string text)
	{
		try
		{
			using var document = JsonDocument.Parse(text);
			var root = document.RootElement;
			if(root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) return null;
			var type = typeElement.GetString()!;
			if(!Server.TryGetValue(type, out var shape)) return null;
			var fields = new Dictionary<string, object>();
			foreach(var (name, kind) in shape)
			{
				if(!root.TryGetProperty(name, out var value)) return null;
				switch(kind)
				{
					case "string" when value.ValueKind == JsonValueKind.String:
						fields[name] = value.GetString()!;
						break;
					case "number" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number):
						fields[name] = number;
						break;
					case "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
						fields[name] = value.GetBoolean();
						break;
					default:
						return null;
				}
			}
			return new ServerMessage(type, fields);
		}
		catch(JsonException)
		{
			return null;
		}
	}

	// A Spotify Jam link, the only kind the bot will post. Checked here too, so a wrong one is caught in the app
	// instead of being silently dropped by the bot.
	public static bool IsJamLink(string text)
	{
		if(!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url)) return false;
		string[] hosts = ["spotify.link", "open.spotify.com", "spotify.app.link"];
		return url.Scheme == Uri.UriSchemeHttps && hosts.Contains(url.Host.ToLowerInvariant()) && url.UserInfo == "" && text.Trim().Length <= LimitFor("url");
	}
}
