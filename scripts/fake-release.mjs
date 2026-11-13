// A stand-in for GitHub's releases on this computer, so an update can be tried from start to finish without
// publishing one. It answers the two things the app asks GitHub for: what the newest release is, and its zip.
//
//   node scripts/fake-release.mjs <RaccoonAudio-win-x64.zip> <version, like 0.1.1> [port] [how it goes]
//
// Then start an older build of the app pointed at it:
//
//   RaccoonAudio.exe --profile <folder> --update-from http://127.0.0.1:<port>/latest
//
// and press Update. (Add --update-test <report file> and it takes the update without being clicked, writes down what
// it told the person, and closes.) The app only accepts an address on this computer for --update-from.
//
// How it goes:
//   ok             the release is as it should be (the default)
//   checksum-file  the release has no checksum of GitHub's, only its own .sha256 file next to the zip
//   wrong-hash     the checksum published isn't the zip's, as if the download had been swapped or damaged
//   cut-off        the download stops half way
//   missing        the download isn't there
//   elsewhere      the download is sent on to another site, which the app should refuse to follow
//   no-checksum    the release publishes no checksum at all
//
// It prints every request with its headers, which is how to see exactly what the app sends.

import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';

const [zipPath, version, port = '8123', how = 'ok'] = process.argv.slice(2);
const ways = ['ok', 'checksum-file', 'wrong-hash', 'cut-off', 'missing', 'elsewhere', 'no-checksum'];
if(!zipPath || !version || !ways.includes(how)) {
	console.error('Usage: node scripts/fake-release.mjs <RaccoonAudio-win-x64.zip> <version, like 0.1.1> [port] [' + ways.join(' | ') + ']');
	process.exit(1);
}
const zip = fs.readFileSync(zipPath);
const real = crypto.createHash('sha256').update(zip).digest('hex');
const published = how === 'wrong-hash' ? crypto.createHash('sha256').update('something else').digest('hex') : real;
const here = 'http://127.0.0.1:' + port;
const name = 'RaccoonAudio-win-x64.zip';

// The same shape GitHub answers with, cut down to what matters
const release = {
	tag_name: 'v' + version,
	name: 'Raccoon Audio v' + version,
	draft: false,
	prerelease: false,
	body: 'A made-up release for testing.',
	assets: [{ name, size: zip.length, state: 'uploaded', browser_download_url: here + '/download/' + name }]
};
if(how === 'checksum-file') release.assets.push({ name: name + '.sha256', size: 90, state: 'uploaded', browser_download_url: here + '/download/' + name + '.sha256' });
else if(how !== 'no-checksum') release.assets[0].digest = 'sha256:' + published;

const server = http.createServer((request, response) => {
	console.log(request.method + ' ' + request.url + '  ' + JSON.stringify(request.headers));
	if(request.method !== 'GET') return response.writeHead(405).end();
	if(request.url === '/latest') return response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(release));
	if(request.url === '/download/' + name + '.sha256') return response.writeHead(200).end(published + '  ' + name + '\n');
	// Like GitHub, the address in the release sends the download on to where the file really is
	if(request.url === '/download/' + name) {
		if(how === 'missing') return response.writeHead(404).end('Not Found');
		return response.writeHead(302, { location: how === 'elsewhere' ? 'https://example.com/' + name : here + '/files/' + name }).end();
	}
	if(request.url === '/files/' + name) {
		response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': zip.length });
		if(how !== 'cut-off') return response.end(zip);
		response.write(zip.subarray(0, Math.floor(zip.length / 2)), () => request.socket.destroy());
		return;
	}
	response.writeHead(404).end('Not Found');
});
server.listen(Number(port), '127.0.0.1', () => {
	console.log('A made-up release of version ' + version + ' (' + how + ') at ' + here + '/latest');
	console.log('The zip is ' + zip.length + ' bytes, SHA-256 ' + real + (published === real ? '' : ', but the release says ' + published));
});
