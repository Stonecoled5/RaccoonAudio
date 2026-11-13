using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RaccoonAudio.Core;
using Xunit;

namespace RaccoonAudio.Tests;

// What a release on GitHub looks like to the app, for the tests to bend out of shape
static class FakeRelease
{
	public const string Page = "https://github.com/Stonecoled5/RaccoonAudio/releases/download/";
	public static string ZipAt(string tag) => Page + tag + "/" + UpdateSource.ZipName;
	public static readonly string SomeHash = new('a', 64);

	// The JSON GitHub answers with, with one zip. A null url or digest leaves that field out.
	public static string Json(string tag = "v0.1.1", string? url = "", object? size = null, string? digest = "", bool draft = false, bool prerelease = false, object[]? more = null)
	{
		var zip = new Dictionary<string, object?> { ["name"] = UpdateSource.ZipName, ["size"] = size ?? 1000, ["state"] = "uploaded" };
		if(url != null) zip["browser_download_url"] = url == "" ? ZipAt(tag) : url;
		if(digest != null) zip["digest"] = digest == "" ? "sha256:" + SomeHash : digest;
		var assets = new List<object> { zip };
		if(more != null) assets.AddRange(more);
		return JsonSerializer.Serialize(new Dictionary<string, object?> { ["tag_name"] = tag, ["name"] = "Raccoon Audio " + tag, ["draft"] = draft, ["prerelease"] = prerelease, ["body"] = "Notes anyone could have written", ["assets"] = assets });
	}

	public static object Checksum(string tag, string? url = null) => new Dictionary<string, object?> { ["name"] = UpdateSource.ChecksumName, ["size"] = 90, ["browser_download_url"] = url ?? Page + tag + "/" + UpdateSource.ChecksumName };

	// A zip like a release's: the app and its README. The "app" is whatever bytes the test gives it.
	public static byte[] Zip(byte[]? exe, string exeName = UpdateSource.ExeName)
	{
		using var buffer = new MemoryStream();
		using(var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			using(var readme = archive.CreateEntry("README.md").Open()) readme.Write(Encoding.UTF8.GetBytes("# Raccoon Audio"));
			if(exe != null)
			{
				using var app = archive.CreateEntry(exeName).Open();
				app.Write(exe);
			}
		}
		return buffer.ToArray();
	}

	public static byte[] Exe(string marker) => Encoding.UTF8.GetBytes("MZ" + marker);
	public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

// GitHub, for the tests: it answers the addresses it was given and writes down everything it was asked
sealed class FakeGitHub : HttpMessageHandler
{
	readonly Dictionary<string, Func<HttpResponseMessage>> answers = [];
	public List<HttpRequestMessage> Asked { get; } = [];
	public string[] AskedFor => Asked.Select((r) => r.RequestUri!.ToString()).ToArray();

	public void Answer(string url, Func<HttpResponseMessage> with) => answers[url] = with;
	public void Answer(string url, byte[] body) => Answer(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
	public void Answer(string url, string body) => Answer(url, Encoding.UTF8.GetBytes(body));
	public void SendOn(string url, string to) => Answer(url, () =>
	{
		var response = new HttpResponseMessage(HttpStatusCode.Found);
		response.Headers.TryAddWithoutValidation("Location", to);
		return response;
	});

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
	{
		Asked.Add(request);
		return Task.FromResult(answers.TryGetValue(request.RequestUri!.ToString(), out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound));
	}
}

// A download that sends a little and then nothing more, without ever ending
sealed class StalledStream(byte[] start) : Stream
{
	int sent;
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
	{
		if(sent < start.Length)
		{
			var count = Math.Min(buffer.Length, start.Length - sent);
			start.AsMemory(sent, count).CopyTo(buffer);
			sent += count;
			return count;
		}
		await Task.Delay(Timeout.Infinite, cancel);
		return 0;
	}
	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override void Flush() { }
	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// A download that arrives a byte at a time, with a pause before each
sealed class TricklingStream(byte[] all, TimeSpan pause) : Stream
{
	int sent;
	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
	{
		if(sent >= all.Length) return 0;
		await Task.Delay(pause, cancel);
		buffer.Span[0] = all[sent++];
		return 1;
	}
	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override void Flush() { }
	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class AppVersionTests
{
	[Theory]
	[InlineData("v0.1.1", 0, 1, 1)]
	[InlineData("0.1.1", 0, 1, 1)]
	[InlineData("v12.0.345", 12, 0, 345)]
	public void AReleaseTagIsThreeNumbers(string tag, int major, int minor, int patch)
	{
		Assert.Equal(new AppVersion(major, minor, patch), AppVersion.FromTag(tag));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("v")]
	[InlineData("v1")]
	[InlineData("v1.2")]
	[InlineData("v1.2.3.4")]
	[InlineData("v1.2.3-beta")]
	[InlineData("v1.2.3+build")]
	[InlineData("V1.2.3")]
	[InlineData("vv1.2.3")]
	[InlineData(" v1.2.3")]
	[InlineData("v1.2.3\n")]
	[InlineData("v1..3")]
	[InlineData("v1.2.x")]
	[InlineData("v-1.2.3")]
	[InlineData("v+1.2.3")]
	[InlineData("v1.2.3/../../evil")]
	[InlineData("v1.2.99999999999999999999")]
	[InlineData("v１.2.3")]
	[InlineData("latest")]
	public void AnythingElseIsNotAVersion(string? tag)
	{
		Assert.Null(AppVersion.FromTag(tag));
	}

	[Fact]
	public void VersionsCompareNumberByNumberNotAsText()
	{
		Assert.True(new AppVersion(0, 1, 10).NewerThan(new AppVersion(0, 1, 9)));
		Assert.True(new AppVersion(0, 2, 0).NewerThan(new AppVersion(0, 1, 99)));
		Assert.True(new AppVersion(1, 0, 0).NewerThan(new AppVersion(0, 99, 99)));
		Assert.False(new AppVersion(0, 1, 1).NewerThan(new AppVersion(0, 1, 1)));
		Assert.False(new AppVersion(0, 1, 0).NewerThan(new AppVersion(0, 1, 1)));
		Assert.Equal("0.1.1", new AppVersion(0, 1, 1).ToString());
	}
}

public class ReleaseTests
{
	static readonly AppVersion running = new(0, 1, 0);
	static Release? Read(string json) => Releases.Read(json, UpdateSource.GitHub, running);

	[Fact]
	public void ANewerReleaseIsOfferedWithItsZipSizeAndChecksum()
	{
		var release = Read(FakeRelease.Json("v0.1.1", size: 69654968))!;
		Assert.Equal(new AppVersion(0, 1, 1), release.Version);
		Assert.Equal("v0.1.1", release.Tag);
		Assert.Equal(FakeRelease.ZipAt("v0.1.1"), release.Zip.ToString());
		Assert.Equal(69654968, release.Size);
		Assert.Equal(FakeRelease.SomeHash, release.Sha256);
		Assert.Null(release.Sha256File);
	}

	[Theory]
	[InlineData("v0.1.0")]
	[InlineData("v0.0.9")]
	[InlineData("0.1.0")]
	public void TheSameOrAnOlderReleaseIsNothingToOffer(string tag)
	{
		Assert.Null(Read(FakeRelease.Json(tag)));
		// Even one the app wouldn't install: it's never going to, so there's nothing to complain about
		Assert.Null(Read(FakeRelease.Json(tag, url: "https://evil.example/app.zip", digest: null)));
	}

	[Theory]
	[InlineData("v0.1.1-beta")]
	[InlineData("newest")]
	[InlineData("v9")]
	[InlineData("")]
	public void AReleaseWithATagThatIsNotAVersionIsRefused(string tag)
	{
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(tag)));
	}

	[Fact]
	public void ADraftOrAPrereleaseIsRefused()
	{
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(draft: true)));
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(prerelease: true)));
	}

	[Theory]
	// Somewhere else entirely
	[InlineData("https://evil.example/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	// Looks like GitHub at a glance
	[InlineData("https://github.com.evil.example/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://github.com@evil.example/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://evil.example\\@github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	// GitHub, but someone else's repository
	[InlineData("https://github.com/Someone/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://github.com/Stonecoled5/Other/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://github.com/Someone/Else/../../Stonecoled5/RaccoonAudio/releases/download/v0.1.1/Other.zip")]
	// This repository, but not that release's file
	[InlineData("https://github.com/Stonecoled5/RaccoonAudio/releases/download/v0.0.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/Other.zip")]
	[InlineData("https://github.com/Stonecoled5/RaccoonAudio/raw/main/RaccoonAudio-win-x64.zip")]
	[InlineData("https://github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip?to=elsewhere")]
	// Not encrypted, an odd port, a name and password, a GitHub host that isn't where releases are
	[InlineData("http://github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://github.com:8443/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://someone:secret@github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("https://raw.githubusercontent.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	// Not an address for the web at all
	[InlineData("file:///C:/Windows/System32/calc.exe")]
	[InlineData("//github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip")]
	[InlineData("javascript:alert(1)")]
	[InlineData("not an address")]
	public void ADownloadAnywhereButThisAppsReleaseOnGitHubIsRefused(string url)
	{
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json("v0.1.1", url: url)));
	}

	[Fact]
	public void AnAddressThatWandersBackToTheRightPlaceIsTidiedBeforeItIsUsed()
	{
		// What's checked and what's fetched are the same tidied address, so there's no gap between the two
		var release = Read(FakeRelease.Json("v0.1.1", url: "https://GitHub.com/Someone/Else/../../Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip"))!;
		Assert.Equal(FakeRelease.ZipAt("v0.1.1"), release.Zip.AbsoluteUri);
	}

	[Fact]
	public void AReleaseWithoutItsDownloadAddressIsRefused()
	{
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(url: null)));
	}

	[Fact]
	public void AReleaseWithNoZipOrTwoIsRefused()
	{
		var none = JsonSerializer.Serialize(new { tag_name = "v0.1.1", assets = new object[] { new { name = "Source.zip", size = 5, browser_download_url = FakeRelease.Page + "v0.1.1/Source.zip" } } });
		Assert.Throws<UpdateException>(() => Read(none));
		Assert.Throws<UpdateException>(() => Read("{\"tag_name\":\"v0.1.1\"}"));
		Assert.Throws<UpdateException>(() => Read("{\"tag_name\":\"v0.1.1\",\"assets\":\"none\"}"));
		var twice = FakeRelease.Json(more: [new { name = UpdateSource.ZipName, size = 5, browser_download_url = FakeRelease.ZipAt("v0.1.1"), digest = "sha256:" + new string('b', 64) }]);
		Assert.Throws<UpdateException>(() => Read(twice));
	}

	[Theory]
	[InlineData(0L)]
	[InlineData(-5L)]
	[InlineData(Releases.MaxZipBytes + 1)]
	[InlineData("1000")]
	[InlineData(12.5)]
	public void ADownloadOfAnImpossibleSizeIsRefused(object size)
	{
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(size: size)));
	}

	[Theory]
	[InlineData("sha256:abc")]
	[InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
	[InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
	[InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
	[InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa extra")]
	[InlineData("")]
	public void AChecksumTheAppCannotCheckIsRefusedNotSkipped(string digest)
	{
		// "" would mean "use the usual one" to the helper, so that case is written out
		var json = digest == "" ? FakeRelease.Json(digest: null).Replace("\"state\"", "\"digest\":\"\",\"state\"") : FakeRelease.Json(digest: digest);
		Assert.Throws<UpdateException>(() => Read(json));
		// A broken checksum isn't a reason to fall back on the checksum file either
		var withFile = digest == "" ? json : FakeRelease.Json(digest: digest, more: [FakeRelease.Checksum("v0.1.1")]);
		Assert.Throws<UpdateException>(() => Read(withFile));
	}

	[Fact]
	public void AChecksumInUpperCaseIsTheSameChecksum()
	{
		Assert.Equal(FakeRelease.SomeHash, Read(FakeRelease.Json(digest: "sha256:" + FakeRelease.SomeHash.ToUpperInvariant()))!.Sha256);
	}

	[Fact]
	public void WithoutGitHubsChecksumTheReleasesOwnChecksumFileIsUsed()
	{
		var release = Read(FakeRelease.Json(digest: null, more: [FakeRelease.Checksum("v0.1.1")]))!;
		Assert.Null(release.Sha256);
		Assert.Equal(FakeRelease.Page + "v0.1.1/" + UpdateSource.ChecksumName, release.Sha256File!.ToString());
	}

	[Fact]
	public void AReleaseWithNoChecksumAtAllIsRefused()
	{
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(digest: null)));
		// A checksum file somewhere else is as good as none
		Assert.Throws<UpdateException>(() => Read(FakeRelease.Json(digest: null, more: [FakeRelease.Checksum("v0.1.1", "https://evil.example/" + UpdateSource.ChecksumName)])));
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("null")]
	[InlineData("\"v0.1.1\"")]
	[InlineData("{\"tag_name\":7}")]
	[InlineData("{\"tag_name\":[\"v0.1.1\"]}")]
	[InlineData("{\"tag_name\":\"v0.1.1\",\"assets\":[7,null,\"zip\",[]]}")]
	[InlineData("{\"tag_name\":\"v0.1.1\",\"assets\":[{\"name\":7}]}")]
	public void AnAnswerThatIsNotAReleaseIsRefusedWithoutBlowingUp(string json)
	{
		Assert.Throws<UpdateException>(() => Read(json));
	}

	[Fact]
	public void AnAnswerNestedAbsurdlyDeepIsRefusedWithoutBlowingUp()
	{
		Assert.Throws<UpdateException>(() => Read("{\"tag_name\":\"v0.1.1\",\"assets\":" + new string('[', 5000) + new string(']', 5000) + "}"));
	}

	[Theory]
	[InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  RaccoonAudio-win-x64.zip\n", true)]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
	[InlineData("\r\n aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa *file", true)]
	[InlineData("", false)]
	[InlineData("RaccoonAudio-win-x64.zip aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
	[InlineData("aaaa", false)]
	public void AChecksumFileIsAHashThenAFileName(string text, bool good)
	{
		Assert.Equal(good ? FakeRelease.SomeHash : null, Releases.Sha256In(text));
	}
}

public class UpdateSourceTests
{
	[Theory]
	[InlineData("https://api.github.com/repos/Stonecoled5/RaccoonAudio/releases/latest", true)]
	[InlineData("https://github.com/Stonecoled5/RaccoonAudio/releases/download/v0.1.1/RaccoonAudio-win-x64.zip", true)]
	[InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=3", true)]
	[InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2", true)]
	[InlineData("http://release-assets.githubusercontent.com/1", false)]
	[InlineData("https://release-assets.githubusercontent.com:444/1", false)]
	[InlineData("https://evilgithubusercontent.com/1", false)]
	[InlineData("https://githubusercontent.com.evil.example/1", false)]
	[InlineData("https://github.com.evil.example/1", false)]
	[InlineData("https://user@github.com/1", false)]
	[InlineData("https://evil.example/github.com", false)]
	[InlineData("http://127.0.0.1:8080/1", false)]
	[InlineData("ftp://github.com/1", false)]
	public void OnlyGitHubsOwnHostsAreEverFetchedFrom(string url, bool allowed)
	{
		Assert.Equal(allowed, UpdateSource.GitHub.MayFetch(new Uri(url)));
	}

	[Fact]
	public void TheOnlyAddressAskedForReleasesIsThisAppsOnGitHub()
	{
		Assert.Equal("https://api.github.com/repos/Stonecoled5/RaccoonAudio/releases/latest", UpdateSource.GitHub.Latest.ToString());
		Assert.False(UpdateSource.GitHub.IsTest);
	}

	[Theory]
	[InlineData("http://127.0.0.1:8123/latest", true)]
	[InlineData("http://localhost:8123/latest", true)]
	[InlineData("http://[::1]:8123/latest", true)]
	[InlineData("https://example.com/latest", false)]
	[InlineData("http://example.com/latest", false)]
	[InlineData("http://192.168.1.20:8123/latest", false)]
	[InlineData("http://127.0.0.1.evil.example/latest", false)]
	// A name that sounds like this computer but would have to be looked up
	[InlineData("http://localhost.evil.example:8123/latest", false)]
	[InlineData("http://user@127.0.0.1:8123/latest", false)]
	[InlineData("file:///C:/latest.json", false)]
	[InlineData("latest", false)]
	public void AStandInForGitHubHasToBeOnThisComputer(string address, bool allowed)
	{
		Assert.Equal(allowed, UpdateSource.ForTesting(address) != null);
	}

	[Fact]
	public void AStandInOnlyServesFromItself()
	{
		var source = UpdateSource.ForTesting("http://127.0.0.1:8123/latest")!;
		Assert.True(source.IsTest);
		Assert.True(source.MayFetch(new Uri("http://127.0.0.1:8123/app.zip")));
		Assert.True(source.IsAssetAddress(new Uri("http://127.0.0.1:8123/app.zip"), "v0.1.1", UpdateSource.ZipName));
		Assert.False(source.MayFetch(new Uri("http://127.0.0.1:9999/app.zip")));
		Assert.False(source.MayFetch(new Uri("http://localhost:8123/app.zip")));
		Assert.False(source.MayFetch(new Uri("https://github.com/app.zip")));
		Assert.False(source.MayFetch(new Uri("http://evil.example:8123/app.zip")));
		Assert.False(source.IsAssetAddress(new Uri("http://evil.example:8123/app.zip"), "v0.1.1", UpdateSource.ZipName));
	}
}

public class UpdaterTests : IDisposable
{
	static readonly AppVersion running = new(0, 1, 0);
	const string latest = "https://api.github.com/repos/Stonecoled5/RaccoonAudio/releases/latest";
	const string fileServer = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=3";
	readonly string folder = Path.Combine(Path.GetTempPath(), "RaccoonAudioTests-" + Guid.NewGuid().ToString("N"));
	readonly string exe;
	readonly FakeGitHub github = new();
	readonly Updater updater;
	static readonly byte[] oldApp = FakeRelease.Exe(" the old app");
	static readonly byte[] newApp = FakeRelease.Exe(" the new app, which is a little longer");

	public UpdaterTests()
	{
		Directory.CreateDirectory(folder);
		exe = Path.Combine(folder, UpdateSource.ExeName);
		File.WriteAllBytes(exe, oldApp);
		updater = new Updater(UpdateSource.GitHub, github);
	}

	public void Dispose()
	{
		updater.Dispose();
		Directory.Delete(folder, true);
	}

	// A release whose zip is these bytes, published the way GitHub does: the address on github.com sends the
	// download on to its file server
	Release Publish(byte[] zip, string? hash = null, long? size = null)
	{
		github.SendOn(FakeRelease.ZipAt("v0.1.1"), fileServer);
		github.Answer(fileServer, zip);
		return new Release(new AppVersion(0, 1, 1), "v0.1.1", new Uri(FakeRelease.ZipAt("v0.1.1")), size ?? zip.Length, hash ?? FakeRelease.Hash(zip), null);
	}

	// What pressing Update does up to the point of starting the new copy: fetch the new app, then swap it in
	async Task InstallAsync(Release release, Action<int>? progress = null, Updater? with = null)
	{
		var fresh = await (with ?? updater).FetchAsync(release, exe, progress);
		Assert.Equal(ExeSwap.FreshPath(exe), fresh);
		try
		{
			ExeSwap.Replace(exe, fresh);
		}
		finally
		{
			ExeSwap.Remove(fresh);
		}
	}

	// The old app is where it was, untouched, and nothing else is in the folder
	void AssertNothingChanged()
	{
		Assert.Equal(oldApp, File.ReadAllBytes(exe));
		Assert.Equal([exe], Directory.GetFiles(folder));
	}

	[Fact]
	public async Task LookingForAReleaseIsOneRequestThatSaysNothingAboutThePerson()
	{
		github.Answer(latest, FakeRelease.Json("v0.1.1"));
		var release = await updater.CheckAsync(running);
		Assert.Equal(new AppVersion(0, 1, 1), release!.Version);
		var request = Assert.Single(github.Asked);
		Assert.Equal(HttpMethod.Get, request.Method);
		Assert.Equal(latest, request.RequestUri!.ToString());
		Assert.Null(request.Content);
		// The app's name and what kind of answer it wants: nothing else, and no version, computer or account
		Assert.Equal(["Accept", "User-Agent"], request.Headers.Select((h) => h.Key).Order());
		Assert.Equal("RaccoonAudio", request.Headers.GetValues("User-Agent").Single());
	}

	[Fact]
	public async Task NothingNewerIsNoReleaseAndAFailedLookIsSaidInWords()
	{
		github.Answer(latest, FakeRelease.Json("v0.1.0"));
		Assert.Null(await updater.CheckAsync(running));
		github.Answer(latest, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
		await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(running));
		github.Answer(latest, () => new HttpResponseMessage(HttpStatusCode.Forbidden));
		Assert.Contains("hour", (await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(running))).Message);
		github.Answer(latest, () => throw new HttpRequestException("No such host is known."));
		await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(running));
		github.Answer(latest, new byte[Releases.MaxJsonBytes + 1]);
		await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(running));
	}

	[Fact]
	public async Task ALookThatIsSentSomewhereElseIsNotFollowed()
	{
		github.SendOn(latest, "https://evil.example/latest");
		github.Answer("https://evil.example/latest", FakeRelease.Json("v9.9.9"));
		await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(running));
		Assert.Equal([latest], github.AskedFor);
	}

	[Fact]
	public async Task AnUpdatePutsTheNewAppInPlaceAndKeepsTheOldOneAside()
	{
		var release = Publish(FakeRelease.Zip(newApp));
		var heard = new List<int>();
		await InstallAsync(release, heard.Add);
		Assert.Equal(newApp, File.ReadAllBytes(exe));
		Assert.Equal(oldApp, File.ReadAllBytes(ExeSwap.OldPath(exe)));
		Assert.Equal([exe, ExeSwap.OldPath(exe)], Directory.GetFiles(folder).Order());
		Assert.Equal([FakeRelease.ZipAt("v0.1.1"), fileServer], github.AskedFor);
		Assert.Equal(100, heard.Last());
		// And the new copy clears up after it
		Assert.True(ExeSwap.CleanUp(exe));
		Assert.Equal([exe], Directory.GetFiles(folder));
	}

	[Fact]
	public async Task FetchingTheNewAppLeavesTheRunningOneAlone()
	{
		var fresh = await updater.FetchAsync(Publish(FakeRelease.Zip(newApp)), exe);
		Assert.Equal(Path.Combine(folder, "RaccoonAudio.new.exe"), fresh);
		Assert.Equal(newApp, File.ReadAllBytes(fresh));
		Assert.Equal(oldApp, File.ReadAllBytes(exe));
		Assert.Equal([exe, fresh], Directory.GetFiles(folder).Order());
		// Given up on at that point, what's cleared away is the new one
		Assert.True(ExeSwap.CleanUp(exe));
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadThatDoesNotMatchItsChecksumIsThrownAway()
	{
		var release = Publish(FakeRelease.Zip(newApp), hash: FakeRelease.Hash(FakeRelease.Zip(FakeRelease.Exe(" what was really published"))));
		var failed = await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		Assert.Contains("checksum", failed.Message);
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadCutOffPartWayIsThrownAway()
	{
		var zip = FakeRelease.Zip(newApp);
		var release = Publish(zip);
		github.Answer(fileServer, zip[..(zip.Length / 2)]);
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadBiggerThanTheReleaseSaidIsThrownAway()
	{
		var zip = FakeRelease.Zip(newApp);
		var release = Publish(zip, size: zip.Length - 1);
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadThatStopsArrivingIsGivenUpOn()
	{
		var zip = FakeRelease.Zip(newApp);
		var release = Publish(zip);
		github.Answer(fileServer, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(zip[..10])) });
		updater.Patience = TimeSpan.FromMilliseconds(200);
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadThatOnlyTricklesInIsGivenUpOnToo()
	{
		// Each piece arrives well inside the wait for a piece, but the whole thing would take for ever
		var zip = FakeRelease.Zip(newApp);
		var release = Publish(zip);
		github.Answer(fileServer, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TricklingStream(zip, TimeSpan.FromMilliseconds(50))) });
		updater.Patience = TimeSpan.FromSeconds(20);
		updater.DownloadLimit = TimeSpan.FromMilliseconds(400);
		await Assert.ThrowsAsync<UpdateException>(() => updater.FetchAsync(release, exe));
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadThatFailsOrIsMissingLeavesTheOldApp()
	{
		var release = Publish(FakeRelease.Zip(newApp));
		github.Answer(fileServer, () => new HttpResponseMessage(HttpStatusCode.NotFound));
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		AssertNothingChanged();
		github.Answer(fileServer, () => throw new HttpRequestException("The connection was reset."));
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		AssertNothingChanged();
	}

	[Theory]
	[InlineData("https://evil.example/RaccoonAudio-win-x64.zip")]
	[InlineData("http://release-assets.githubusercontent.com/1")]
	[InlineData("https://github.com.evil.example/1")]
	[InlineData("file:///C:/Windows/System32/calc.exe")]
	public async Task ADownloadSentOnToSomewhereThatIsNotGitHubIsNeverFetched(string elsewhere)
	{
		var zip = FakeRelease.Zip(newApp);
		var release = Publish(zip);
		github.SendOn(FakeRelease.ZipAt("v0.1.1"), elsewhere);
		// Even though what's there would pass every other check
		github.Answer(elsewhere, zip);
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		Assert.Equal([FakeRelease.ZipAt("v0.1.1")], github.AskedFor);
		AssertNothingChanged();
	}

	[Fact]
	public async Task ADownloadSentRoundInCirclesIsGivenUpOn()
	{
		var release = Publish(FakeRelease.Zip(newApp));
		github.SendOn(fileServer, FakeRelease.ZipAt("v0.1.1"));
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		Assert.True(github.Asked.Count <= 5);
		AssertNothingChanged();
	}

	[Fact]
	public async Task AZipWithoutTheAppInItIsThrownAway()
	{
		// Each of these matches its checksum: it's what's inside that's wrong
		byte[][] zips =
		[
			FakeRelease.Zip(null),
			FakeRelease.Zip(newApp, "publish/RaccoonAudio.exe"),
			FakeRelease.Zip(newApp, "../RaccoonAudio.exe"),
			FakeRelease.Zip(newApp, "RaccoonAudio.exe.txt"),
			FakeRelease.Zip(Encoding.UTF8.GetBytes("#!/bin/sh not a Windows program")),
			FakeRelease.Zip([]),
			Encoding.UTF8.GetBytes("this is not a zip at all, just some text of about the right kind of length"),
		];
		foreach(var zip in zips)
		{
			await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(Publish(zip)));
			AssertNothingChanged();
		}
	}

	[Fact]
	public async Task TheReleasesOwnChecksumFileIsFetchedAndChecked()
	{
		var zip = FakeRelease.Zip(newApp);
		var checksumAt = FakeRelease.Page + "v0.1.1/" + UpdateSource.ChecksumName;
		var release = Publish(zip) with { Sha256 = null, Sha256File = new Uri(checksumAt) };

		github.Answer(checksumAt, new string('0', 64) + "  " + UpdateSource.ZipName + "\n");
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		AssertNothingChanged();

		github.Answer(checksumAt, "this isn't a checksum");
		github.Asked.Clear();
		await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(release));
		// It didn't go on to download something it had no way to check
		Assert.DoesNotContain(fileServer, github.AskedFor);
		AssertNothingChanged();

		github.Answer(checksumAt, FakeRelease.Hash(zip) + "  " + UpdateSource.ZipName + "\n");
		await InstallAsync(release);
		Assert.Equal(newApp, File.ReadAllBytes(exe));
	}

	[Fact]
	public async Task AnOldVersionLeftFromLastTimeIsClearedOutOfTheWay()
	{
		File.WriteAllBytes(ExeSwap.OldPath(exe), FakeRelease.Exe(" the one before last"));
		await InstallAsync(Publish(FakeRelease.Zip(newApp)));
		Assert.Equal(newApp, File.ReadAllBytes(exe));
		Assert.Equal(oldApp, File.ReadAllBytes(ExeSwap.OldPath(exe)));
	}

	[Fact]
	public async Task IfTheOldVersionCannotStepAsideNothingChanges()
	{
		// Something has the left-over file open, so it can't be removed to make room
		var leftOver = FakeRelease.Exe(" the one before last");
		File.WriteAllBytes(ExeSwap.OldPath(exe), leftOver);
		using(new FileStream(ExeSwap.OldPath(exe), FileMode.Open, FileAccess.Read, FileShare.None))
		{
			if(!OperatingSystem.IsWindows()) return;
			await Assert.ThrowsAsync<UpdateException>(() => InstallAsync(Publish(FakeRelease.Zip(newApp))));
		}
		Assert.Equal(oldApp, File.ReadAllBytes(exe));
		Assert.Equal(leftOver, File.ReadAllBytes(ExeSwap.OldPath(exe)));
		Assert.Equal([exe, ExeSwap.OldPath(exe)], Directory.GetFiles(folder).Order());
	}

	[Fact]
	public void IfTheNewAppCannotBePutInPlaceTheOldOneGoesBack()
	{
		Assert.Throws<UpdateException>(() => ExeSwap.Replace(exe, Path.Combine(folder, "there is no new app")));
		AssertNothingChanged();
	}

	[Fact]
	public async Task ANewVersionThatWillNotStartCanBePutBack()
	{
		await InstallAsync(Publish(FakeRelease.Zip(newApp)));
		Assert.True(ExeSwap.PutBack(exe));
		AssertNothingChanged();
		// Nothing to put back a second time
		Assert.False(ExeSwap.PutBack(exe));
		AssertNothingChanged();
	}

	[Fact]
	public async Task AStandInOnThisComputerIsNeverLeftForAnywhereElse()
	{
		var local = "http://127.0.0.1:8123/";
		var zip = FakeRelease.Zip(newApp);
		using var testing = new Updater(UpdateSource.ForTesting(local + "latest")!, github);
		github.Answer(local + "latest", FakeRelease.Json("v0.1.1", url: local + "app.zip", size: zip.Length, digest: "sha256:" + FakeRelease.Hash(zip)));
		github.Answer(local + "app.zip", zip);
		var release = await testing.CheckAsync(running);
		await InstallAsync(release!, with: testing);
		Assert.Equal(newApp, File.ReadAllBytes(exe));

		// A stand-in can't point the app at the real internet
		github.Answer(local + "latest", FakeRelease.Json("v0.1.2", url: "https://evil.example/app.zip"));
		await Assert.ThrowsAsync<UpdateException>(() => testing.CheckAsync(running));
		github.Answer(local + "latest", FakeRelease.Json("v0.1.2", url: FakeRelease.ZipAt("v0.1.2")));
		await Assert.ThrowsAsync<UpdateException>(() => testing.CheckAsync(running));
	}
}
