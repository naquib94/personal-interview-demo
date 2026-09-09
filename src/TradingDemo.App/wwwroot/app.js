// =========================================================================================
// Trading Platform Demo - client
// =========================================================================================
// Two conventions in here exist specifically to make the application testable, and they are
// the contract between this app and the automation framework:
//
//   1. data-testid attributes.   Every element a test needs is addressed by a stable
//                                data-testid, never by CSS class or DOM position. Naming is
//                                <area>-<thing>-<kind>, e.g. login-username-input.
//
//   2. A single busy indicator.  #app-busy carries the .active class for the whole duration of
//                                any in-flight request. The framework waits on that one element
//                                instead of guessing at timings, which is what removes the need
//                                for hardcoded sleeps. See QaFramework.Web/Synchronisation.
//
// Both are cheap for a developer to provide and eliminate the largest single source of UI test
// flakiness. Agreeing them with the development team is a QA engineering activity, not an
// afterthought.
// =========================================================================================

const Api = (() => {
  const TOKEN_KEY = 'demo.token';
  const USER_KEY = 'demo.username';

  let inFlight = 0;

  function setBusy(busy) {
    inFlight += busy ? 1 : -1;
    const el = document.querySelector('[data-testid="app-busy"]');
    if (el) el.classList.toggle('active', inFlight > 0);
  }

  async function request(method, path, body) {
    setBusy(true);
    try {
      const headers = { 'Content-Type': 'application/json' };
      const token = sessionStorage.getItem(TOKEN_KEY);
      if (token) headers['Authorization'] = `Bearer ${token}`;

      const response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body)
      });

      const text = await response.text();
      const payload = text ? JSON.parse(text) : null;
      return { ok: response.ok, status: response.status, payload };
    } finally {
      setBusy(false);
    }
  }

  return {
    get: (path) => request('GET', path),
    post: (path, body) => request('POST', path, body),
    token: () => sessionStorage.getItem(TOKEN_KEY),
    username: () => sessionStorage.getItem(USER_KEY),
    signIn: (token, username) => {
      sessionStorage.setItem(TOKEN_KEY, token);
      sessionStorage.setItem(USER_KEY, username);
    },
    signOut: () => {
      sessionStorage.removeItem(TOKEN_KEY);
      sessionStorage.removeItem(USER_KEY);
    }
  };
})();

const Ui = {
  /** Renders the shared chrome. Keeping it in one place means the nav has one set of testids. */
  chrome(active) {
    const links = [
      ['account', 'Account', '/account.html'],
      ['market', 'Market', '/market.html'],
      ['new-order', 'New Order', '/order-new.html'],
      ['order-history', 'Order History', '/orders.html']
    ];

    document.body.insertAdjacentHTML('afterbegin', `
      <header class="app-bar">
        <div class="brand" data-testid="app-brand">Meridian Trading <small>demo</small></div>
        <span class="spinner" data-testid="app-busy" aria-live="polite" aria-label="Loading"></span>
        <nav class="app-nav">
          ${links.map(([id, label, href]) => `
            <a href="${href}" data-testid="nav-${id}-link"
               class="${id === active ? 'active' : ''}">${label}</a>`).join('')}
          <a href="#" data-testid="nav-sign-out-link">Sign out</a>
        </nav>
      </header>`);

    document.querySelector('[data-testid="nav-sign-out-link"]').addEventListener('click', (e) => {
      e.preventDefault();
      Api.signOut();
      window.location.href = '/index.html';
    });
  },

  /** Every page that needs a session calls this first. */
  requireSession() {
    if (!Api.token()) {
      window.location.href = '/index.html';
      return false;
    }
    return true;
  },

  /**
   * Renders the shared alert region. The testid is constant and the *kind* is carried in a
   * class, so a test can assert "an error is shown" and "the message says X" separately.
   */
  alert(container, kind, message, details) {
    const list = (details && details.length)
      ? `<ul>${details.map(d => `<li data-testid="alert-detail-item">${d.field}: ${d.reason}</li>`).join('')}</ul>`
      : '';

    container.innerHTML = `
      <div class="alert alert-${kind}" data-testid="alert-message" data-alert-kind="${kind}">
        <span data-testid="alert-text">${message}</span>${list}
      </div>`;
  },

  clearAlert(container) {
    container.innerHTML = '';
  },

  money(value, currency) {
    return `${Number(value).toFixed(2)} ${currency || ''}`.trim();
  }
};
