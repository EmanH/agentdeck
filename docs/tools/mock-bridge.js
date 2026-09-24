// Stand-in for AgentDeck's C# host, for docs screenshots: feeds the real UI sample data. ?scene=main|workflows|editor|dictations
(() => {
  const listeners = [];
  const scene = new URLSearchParams(location.search).get('scene') || 'main';
  const emit = data => listeners.forEach(fn => fn({ data }));
  window.chrome = {
    webview: {
      addEventListener: (type, fn) => { if (type === 'message') listeners.push(fn); },
      postMessage: m => { if (m.t === 'ready') setTimeout(start, 60); },
    },
  };

  const palette = ['#3b82f6', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#ec4899', '#14b8a6', '#f97316', '#eab308', '#64748b'];
  const projects = [
    { id: 'p1', name: 'AgentDeck', path: 'C:\\dev\\agentdeck', color: '#3b82f6', icon: 'rocket' },
    { id: 'p2', name: 'Website', path: 'C:\\dev\\website', color: '#22c55e', icon: 'globe-showing-americas' },
    { id: 'p3', name: 'Mobile App', path: 'C:\\dev\\mobile-app', color: '#f59e0b', icon: 'mobile-phone' },
    { id: 'p4', name: 'Data Pipeline', path: 'C:\\dev\\data-pipeline', color: '#a855f7', icon: 'bar-chart' },
    { id: 'p5', name: 'Docs', path: 'C:\\dev\\docs', color: '#ec4899', icon: 'books' },
  ];
  const sessions = [
    { id: 1, projectId: 'p1', agent: 'claude', label: 'Fix Login Bug', done: false },
    { id: 3, projectId: 'p1', agent: 'grok', label: 'API Tests', done: false },
    { id: 2, projectId: 'p1', agent: 'codex', label: 'Deploy Script', done: true },
    { id: 4, projectId: 'p2', agent: 'claude', label: 'Hero Section', done: true },
    { id: 5, projectId: 'p3', agent: 'codex', label: 'Push Alerts', done: false },
  ];
  const wf = (id, name, agent, color, icon, instructions, model, effort) =>
    ({ id, projectId: 'p1', name, agent, color, icon, instructions, model, effort });
  const workflows = [
    wf('w1', 'Review Open PRs', 'claude', '#a855f7', 'magnifying-glass-tilted-left',
      'Review every open pull request. For each one, summarise the change, flag risky code and missing tests, and leave a short verdict.', 'opus', 'high'),
    wf('w2', 'Update Deps', 'codex', '#14b8a6', 'package',
      'Update all dependencies to their latest compatible versions, run the full test suite, and fix anything that breaks.', 'gpt-6-astra', 'medium'),
    wf('w3', 'Ship It', 'grok', '#f97316', 'rocket',
      'Bump the version, write the changelog from merged PRs since the last tag, build the release and draft the GitHub release notes.', null, 'high'),
    wf('w4', 'Write Tests', 'claude', '#22c55e', 'test-tube',
      'Find the least-tested modules and add focused unit tests for their edge cases.', 'sonnet', null),
  ];

  const ESC = '\x1b[';
  const rgb = (r, g, b) => `${ESC}38;2;${r};${g};${b}m`;
  const R = `${ESC}0m`, B = `${ESC}1m`, DIM = `${ESC}2m`;
  const orange = rgb(215, 119, 87), grey = rgb(140, 140, 140), green = rgb(80, 200, 120), red = rgb(230, 90, 90), blue = rgb(110, 160, 255), white = rgb(235, 235, 235);
  const nl = s => s.replace(/\n/g, '\r\n');

  const W = 40; // inner width of the welcome box
  const row = (visible, styled) => `${orange}│${R} ${styled}${' '.repeat(W - 1 - visible.length)}${orange}│${R}\n`;
  const claudeOut = nl(
    `${orange}╭${'─'.repeat(W)}╮${R}\n` +
    row('✻ Welcome to Claude Code', `${orange}✻${R} ${B}Welcome to Claude Code${R}`) +
    row('  cwd: C:\\dev\\agentdeck', `  ${grey}cwd: C:\\dev\\agentdeck${R}`) +
    `${orange}╰${'─'.repeat(W)}╯${R}\n\n` +
    `${grey}>${R} The login form throws when the session cookie has expired.\n  Fix it and add a regression test.\n\n` +
    `${white}⏺${R} I'll check how the session cookie is validated.\n\n` +
    `${green}⏺${R} ${B}Read${R}(src/auth/session.ts)\n  ${grey}⎿  Read 84 lines${R}\n\n` +
    `${green}⏺${R} ${B}Update${R}(src/auth/session.ts)\n  ${grey}⎿  Updated with 6 additions and 2 removals${R}\n` +
    `      ${red}41 -   const session = decode(cookie)${R}\n` +
    `      ${green}41 +   const session = cookie ? tryDecode(cookie) : null${R}\n` +
    `      ${green}42 +   if (!session || session.expired) return redirectToLogin()${R}\n\n` +
    `${green}⏺${R} ${B}Bash${R}(npm test -- auth)\n  ${grey}⎿${R}  ${green}✓ 15 passed${R} ${grey}(1.8s)${R}\n\n` +
    `${white}⏺${R} Fixed ✅ Expired cookies now send users back to the login\n  page instead of throwing, with a regression test in\n  ${blue}src/auth/session.test.ts${R}.\n\n` +
    `${orange}✳${R} ${orange}Tidying up…${R} ${grey}(12s · esc to interrupt)${R}\n`);

  const grokOut = nl(
    `${grey}PS C:\\dev\\agentdeck>${R} grok "Run the API test suite and fix failures"\n\n` +
    `${B}Grok Build${R} ${grey}· grok-4.7-build-fast · high effort${R}\n\n` +
    `${blue}▸${R} Running ${B}npm run test:api${R}\n\n` +
    ` ${green}✓${R} GET /projects returns list ${grey}(38ms)${R}\n` +
    ` ${green}✓${R} POST /projects validates name ${grey}(21ms)${R}\n` +
    ` ${green}✓${R} DELETE /projects/:id cascades ${grey}(44ms)${R}\n` +
    ` ${red}✗${R} PATCH /sessions/:id rejects stale token\n` +
    `   ${red}Expected 401, received 500${R}\n\n` +
    `${blue}▸${R} Found it: token expiry check runs after the DB lookup.\n` +
    `${blue}▸${R} Moving the check up in ${B}api/sessions.ts${R}…\n\n` +
    ` ${green}✓${R} PATCH /sessions/:id rejects stale token ${grey}(19ms)${R}\n\n` +
    `${green}${B}All 42 API tests passing${R} ⚡\n`);

  function start() {
    emit({ t: 'state', projects, selected: 'p1', sessions, palette });
    emit({ t: 'workflows', items: workflows });
    emit({
      t: 'agentOptions', agents: {
        claude: { models: [{ id: 'fable', label: 'Fable' }, { id: 'opus', label: 'Opus' }, { id: 'sonnet', label: 'Sonnet' }], efforts: ['low', 'medium', 'high', 'xhigh', 'max'] },
        codex: { models: [{ id: 'gpt-6-astra', label: 'GPT-6-Astra', efforts: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultEffort: 'medium' }], efforts: ['low', 'medium', 'high', 'xhigh', 'max', 'ultra'], defaultModel: 'gpt-6-astra' },
        grok: { models: [{ id: 'grok-4.7', label: 'grok-4.7' }, { id: 'grok-4.7-build-fast', label: 'grok-4.7-build-fast' }], efforts: ['low', 'medium', 'high', 'xhigh'], defaultModel: 'grok-4.7-build-fast' },
      },
    });
    const now = Date.now();
    emit({
      t: 'transcripts', items: [
        { at: new Date(now - 40e3).toISOString(), text: 'Can you check why the build is failing on main? I think it\'s the tests.' },
        { at: new Date(now - 6 * 60e3).toISOString(), text: 'I want to add a settings page to the app. It should include:\n\n- A toggle for sound.\n- A way to change the hold time for closing terminals.\n- A way to change the deck brightness.' },
        { at: new Date(now - 52 * 60e3).toISOString(), text: 'Let\'s meet at the library at noon.' },
      ],
    });

    emit({ t: 'created', id: 1, projectId: 'p1', agent: 'claude', placement: 'tab' });
    emit({ t: 'created', id: 3, projectId: 'p1', agent: 'grok', placement: 'right', relativeTo: 1 });
    emit({ t: 'created', id: 2, projectId: 'p1', agent: 'codex', placement: 'tab' });
    emit({ t: 'created', id: 4, projectId: 'p2', agent: 'claude', placement: 'tab' });
    emit({ t: 'created', id: 5, projectId: 'p3', agent: 'codex', placement: 'tab' });

    setTimeout(() => {
      emit({ t: 'output', id: 1, data: claudeOut });
      emit({ t: 'output', id: 3, data: grokOut });
      emit({ t: 'activate', id: 1 });
      document.activeElement?.blur();
      setTimeout(() => {
        if (scene === 'workflows') showWorkflows();
        if (scene === 'dictations') showTranscripts();
        if (scene === 'models') {
          showWorkflowEditor(workflows[1], true); // Codex: per-model thinking levels
          document.activeElement?.blur();
        }
        if (scene === 'editor') {
          showWorkflowEditor(workflows[0], true);
          document.querySelector('#wf-icon .icon-current')?.click();
          document.querySelector('#wf-icon .icon-search')?.blur();
        }
      }, 300);
    }, 400);
  }
})();
