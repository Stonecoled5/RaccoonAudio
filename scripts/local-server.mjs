// Runs raccoon man's real stream server on this computer, with a stand-in for the part that plays in voice, so the
// app can be tried end to end without Discord. It prints the address and a pairing code, then what it hears.
//
//   node scripts/local-server.mjs <path to the RaccoonBot folder> [port]
//
// RaccoonBot has to be built first (npm run build in its folder). Nothing here touches Discord, Heroku or a database:
// paired computers are kept in memory and forgotten when this stops.

import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [bot, port = '8787'] = process.argv.slice(2);
if(!bot) {
	console.error('Usage: node scripts/local-server.mjs <path to the RaccoonBot folder> [port]');
	process.exit(1);
}
delete process.env.DATABASE_URL;
const load = (file) => import(pathToFileURL(path.join(bot, 'dist', file)).href);
const server = await load('stream/server.js');
const pairing = await load('stream/pairing.js');

var packets = 0;
var bytes = 0;
server.setStreamStation({
	goLive: async (session) => {
		console.log('LIVE from ' + session.device.name);
		// Read the way voice would: one packet every 20 ms
		const reader = setInterval(() => {
			if(!session.audio) return clearInterval(reader);
			for(let packet = session.audio.read(); packet; packet = session.audio.read()) {
				packets++;
				bytes += packet.length;
			}
		}, 20);
		return null;
	},
	ended: (_session, why) => console.log('ENDED (' + why + ') after ' + packets + ' packets, ' + bytes + ' bytes'),
	nowPlaying: (_session, now) => console.log('NOW ' + JSON.stringify(now)),
	jam: (_session, url) => console.log('JAM ' + url)
});

const web = server.startStreamServer(Number(port), { host: '127.0.0.1' });
await new Promise((resolve) => web.once('listening', resolve));
const code = pairing.newPairingCode('local-tester');
console.log('ADDRESS ws://127.0.0.1:' + web.address().port + '/stream');
console.log('CODE ' + code.code);
console.log('Press Ctrl+C to stop.');
