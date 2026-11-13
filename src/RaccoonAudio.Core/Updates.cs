using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RaccoonAudio.Core;

// Updating the app: finding out there's a newer release, and, when the person asks for it, downloading it, checking
// it and swapping it in for the copy that's running.
//
// The rules it keeps:
// - Looking for a release is one request that says nothing about the person or the computer.
// - Everything a release says about itself is treated as something a stranger could have written. A release is only
//   used if its tag is a plain version newer than the running one, and its download is this app's zip, on this app's
//   GitHub page.
// - Nothing is fetched from anywhere but GitHub, including where GitHub sends a download on to.
// - A download is only used if its SHA-256 is the one the release publishes for it.
// - Whatever goes wrong, the copy that was running is still there, where it was, afterwards.
//
// What it can't do: the app isn't signed, so the checksum only shows the download is the file that was published. It
// says nothing about who published it. That rests on the GitHub account the releases come from.

// Something went wrong with an update. The message is whole sentences, for the person to read.
public sealed class UpdateException(string message) : Exception(message);

// A version of the app: three numbers, as in the release tag v0.1.1
public sealed record AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
	// A release tag, "v0.1.1" or "0.1.1", or null if it's anything else: a fourth number, a suffix like -beta, a
	// space, a sign. A tag the app doesn't understand is never a reason to update.
	public static AppVersion? FromTag(string? tag)
	{
		if(tag == null || tag.Length > 24) return null;
		var parts = (tag.StartsWith('v') ? tag[1..] : tag).Split('.');
		if(parts.Length != 3) return null;
		var numbers = new int[3];
		for(var i = 0; i < 3; i++)
		{
			if(parts[i].Length is 0 or > 6 || !parts[i].All((c) => c is >= '0' and <= '9')) return null;
			numbers[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
		}
		return new AppVersion(numbers[0], numbers[1], numbers[2]);
	}

	public int CompareTo(AppVersion? other)
	{
		if(other is null) return 1;
		if(Major != other.Major) return Major.CompareTo(other.Major);
		if(Minor != other.Minor) return Minor.CompareTo(other.Minor);
		return Patch.CompareTo(other.Patch);
	}

	public bool NewerThan(AppVersion other) => CompareTo(other) > 0;
	public override string ToString() => Major + "." + Minor + "." + Patch;
}

// A release that's newer than the running app and that the app is willing to install: where its zip is, how big it
// is, and the SHA-256 the release publishes for it (or, failing that, the file that holds it)
public sealed record Release(AppVersion Version, string Tag, Uri Zip, long Size, string? Sha256, Uri? Sha256File);

// Where releases come from. For everyone, that's this app's own page on GitHub, and it can't be changed by a setting:
// an address that could be changed would be a way to put someone else's program on a person's computer. The only
// other source there is points at a server on this same computer, for trying an update without publishing one.
public sealed class UpdateSource
{
	public const string ZipName = "RaccoonAudio-win-x64.zip";
	public const string ChecksumName = ZipName + ".sha256";
	public const string ExeName = "RaccoonAudio.exe";
	const string repository = "Stonecoled5/RaccoonAudio";

	// The one address asked for the newest release
	public Uri Latest { get; }
	// Set when this is a server on this computer, for testing
	readonly Uri? local;

	UpdateSource(Uri latest, Uri? local)
	{
		Latest = latest;
		this.local = local;
	}

	public static UpdateSource GitHub { get; } = new(new Uri("https://api.github.com/repos/" + repository + "/releases/latest"), null);

	// A stand-in for GitHub on this computer, or null if the address is anywhere else
	public static UpdateSource? ForTesting(string address)
	{
		if(!Uri.TryCreate(address, UriKind.Absolute, out var url)) return null;
		if(url.Scheme != Uri.UriSchemeHttp || !url.IsLoopback || url.UserInfo != "") return null;
		// By number, or by the one name that always means this computer: any other name would have to be looked up,
		// and could come back as somewhere else
		var numbered = url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(url.DnsSafeHost, out var number) && IPAddress.IsLoopback(number);
		if(!numbered && url.Host != "localhost") return null;
		return new UpdateSource(url, url);
	}

	public bool IsTest => local != null;

	// Whether a release's file is where this app's releases keep theirs: on GitHub, in this app's repository, under
	// that release's tag, with that name, and nothing else in the address
	public bool IsAssetAddress(Uri url, string tag, string name)
	{
		if(local != null) return OnTestServer(url);
		return Plain(url) && url.Host == "github.com" && url.Query == "" && url.Fragment == ""
			&& url.AbsolutePath == "/" + repository + "/releases/download/" + tag + "/" + name;
	}

	// Whether the app will fetch from this address at all. A download from GitHub is passed on to GitHub's file
	// servers; anywhere else it's passed on to is refused.
	public bool MayFetch(Uri url)
	{
		if(local != null) return OnTestServer(url);
		if(!Plain(url)) return false;
		return url.Host is "api.github.com" or "github.com" || url.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
	}

	// Encrypted, on the usual port, with no name and password tucked into the address
	static bool Plain(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo == "";

	bool OnTestServer(Uri url) => url.IsAbsoluteUri && url.Scheme == local!.Scheme && url.IsLoopback && url.Host == local.Host && url.Port == local.Port && url.UserInfo == "";
}

public static class Releases
{
	// More than the app will read or keep of each thing, whatever a release claims
	public const int MaxJsonBytes = 1 << 20;
	public const long MaxZipBytes = 400L << 20;
	public const long MaxExeBytes = 1L << 30;

	// What GitHub says the newest release is, as a release to offer: null if it isn't newer than the running app,
	// and an UpdateException if it's newer but not one the app will install.
	public static Release? Read(string json, UpdateSource source, AppVersion running)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
		}
		catch(JsonException)
		{
			throw new UpdateException("GitHub's answer about the newest version couldn't be read.");
		}
		using(document)
		{
			var root = document.RootElement;
			if(root.ValueKind != JsonValueKind.Object) throw new UpdateException("GitHub's answer about the newest version couldn't be read.");
			var tag = Text(root, "tag_name");
			var version = AppVersion.FromTag(tag) ?? throw new UpdateException("The newest release has a version the app doesn't understand.");
			if(!version.NewerThan(running)) return null;
			if(Flag(root, "draft") || Flag(root, "prerelease")) throw new UpdateException("The newest release isn't a finished one.");

			var files = root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
				? assets.EnumerateArray().Where((a) => a.ValueKind == JsonValueKind.Object).ToList() : [];
			var zips = files.Where((a) => Text(a, "name") == UpdateSource.ZipName).ToList();
			if(zips.Count != 1) throw new UpdateException("Version " + version + " is out, but its release doesn't have the app's download in it.");
			var zip = zips[0];
			var address = Address(zip, source, tag!, UpdateSource.ZipName);
			if(!zip.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > MaxZipBytes)
			{
				throw new UpdateException("Version " + version + " is out, but its download isn't a size the app will take.");
			}

			// The checksum GitHub works out for every file in a release. A release from before it did that can
			// publish its own, as a small file next to the zip.
			if(zip.TryGetProperty("digest", out var digest) && digest.ValueKind != JsonValueKind.Null)
			{
				var hash = digest.ValueKind == JsonValueKind.String && digest.GetString()!.StartsWith("sha256:", StringComparison.Ordinal) ? Sha256In(digest.GetString()![7..], whole: true) : null;
				if(hash == null) throw new UpdateException("Version " + version + " is out, but its checksum isn't one the app can check.");
				return new Release(version, tag!, address, size, hash, null);
			}
			var checksums = files.Where((a) => Text(a, "name") == UpdateSource.ChecksumName).ToList();
			if(checksums.Count != 1) throw new UpdateException("Version " + version + " is out, but its release doesn't publish a checksum, so the app won't install it by itself.");
			return new Release(version, tag!, address, size, null, Address(checksums[0], source, tag!, UpdateSource.ChecksumName));
		}
	}

	static Uri Address(JsonElement file, UpdateSource source, string tag, string name)
	{
		var text = Text(file, "browser_download_url");
		if(text == null || text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out var url) || !source.IsAssetAddress(url, tag, name))
		{
			throw new UpdateException("The newest release points somewhere other than this app's page on GitHub, so the app won't touch it.");
		}
		return url;
	}

	static string? Text(JsonElement from, string name) => from.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	static bool Flag(JsonElement from, string name) => from.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.False && value.ValueKind != JsonValueKind.Null;

	// A SHA-256 as 64 lower-case hex characters, or null. With whole, the text has to be exactly that; without, it's
	// the first word of the text, the way a .sha256 file is written ("<hash>  <file name>").
	public static string? Sha256In(string text, bool whole = false)
	{
		var word = whole ? text : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
		if(word.Length != 64 || !word.All((c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return null;
		return word.ToLowerInvariant();
	}
}

// Asks for the newest release, and installs one
public sealed class Updater : IDisposable
{
	readonly UpdateSource source;
	readonly HttpMessageInvoker http;
	// How long it waits for an answer, or for the next piece of a download, before giving up
	public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(30);
	// How long a whole download can take, however steadily it trickles in. Small things get two waits' worth.
	public TimeSpan DownloadLimit { get; set; } = TimeSpan.FromMinutes(30);
	// What it says it is to GitHub: the app's name and nothing else
	public const string UserAgent = "RaccoonAudio";

	public Updater(UpdateSource source, HttpMessageHandler? handler = null)
	{
		this.source = source;
		// No following where it's sent by itself (each place is checked first), no cookies, nothing remembered
		// A stand-in on this computer is talked to directly, never through a proxy that could answer for it.
		http = new HttpMessageInvoker(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = !source.IsTest, ConnectTimeout = TimeSpan.FromSeconds(15) });
	}

	public void Dispose() => http.Dispose();

	// The newest release if it's newer than the running app and one the app will install, or null if there's
	// nothing newer. One request. Throws an UpdateException if it couldn't find out.
	public async Task<Release?> CheckAsync(AppVersion running, CancellationToken stop = default)
	{
		var json = await Guarded(async (patience) =>
		{
			using var response = await GetAsync(source.Latest, "application/vnd.github+json", patience.Token);
			return Encoding.UTF8.GetString(await ReadAllAsync(response, Releases.MaxJsonBytes, patience));
		}, "Couldn't reach GitHub to look for a new version.", Patience * 2, stop);
		return Releases.Read(json, source, running);
	}

	// Downloads the release, checks it against its published checksum, and takes the app out of it. The new app is
	// left beside the one at exePath, and its path is what comes back; the one at exePath isn't touched. If it throws,
	// nothing is left behind. progress hears 0 to 100 as the download arrives.
	public async Task<string> FetchAsync(Release release, string exePath, Action<int>? progress = null, CancellationToken stop = default)
	{
		var zipPath = exePath + ".download";
		var freshPath = ExeSwap.FreshPath(exePath);
		var fetched = false;
		try
		{
			var expected = release.Sha256 ?? await PublishedHashAsync(release, stop);
			FileStream zip;
			try
			{
				zip = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			}
			catch(Exception err) when(err is UnauthorizedAccessException or IOException)
			{
				throw new UpdateException("The app can't write to the folder it's in (" + Path.GetDirectoryName(exePath) + "). Move it somewhere you can save files, like Documents, and try again.");
			}
			using(zip)
			{
				var actual = await Guarded((patience) => DownloadAsync(release, zip, progress, patience), "The download was cut off.", DownloadLimit, stop);
				if(actual != expected) throw new UpdateException("The download didn't match the checksum GitHub publishes for it, so it was thrown away.");
				zip.Position = 0;
				try
				{
					TakeExe(zip, freshPath);
				}
				catch(Exception err) when(err is IOException or UnauthorizedAccessException)
				{
					throw new UpdateException("Couldn't unpack the download: " + err.Message);
				}
			}
			fetched = true;
			return freshPath;
		}
		finally
		{
			ExeSwap.Remove(zipPath);
			if(!fetched) ExeSwap.Remove(freshPath);
		}
	}

	// The checksum from the small file a release publishes next to its zip
	async Task<string> PublishedHashAsync(Release release, CancellationToken stop)
	{
		var text = await Guarded(async (patience) =>
		{
			using var response = await GetAsync(release.Sha256File!, "application/octet-stream", patience.Token);
			return Encoding.UTF8.GetString(await ReadAllAsync(response, 1024, patience));
		}, "Couldn't fetch the checksum for the download.", Patience * 2, stop);
		return Releases.Sha256In(text) ?? throw new UpdateException("The release's checksum file isn't one the app can check.");
	}

	// Saves the zip, and gives back its SHA-256. Exactly as many bytes as the release said, or it fails.
	async Task<string> DownloadAsync(Release release, Stream to, Action<int>? progress, CancellationTokenSource patience)
	{
		using var response = await GetAsync(release.Zip, "application/octet-stream", patience.Token);
		using var from = await response.Content.ReadAsStreamAsync(patience.Token);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var buffer = new byte[81920];
		long got = 0;
		var said = -1;
		while(true)
		{
			patience.CancelAfter(Patience);
			var count = await from.ReadAsync(buffer, patience.Token);
			if(count == 0) break;
			got += count;
			if(got > release.Size) throw new UpdateException("The download was bigger than the release said it would be, so it was thrown away.");
			hash.AppendData(buffer, 0, count);
			try
			{
				await to.WriteAsync(buffer.AsMemory(0, count), patience.Token);
			}
			catch(IOException err)
			{
				// Not the network: the disk
				throw new UpdateException("Couldn't save the download next to the app. Is the disk full? " + err.Message);
			}
			var percent = (int)(got * 100 / release.Size);
			if(percent != said) progress?.Invoke(said = percent);
		}
		if(got != release.Size) throw new UpdateException("The download was cut off part way.");
		await to.FlushAsync(patience.Token);
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	// Takes the app out of a release's zip and writes it to freshPath. The zip has to hold exactly one
	// RaccoonAudio.exe, at its top, and it has to be a Windows program.
	public static void TakeExe(Stream zip, string freshPath)
	{
		try
		{
			using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
			var found = archive.Entries.Where((e) => string.Equals(e.FullName, UpdateSource.ExeName, StringComparison.OrdinalIgnoreCase)).ToList();
			if(found.Count != 1) throw new UpdateException("The download doesn't have the app in it.");
			var size = found[0].Length;
			if(size < 2 || size > Releases.MaxExeBytes) throw new UpdateException("The app in the download isn't a size the app will take.");
			using var from = found[0].Open();
			using var to = new FileStream(freshPath, FileMode.Create, FileAccess.Write, FileShare.None);
			var buffer = new byte[81920];
			long got = 0;
			int count;
			while((count = from.Read(buffer, 0, buffer.Length)) > 0)
			{
				// Every Windows program starts with these two letters
				if(got == 0 && (count < 2 || buffer[0] != 'M' || buffer[1] != 'Z')) throw new UpdateException("What's in the download isn't a Windows program.");
				got += count;
				// A zip can say one size and unpack to another: it gets as much room as it said and no more
				if(got > size) throw new UpdateException("The app in the download is bigger than it says it is, so it was thrown away.");
				to.Write(buffer, 0, count);
			}
			if(got != size) throw new UpdateException("The app in the download is damaged.");
		}
		catch(InvalidDataException)
		{
			throw new UpdateException("The download isn't a zip the app can open, or it's damaged.");
		}
	}

	// One GET, following where it's sent on at most a few times and only to places the source allows
	async Task<HttpResponseMessage> GetAsync(Uri url, string accept, CancellationToken stop)
	{
		for(var hop = 0; hop < 5; hop++)
		{
			if(!source.MayFetch(url)) throw new UpdateException("The download was sent on to somewhere that isn't GitHub (" + url.Host + "), so the app stopped.");
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
			request.Headers.TryAddWithoutValidation("Accept", accept);
			var response = await http.SendAsync(request, stop);
			if(response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
			{
				var next = response.Headers.Location;
				response.Dispose();
				if(next == null) throw new UpdateException("GitHub sent the download on without saying where.");
				url = next.IsAbsoluteUri ? next : new Uri(url, next);
				continue;
			}
			if(response.StatusCode != HttpStatusCode.OK)
			{
				var code = (int)response.StatusCode;
				response.Dispose();
				throw new UpdateException(code is 403 or 429 ? "GitHub is turning requests away for now (too many from this network). Try again in an hour." : "GitHub answered with an error (" + code + ").");
			}
			return response;
		}
		throw new UpdateException("The download was sent on too many times.");
	}

	async Task<byte[]> ReadAllAsync(HttpResponseMessage response, int most, CancellationTokenSource patience)
	{
		using var from = await response.Content.ReadAsStreamAsync(patience.Token);
		using var kept = new MemoryStream();
		var buffer = new byte[16384];
		while(true)
		{
			patience.CancelAfter(Patience);
			var count = await from.ReadAsync(buffer, patience.Token);
			if(count == 0) return kept.ToArray();
			if(kept.Length + count > most) throw new UpdateException("GitHub's answer was far bigger than it should be, so the app stopped reading it.");
			kept.Write(buffer, 0, count);
		}
	}

	// Runs something that talks to the network, with a limit on how long each wait can be and on how long the whole
	// thing can take, and turns the ways a network fails into words
	async Task<T> Guarded<T>(Func<CancellationTokenSource, Task<T>> work, string failed, TimeSpan limit, CancellationToken stop)
	{
		// The limit on the whole thing can't be pushed back by a download that sends a little every so often
		using var whole = new CancellationTokenSource(limit);
		using var patience = CancellationTokenSource.CreateLinkedTokenSource(stop, whole.Token);
		patience.CancelAfter(Patience);
		try
		{
			return await work(patience);
		}
		catch(OperationCanceledException) when(!stop.IsCancellationRequested)
		{
			throw new UpdateException(failed + " GitHub stopped answering.");
		}
		catch(Exception err) when(err is HttpRequestException or IOException)
		{
			throw new UpdateException(failed);
		}
	}
}

// Swapping the new app in for the running one. Windows won't let a running program's file be written over or
// deleted, but it will let it be renamed. So the running one is renamed aside, the new one takes its name, and the
// old file is deleted by the new copy once the old one has closed.
//
// One thing to know before calling Replace on the app that's running: the app is a single file that holds all its
// parts, and .NET fetches a part from that file, by the file's name, the first time the part is needed. Once the
// running app has been renamed aside, that name belongs to the new version, whose parts are in different places. So
// the running app has to have loaded everything it's still going to use before it steps aside (TrayApp does).
public static class ExeSwap
{
	public static string OldPath(string exePath) => exePath + ".old";
	// Where the new version waits, next to the app, until it's swapped in. It keeps an .exe ending so it can be
	// started there once, to see that it runs, before anything is swapped.
	public static string FreshPath(string exePath) => Path.ChangeExtension(exePath, ".new.exe");

	// The app at exePath steps aside to ".old" and the one at freshPath takes its place. If the new one can't be put
	// there, the old one is put back.
	public static void Replace(string exePath, string freshPath)
	{
		var old = OldPath(exePath);
		try
		{
			File.Delete(old);
		}
		catch(Exception err) when(err is IOException or UnauthorizedAccessException)
		{
			throw new UpdateException("A file left over from an earlier update (" + Path.GetFileName(old) + ") is in the way and can't be removed. Restart the computer and try again.");
		}
		try
		{
			File.Move(exePath, old);
		}
		catch(Exception err) when(err is IOException or UnauthorizedAccessException)
		{
			throw new UpdateException("Windows wouldn't let the app move itself aside to make room for the new version. " + err.Message);
		}
		try
		{
			File.Move(freshPath, exePath);
		}
		catch(Exception err) when(err is IOException or UnauthorizedAccessException)
		{
			File.Move(old, exePath);
			throw new UpdateException("Couldn't put the new version in place. " + err.Message);
		}
	}

	// Undoes Replace: the new app is moved out of the way and the old one goes back where it was. For when the new
	// one won't start. False if there was no old one to put back.
	public static bool PutBack(string exePath)
	{
		var old = OldPath(exePath);
		if(!File.Exists(old)) return false;
		var bad = exePath + ".bad";
		Remove(bad);
		if(File.Exists(exePath)) File.Move(exePath, bad);
		File.Move(old, exePath);
		Remove(bad);
		return true;
	}

	// Deletes what an update leaves beside the app: the old version, and anything half done. True if it's all gone.
	public static bool CleanUp(string exePath)
	{
		var gone = true;
		foreach(var left in new[] { OldPath(exePath), FreshPath(exePath), exePath + ".download", exePath + ".bad" }) gone &= Remove(left);
		return gone;
	}

	public static bool Remove(string path)
	{
		try
		{
			File.Delete(path);
			return true;
		}
		catch(Exception err) when(err is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
