````markdown
```html
<!--
Client-side JS snippet for jellyfin-plugin-custom-javascript.
- Shows per-client Intiface settings when viewing media details (if a .funscript exists).
- Persists to localStorage.
- Calls server endpoint /TheHandy/IntifaceClientPing/{itemId} which returns X-Intiface-Request-Id header.
- The client should include X-Intiface-Request-Id header for subsequent playback-related requests (this snippet attempts to attach it by issuing the ping just before user clicks play).
Paste into jellyfin-plugin-custom-javascript plugin configuration.
-->
<script>
(function() {
  const KEY = 'intiface_client_settings_v1';

  function load() {
    try {
      return JSON.parse(localStorage.getItem(KEY) || '{}');
    } catch { return {}; }
  }
  function save(obj) {
    try {
      localStorage.setItem(KEY, JSON.stringify(obj));
    } catch (e) { console.warn(e); }
  }

  function createUi(existing) {
    const container = document.createElement('div');
    container.id = 'intiface-client-settings-ui';
    container.style.marginTop = '8px';
    container.style.padding = '8px';
    container.style.border = '1px solid rgba(255,255,255,0.06)';
    container.style.borderRadius = '6px';
    container.style.background = 'rgba(0,0,0,0.03)';

    const title = document.createElement('div');
    title.textContent = 'Intiface client (client-only)';
    title.style.fontWeight = '700';
    title.style.marginBottom = '6px';
    container.appendChild(title);

    function row(labelText, placeholder, name, type) {
      const r = document.createElement('div');
      r.style.marginBottom = '6px';
      const label = document.createElement('label');
      label.textContent = labelText;
      label.style.fontSize = '12px';
      label.style.display = 'block';
      const input = document.createElement('input');
      input.type = type || 'text';
      input.placeholder = placeholder || '';
      input.value = existing[name] || '';
      input.dataset.intiface = name;
      input.style.width = '100%';
      r.appendChild(label);
      r.appendChild(input);
      return r;
    }

    container.appendChild(row('WebSocket URL', 'ws://127.0.0.1:12345 or wss://host', 'ws'));
    container.appendChild(row('User (optional)', '', 'user'));
    container.appendChild(row('Password (optional)', '', 'pass', 'password'));

    const btn = document.createElement('button');
    btn.textContent = 'Save (client)';
    btn.className = 'btn btn-primary';
    btn.onclick = () => {
      const s = load();
      s.ws = container.querySelector('input[data-intiface="ws"]').value.trim();
      s.user = container.querySelector('input[data-intiface="user"]').value.trim();
      s.pass = container.querySelector('input[data-intiface="pass"]').value;
      save(s);
      alert('Saved locally to this client.');
    };
    container.appendChild(btn);
    return container;
  }

  // Attempt to insert UI into media details area when opened.
  function tryInject() {
    // Jellyfin's details area uses '.item-info' in many versions; fallback to other selectors if needed.
    const details = document.querySelector('.item-info') || document.querySelector('.details-content');
    if (!details) return;
    if (details.querySelector('#intiface-client-settings-ui')) return;
    const settings = load();

    // Simple heuristic: show UI when the item has a playable path (we assume .funscript exists in server storage).
    // For a more accurate check, you can query a server endpoint that checks for the presence of the .funscript file.
    const ui = createUi(settings);
    details.appendChild(ui);
  }

  // Call server ping to register headers for upcoming playback. Returns promise with requestId (or null).
  function serverPing(itemId) {
    const s = load();
    if (!s || !s.ws) return Promise.resolve(null);
    const url = '/TheHandy/IntifaceClientPing/' + itemId;
    return fetch(url, {
      method: 'POST',
      headers: {
        'X-Intiface-WS': s.ws,
        'X-Intiface-User': s.user || '',
        'X-Intiface-Pass': s.pass || ''
      }
    }).then(resp => {
      if (!resp.ok) return null;
      return resp.headers.get('X-Intiface-Request-Id');
    }).catch(() => null);
  }

  // Hook play button: best-effort. When play clicked we call serverPing and store requestId globally for subsequent playback calls.
  document.addEventListener('click', function(ev) {
    const path = ev?.target?.closest?.('button, a, div');
    if (!path) return;
    // Heuristic: play buttons have aria-label=Play or class contains 'play' or 'play-button'
    const target = ev.target;
    const isPlay = (target.getAttribute && target.getAttribute('aria-label') === 'Play') ||
                   (target.classList && (target.classList.contains('play-button') || target.classList.contains('play')));
    if (!isPlay) return;

    // find current item id
    const mediaItem = window.currentItem || null;
    if (!mediaItem || !mediaItem.Id) return;

    // call server ping, then store request id into future requests by adding header when plugin endpoints are invoked by player
    serverPing(mediaItem.Id).then(id => {
      if (id) {
        // store to window for later inclusion by other requests; the server plugin will look for X-Intiface-Request-Id header.
        window.__intiface_request_id = id;
      }
    });
  });

  // Periodic attempt to inject UI (covers various app navigation events)
  setInterval(tryInject, 700);

})();
</script>
```