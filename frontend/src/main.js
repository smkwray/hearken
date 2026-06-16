import './style.css';
import { GetStatus, SetPeerIP, SetDirection, SetRole, SetVolume, ApplyParams, Toggle, Verify, SetAutoStart, DiscoverPeers } from '../wailsjs/go/main/App';

const app = document.querySelector('#app');
app.innerHTML = `
  <div class="panel">
    <header>
      <div class="title">hearken</div>
      <div class="route"><span id="self">Mac</span> ⇄ <span id="peer">Win</span><span class="rolebadge" id="rolebadge"></span><span class="ping" id="ping"></span></div>
    </header>

    <div class="selfip" id="selfip"></div>

    <div class="deps" id="deps" hidden></div>

    <div class="seg" id="role" title="Host listens for the peer; Client dials the host. Auto = host on macOS, client elsewhere.">
      <button data-role="">Auto</button>
      <button data-role="host">Host</button>
      <button data-role="client">Client</button>
    </div>

    <div class="iprow">
      <span class="lbl">Peer IP</span>
      <input id="ip" type="text" inputmode="decimal" placeholder="100.x.y.z (the other machine)" autocomplete="off" spellcheck="false"/>
      <button class="btn sm" id="scan" title="Find hearken hosts on your Tailscale">Scan</button>
      <button class="btn sm" id="ipsave">Save</button>
    </div>
    <div class="peers" id="peers" hidden></div>

    <button class="power" id="power"><span class="dot"></span><span id="powtxt">Start</span></button>

    <div class="seg" id="dir" title="Which machine's audio flows. Host = the listener; Client = the dialer.">
      <button data-dir="mac2win">Host→Client</button>
      <button data-dir="win2mac">Client→Host</button>
      <button data-dir="both">Both</button>
    </div>

    <label class="slider vol"><span class="lbl">Volume</span><input type="range" id="vol" min="0" max="100" step="5"><span class="val" id="vol-v"></span></label>

    <div class="pills">
      <div class="pill" id="p-bh"><i></i><span>BlackHole</span></div>
      <div class="pill" id="p-bo"><i></i><span>Bridge Out</span></div>
      <div class="pill" id="p-hear"><i></i><span>Hear</span></div>
      <div class="pill" id="p-talk"><i></i><span>Talk</span></div>
      <div class="pill wide" id="p-peer"><i></i><span>Peer</span></div>
    </div>

    <div class="section">
      <div class="sec-h">Latency <span class="hint">lower = less delay, may crackle</span></div>
      <label class="slider"><span class="lbl">Send buffer</span><input type="range" id="snd" min="4" max="128" step="4"><span class="val" id="snd-v"></span></label>
      <label class="slider"><span class="lbl">Capture</span><input type="range" id="cap" min="3" max="30" step="1"><span class="val" id="cap-v"></span></label>
      <label class="slider"><span class="lbl">Recv buffer</span><input type="range" id="recv" min="4" max="128" step="4"><span class="val" id="recv-v"></span></label>
    </div>

    <div class="actions">
      <button class="btn ghost" id="verify">Verify</button>
      <button class="btn" id="apply">Apply ⟳</button>
    </div>

    <label class="chk"><input type="checkbox" id="autostart"> Start bridge automatically on launch</label>
    <div class="statusline" id="msg">…</div>
  </div>
`;

const $ = (id) => document.getElementById(id);
const setPill = (el, ok, na) => { el.classList.toggle('ok', !!ok && !na); el.classList.toggle('na', !!na); el.classList.toggle('bad', !ok && !na); };
const msg = (t) => { $('msg').textContent = t; };
const sv = () => { $('snd-v').textContent = $('snd').value + ' KB'; $('cap-v').textContent = $('cap').value + ' ms'; $('recv-v').textContent = $('recv').value + ' KB'; };
['snd', 'cap', 'recv'].forEach((id) => $(id).addEventListener('input', sv));

let isWin = false, ipFocused = false, touched = false;
$('ip').addEventListener('focus', () => ipFocused = true);
$('ip').addEventListener('blur', () => ipFocused = false);
['snd', 'cap', 'recv'].forEach((id) => $(id).addEventListener('input', () => touched = true));

async function refresh() {
  let s; try { s = await GetStatus(); } catch (e) { msg('backend error: ' + e); return; }
  isWin = s.os === 'windows';
  $('self').textContent = s.self; $('peer').textContent = s.peer;
  $('ping').textContent = s.pingMs >= 0 ? ` · ${s.pingMs} ms` : '';
  if (!ipFocused && s.peerIP) $('ip').value = s.peerIP;

  // this device's own address(es) — read off onto the other machine
  const ips = [];
  if (s.selfTailscaleIP) ips.push('TS ' + s.selfTailscaleIP);
  if (s.selfLANIP) ips.push('LAN ' + s.selfLANIP);
  $('selfip').textContent = ips.length ? 'Others reach this device at:  ' + ips.join('   ·   ') : '';

  // deps banner
  if (s.missingDeps && s.missingDeps.length) {
    $('deps').hidden = false;
    $('deps').textContent = '⚠ Missing: ' + s.missingDeps.join(', ') + ' — run the installer for this machine.';
  } else $('deps').hidden = true;

  // power
  $('power').classList.toggle('on', s.active);
  $('powtxt').textContent = s.active ? 'Streaming — click to stop' : 'Start';

  setPill($('p-bh'), s.blackHole, isWin);
  setPill($('p-bo'), s.bridgeOut, isWin);
  setPill($('p-hear'), s.hearUp);
  setPill($('p-talk'), s.talkUp);
  setPill($('p-peer'), s.peerConnected);
  $('p-peer').querySelector('span').textContent = s.peerConnected ? 'Peer connected' : 'Peer';

  document.querySelectorAll('#dir button').forEach((b) => b.classList.toggle('active', b.dataset.dir === s.direction));
  document.querySelectorAll('#role button').forEach((b) => b.classList.toggle('active', b.dataset.role === (s.roleMode || '')));
  $('rolebadge').textContent = s.role ? ' · ' + s.role : '';

  $('recv').disabled = !isWin; $('snd').disabled = isWin; $('cap').disabled = isWin;
  $('snd').closest('.slider').classList.toggle('disabled', isWin);
  $('cap').closest('.slider').classList.toggle('disabled', isWin);
  $('recv').closest('.slider').classList.toggle('disabled', !isWin);
  $('autostart').checked = !!s.autoStart;

  if (!touched) { $('snd').value = s.sndBufKB; $('cap').value = s.captureMs; $('recv').value = s.recvBufKB; sv(); }
  if ($('vol') !== document.activeElement) { $('vol').value = s.volumePct; $('vol-v').textContent = s.volumePct + '%'; }
}

$('power').addEventListener('click', async () => { msg('…'); msg(await Toggle()); setTimeout(refresh, 700); });
$('ipsave').addEventListener('click', async () => { msg('saving IP…'); msg(await SetPeerIP($('ip').value)); ipFocused = false; setTimeout(refresh, 700); });
$('ip').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('ipsave').click(); });
$('scan').addEventListener('click', async () => {
  msg('scanning Tailscale…');
  let peers = [];
  try { peers = await DiscoverPeers(); } catch (e) { msg('scan failed: ' + e); return; }
  const box = $('peers');
  box.innerHTML = '';
  if (!peers || !peers.length) { box.hidden = true; msg('No hearken hosts found on your Tailscale.'); return; }
  peers.forEach((p) => {
    const b = document.createElement('button');
    b.className = 'peerchip';
    b.textContent = `${p.name || p.ip} · ${p.os || '?'} · ${p.ip}`;
    b.addEventListener('click', async () => {
      $('ip').value = p.ip;
      msg(await SetPeerIP(p.ip));
      box.hidden = true;
      setTimeout(refresh, 700);
    });
    box.appendChild(b);
  });
  box.hidden = false;
  msg(`Found ${peers.length} hearken host(s) — pick one.`);
});
document.querySelectorAll('#dir button').forEach((b) => b.addEventListener('click', async () => { msg('switching…'); msg(await SetDirection(b.dataset.dir)); setTimeout(refresh, 700); }));
document.querySelectorAll('#role button').forEach((b) => b.addEventListener('click', async () => { msg('switching mode…'); msg(await SetRole(b.dataset.role)); setTimeout(refresh, 700); }));
$('verify').addEventListener('click', async () => { msg('verifying…'); msg(await Verify()); });
$('apply').addEventListener('click', async () => { msg('applying…'); const r = await ApplyParams(parseInt($('snd').value), parseInt($('cap').value), parseInt($('recv').value)); touched = false; msg(r); setTimeout(refresh, 800); });
$('autostart').addEventListener('change', async () => { await SetAutoStart($('autostart').checked); });
$('vol').addEventListener('input', () => { $('vol-v').textContent = $('vol').value + '%'; });
$('vol').addEventListener('change', async () => { msg('volume ' + $('vol').value + '%…'); msg(await SetVolume(parseInt($('vol').value))); setTimeout(refresh, 700); });

sv(); refresh(); setInterval(refresh, 2500);
