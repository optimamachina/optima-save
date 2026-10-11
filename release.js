// optima save · release helper (needs Node.js). Three steps, in order:
//
//   node release.js            build, then make releases\v<version>\ with
//                                Optima-Save-Setup.exe       the helper + installer
//                                latest.json + latest.json.sig  what the helper's updater reads (signed)
//                                optima-save-for-mozilla.xpi  upload this to addons.mozilla.org to be signed
//   node release.js signed <the signed .xpi from Mozilla>
//                              adds it as optima-save.xpi and lists it in updates.json (Firefox's updater)
//   node release.js publish    privacy scan, then fills ..\_publish with the repo files and the release files
//
// The update-signing key lives in %USERPROFILE%\.optima-signing\optima-save-update.pem, outside the repo
// and outside synced folders. Without it no update can be published; back it up somewhere safe and offline.
'use strict';
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const { execFileSync } = require('child_process');

const ROOT = __dirname;
const REPO = 'optimamachina/optima-save';
const KEY = path.join(process.env.USERPROFILE || '', '.optima-signing', 'optima-save-update.pem');
const PUBLISH = path.join(ROOT, '..', '_publish');
const CODE_DIR = path.join(PUBLISH, '5 optima-save - code (drag everything inside into the repo)');
const FILES_DIR = path.join(PUBLISH, '6 optima-save - release files');
// what goes into the public repo (build outputs, releases and local folders never do)
const REPO_FILES = ['README.md', 'AGENTS.md', 'LICENSE', '.gitignore', 'build.bat', 'release.js', 'release.bat', 'add signed xpi.bat',
  'optima-save.cs', 'trim.html', 'setup.html', 'chrome-manifest.json', 'updates.json', 'extension', 'icons', 'vendor'];

const sha256 = (file) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const die = (msg) => { console.error('\n' + msg + '\n'); process.exit(1); };

function version() {
  const cs = fs.readFileSync(path.join(ROOT, 'optima-save.cs'), 'utf8');
  const v = (/const string AppVersion = "([\d.]+)"/.exec(cs) || [])[1];
  const asm = (/AssemblyVersion\("([\d.]+)\.0"\)/.exec(cs) || [])[1];
  const ff = JSON.parse(fs.readFileSync(path.join(ROOT, 'extension', 'manifest.json'), 'utf8')).version;
  const cr = JSON.parse(fs.readFileSync(path.join(ROOT, 'chrome-manifest.json'), 'utf8')).version;
  if (!v || v !== asm || v !== ff || v !== cr) die(`versions don't match: helper ${v}, exe ${asm}, firefox ${ff}, chrome ${cr}. set all four to the same number.`);
  return v;
}

// Typed passphrases are never shown, logged or stored anywhere. Windows' own masked prompt (PowerShell's
// Read-Host -AsSecureString, a * per character) does the typing; the passphrase comes back over a pipe,
// never through the screen, a file or an environment variable.
function askSecret(question) {
  const ps = [
    '[Console]::OutputEncoding = [Text.Encoding]::UTF8',
    "$s = Read-Host -AsSecureString -Prompt '" + question.replace(/'/g, "''") + "'",
    '$b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)',
    'try { [Console]::Out.Write([Runtime.InteropServices.Marshal]::PtrToStringBSTR($b)) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }',
  ].join('; ');
  try {
    return Promise.resolve(execFileSync('powershell.exe', ['-NoProfile', '-Command', ps],
      { stdio: ['inherit', 'pipe', 'inherit'] }).toString('utf8'));
  } catch (e) {
    die('the passphrase prompt could not open (' + e.message + ').');
  }
}

// The key file on its own is useless to a thief: it is encrypted with a passphrase only you know.
// An unprotected key is never used to sign; the first run turns it into a protected one.
async function signingKey() {
  if (!fs.existsSync(KEY)) die('the update-signing key is missing: ' + KEY + '\nwithout it installed copies would refuse this update.');
  const pem = fs.readFileSync(KEY, 'utf8');
  if (!pem.includes('ENCRYPTED')) {
    console.log('\nthe update-signing key isn\'t protected by a passphrase yet. choose one now (12+ characters).');
    console.log('keep it in your password manager: without it no update can ever be signed.\n');
    let pass;
    for (;;) {
      pass = await askSecret('new passphrase');
      if (pass.length < 12) { console.log('too short, 12 characters or more.'); continue; }
      if ((await askSecret('same again')) !== pass) { console.log('they didn\'t match, try again.'); continue; }
      break;
    }
    const locked = crypto.createPrivateKey(pem).export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase: pass });
    fs.writeFileSync(KEY + '.new', locked, { mode: 0o600 });
    crypto.createPrivateKey({ key: locked, passphrase: pass }); // proves it opens before replacing the old file
    fs.renameSync(KEY + '.new', KEY);
    console.log('the key is now protected.\n');
    return crypto.createPrivateKey({ key: locked, passphrase: pass });
  }
  for (let tries = 0; tries < 3; tries++) {
    const pass = await askSecret('passphrase for the update key');
    try { return crypto.createPrivateKey({ key: pem, passphrase: pass }); } catch (e) { console.log('that isn\'t it.'); }
  }
  die('three wrong passphrases, stopped.');
}

async function prepare() {
  const v = version();
  const key = await signingKey();
  execFileSync('cmd.exe', ['/c', path.join(ROOT, 'build.bat')], { cwd: ROOT, stdio: 'inherit' });
  const out = path.join(ROOT, 'releases', 'v' + v);
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });
  const exe = path.join(out, 'Optima-Save-Setup.exe');
  fs.copyFileSync(path.join(ROOT, 'optima-save.exe'), exe);
  fs.copyFileSync(path.join(ROOT, 'optima-save.xpi'), path.join(out, 'optima-save-for-mozilla.xpi'));
  const latest = JSON.stringify({ version: v, url: `https://github.com/${REPO}/releases/download/v${v}/Optima-Save-Setup.exe`, sha256: sha256(exe) });
  fs.writeFileSync(path.join(out, 'latest.json'), latest);
  const signature = crypto.sign('sha256', Buffer.from(latest), key);
  if (!crypto.verify('sha256', Buffer.from(latest), crypto.createPublicKey(key), signature)) die('the signature didn\'t verify, stopped.');
  fs.writeFileSync(path.join(out, 'latest.json.sig'), signature.toString('base64'));
  console.log(`\nreleases\\v${v} is ready. next: addons.mozilla.org → My Add-ons → optima save → Upload New Version →\nupload optima-save-for-mozilla.xpi (On your own, source code: No), then drag the signed .xpi onto "add signed xpi.bat".`);
}

function signed(file) {
  const v = version();
  if (!file || !fs.existsSync(file)) die('drag the signed .xpi from Mozilla onto "add signed xpi.bat".');
  const out = path.join(ROOT, 'releases', 'v' + v);
  if (!fs.existsSync(out)) die('run release.bat first.');
  const xpi = path.join(out, 'optima-save.xpi');
  fs.copyFileSync(file, xpi);
  const updatesFile = path.join(ROOT, 'updates.json');
  const updates = fs.existsSync(updatesFile) ? JSON.parse(fs.readFileSync(updatesFile, 'utf8')) : { addons: {} };
  const entry = updates.addons['optima-save@optimamachina.world'] || (updates.addons['optima-save@optimamachina.world'] = { updates: [] });
  entry.updates = entry.updates.filter((u) => u.version !== v);
  entry.updates.push({ version: v, update_link: `https://github.com/${REPO}/releases/download/v${v}/optima-save.xpi`, update_hash: 'sha256:' + sha256(xpi) });
  fs.writeFileSync(updatesFile, JSON.stringify(updates, null, 2) + '\n');
  console.log(`added the signed add-on to releases\\v${v} and updates.json. next: node release.js publish (or release.bat publish).`);
}

// Nothing personal or secret may reach the public repo or the release: names, emails, paths, keys.
function scan(dir) {
  // Personal words (names, emails, the Windows username, local paths) come from a private list on this PC,
  // so the public repo never contains them; the generic secret shapes are built in pieces so this file
  // doesn't match itself.
  const words = path.join(process.env.USERPROFILE || '', '.optima-signing', 'private-words.txt');
  if (!fs.existsSync(words)) die('the private word list is missing: ' + words + ' (one word or email per line)');
  const literal = (w) => new RegExp(w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'i');
  const bad = fs.readFileSync(words, 'utf8').split(/\r?\n/).map((w) => w.trim()).filter(Boolean).map(literal)
    .concat([['sk', '-[A-Za-z0-9]{10}'], ['sk_', '(live|test)_'], ['ghp', '_[A-Za-z0-9]{20}'], ['sb_', 'secret'],
      ['service', '_role'], ['BEGIN [A-Z ]*PRIVATE', ' KEY']].map(([x, y]) => new RegExp(x + y)));
  const hits = [];
  const walk = (d) => {
    for (const f of fs.readdirSync(d)) {
      const p = path.join(d, f);
      if (fs.statSync(p).isDirectory()) { walk(p); continue; }
      const b = fs.readFileSync(p), text = b.toString('latin1') + '\n' + b.toString('utf16le');
      for (const re of bad) if (re.test(text)) hits.push(path.relative(dir, p) + ' matches ' + re);
    }
  };
  walk(dir);
  return hits;
}

function publish() {
  const v = version();
  const out = path.join(ROOT, 'releases', 'v' + v);
  for (const f of ['Optima-Save-Setup.exe', 'latest.json', 'latest.json.sig'])
    if (!fs.existsSync(path.join(out, f))) die(`releases\\v${v}\\${f} is missing. run release.bat first.`);
  const signedYet = fs.existsSync(path.join(out, 'optima-save.xpi'));
  fs.rmSync(CODE_DIR, { recursive: true, force: true });
  fs.rmSync(FILES_DIR, { recursive: true, force: true });
  fs.mkdirSync(CODE_DIR, { recursive: true });
  fs.mkdirSync(FILES_DIR, { recursive: true });
  for (const f of REPO_FILES) fs.cpSync(path.join(ROOT, f), path.join(CODE_DIR, f), { recursive: true });
  for (const f of ['Optima-Save-Setup.exe', 'latest.json', 'latest.json.sig']) fs.copyFileSync(path.join(out, f), path.join(FILES_DIR, f));
  // until Mozilla has signed the add-on, the folder holds the file to upload to them instead
  if (signedYet) fs.copyFileSync(path.join(out, 'optima-save.xpi'), path.join(FILES_DIR, 'optima-save.xpi'));
  else fs.copyFileSync(path.join(out, 'optima-save-for-mozilla.xpi'), path.join(FILES_DIR, 'optima-save-for-mozilla (upload to mozilla, not github).xpi'));
  const hits = scan(CODE_DIR).concat(scan(FILES_DIR).map((h) => 'release: ' + h));
  if (hits.length) {
    fs.rmSync(CODE_DIR, { recursive: true, force: true });
    fs.rmSync(FILES_DIR, { recursive: true, force: true });
    die('stopped: personal or secret data found, nothing was published:\n  ' + hits.join('\n  '));
  }
  console.log(`privacy scan clean. _publish now has:\n  5 optima-save - code (drag everything inside into the repo)\n  6 optima-save - release files  (tag v${v})` +
    (signedYet ? '' : '\n  (the add-on isn\'t signed yet: upload the for-mozilla file, then drag the signed one onto "add signed xpi.bat")'));
}

const [cmd, arg] = process.argv.slice(2);
if (!cmd) prepare().catch((e) => die(String(e && e.message || e)));
else if (cmd === 'signed') signed(arg);
else if (cmd === 'publish') publish();
else die('use: node release.js | node release.js signed <file.xpi> | node release.js publish');
