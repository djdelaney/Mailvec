// Mailvec site: theme toggle, mobile menu, example questions, privacy diagram switch, install tabs, copy.
// Everything degrades to a readable static page without JS.
(function () {
  var root = document.documentElement;

  function store(key, value) { try { localStorage.setItem(key, value); } catch (e) {} }

  // Theme
  var themeBtn = document.getElementById('theme-toggle');
  function syncThemeLabel() {
    var dark = root.getAttribute('data-theme') === 'dark';
    themeBtn.setAttribute('aria-label', dark ? 'Switch to light theme' : 'Switch to dark theme');
  }
  if (themeBtn) {
    syncThemeLabel();
    themeBtn.addEventListener('click', function () {
      var next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      root.setAttribute('data-theme', next);
      store('mailvec-theme', next);
      syncThemeLabel();
    });
  }

  // Mobile menu
  var menuBtn = document.getElementById('menu-toggle');
  if (menuBtn) {
    var nav = menuBtn.closest('nav');
    menuBtn.addEventListener('click', function () {
      var open = nav.classList.toggle('open');
      menuBtn.setAttribute('aria-expanded', String(open));
    });
    nav.querySelectorAll('.navlinks a').forEach(function (a) {
      a.addEventListener('click', function () { nav.classList.remove('open'); menuBtn.setAttribute('aria-expanded', 'false'); });
    });
  }

  // Example questions
  var cards = document.querySelectorAll('.askcard');
  var q = document.getElementById('ask-q'), call = document.getElementById('ask-call'), result = document.getElementById('ask-result');
  cards.forEach(function (card) {
    card.addEventListener('click', function () {
      cards.forEach(function (c) { c.setAttribute('data-on', 'false'); c.setAttribute('aria-pressed', 'false'); });
      card.setAttribute('data-on', 'true'); card.setAttribute('aria-pressed', 'true');
      q.textContent = card.querySelector('span').textContent;
      call.textContent = card.getAttribute('data-call');
      result.textContent = card.getAttribute('data-result');
    });
  });

  // Privacy diagram: One Mac / Server
  var segs = document.querySelectorAll('[data-d4]');
  segs.forEach(function (btn) {
    btn.addEventListener('click', function () {
      var v = btn.getAttribute('data-d4');
      segs.forEach(function (b) { var on = b === btn; b.setAttribute('data-on', String(on)); b.setAttribute('aria-pressed', String(on)); });
      document.querySelectorAll('[data-variant]').forEach(function (el) { el.hidden = el.getAttribute('data-variant') !== v; });
    });
  });

  // Install tabs
  var tabs = Array.prototype.slice.call(document.querySelectorAll('[data-tab]'));
  function selectTab(tab) {
    tabs.forEach(function (t) {
      var on = t === tab;
      t.setAttribute('data-on', String(on)); t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
    });
    if (copyBtn) copyBtn.textContent = 'Copy';
  }
  tabs.forEach(function (t, i) {
    t.addEventListener('click', function () { selectTab(t); });
    t.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
        var n = tabs[(i + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length];
        selectTab(n); n.focus(); e.preventDefault();
      }
    });
  });

  // Copy: the visible command block, without trailing comments
  var copyBtn = document.getElementById('copy-cmds');
  if (copyBtn) {
    copyBtn.addEventListener('click', function () {
      var panel = document.querySelector('[role="tabpanel"]:not([hidden])');
      var text = panel.textContent.replace(/ +#.*$/gm, '');
      var done = function () { copyBtn.textContent = 'Copied'; setTimeout(function () { copyBtn.textContent = 'Copy'; }, 2000); };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done, function () { copyBtn.textContent = 'Select and copy'; });
      } else {
        copyBtn.textContent = 'Select and copy';
      }
    });
  }
})();
