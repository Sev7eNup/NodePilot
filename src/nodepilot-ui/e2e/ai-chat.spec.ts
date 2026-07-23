import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

/**
 * E2ETests.md — Global "AI Chat" knowledge assistant (/ai-chat). Read-only Q&A over NodePilot
 * docs, workflows/operations, source code, and database (text2sql). SSE-streamed answers with
 * tool-call indicators, thread persistence, export-to-Markdown, regenerate, role-gated source
 * badges (Source-Code + DB require Admin/Operator via `User.IsPrivileged()`).
 *
 * Hermetic: every API call mocked via `page.route`, incl. POST /api/ai/knowledge/ask (SSE mocked
 * with `text/event-stream` body frames — same pattern as ai-assistant.spec.ts). SPA renders
 * ENGLISH under Playwright. No SignalR, no canvas, no real LLM.
 *
 * IMPORTANT: `installDefaultMocks`'s predicate catch-all returns `[]` (200) for any unmocked
 * /api/* endpoint. `/api/ai/knowledge/capabilities` is an OBJECT endpoint — `[]` parses to an
 * array, `caps.enabled` → undefined → the page's `caps && !caps.enabled` guard renders the
 * disabled-state card. So EVERY test must install its own capabilities mock (or the disabled
 * variant) — otherwise the composer never mounts.
 */

// ---- Types (frontend mirrors of backend DTOs, kept inline to avoid importing src/) --------

interface KnowledgeCapabilities {
  enabled: boolean;
  docs: boolean;
  operational: boolean;
  sourceCode: boolean;
  db: boolean;
}

interface ChatDoneMeta {
  model: string;
  durationMs: number;
  promptTokens?: number | null;
  completionTokens?: number | null;
}

// ---- Mock-factory helpers ---------------------------------------------------------------

/** Default caps: everything on (Admin/Operator view). */
function capsJson(overrides: Partial<KnowledgeCapabilities> = {}): KnowledgeCapabilities {
  return { enabled: true, docs: true, operational: true, sourceCode: true, db: true, ...overrides };
}

/** Mocks GET /api/ai/knowledge/capabilities with a JSON object. */
async function mockCaps(page: Page, caps: KnowledgeCapabilities) {
  await page.route('**/api/ai/knowledge/capabilities**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(caps) }),
  );
}

/** Mocks POST /api/ai/knowledge/ask with a concatenation of prebuilt SSE frames. */
async function mockAsk(page: Page, frames: string[]) {
  await page.route('**/api/ai/knowledge/ask**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/event-stream', body: frames.join('') }),
  );
}

// ---- SSE frame builders (event: <name>\ndata: <json>\n\n) --------------------------------

function deltaFrame(text: string): string {
  return `event: delta\ndata: ${JSON.stringify({ text })}\n\n`;
}
function toolCallFrame(toolName: string, toolId: string): string {
  return `event: tool_call\ndata: ${JSON.stringify({ toolName, toolId })}\n\n`;
}
function toolResultFrame(toolName: string, toolId: string): string {
  return `event: tool_result\ndata: ${JSON.stringify({ toolName, toolId })}\n\n`;
}
function doneFrame(meta: ChatDoneMeta): string {
  return `event: done\ndata: ${JSON.stringify(meta)}\n\n`;
}

/** Navigates to /ai-chat and waits for the page header to mount. */
async function openChat(page: Page) {
  await page.goto('/ai-chat');
  // Scope to #np-main-scroll: the sidebar nav ALSO renders an <h1 title="AI Chat"> — without the
  // scope, getByRole(heading) matches 2 elements (strict-mode violation).
  await expect(page.locator('#np-main-scroll').getByRole('heading', { name: /^AI Chat$/i }))
    .toBeVisible({ timeout: 20_000 });
}

test.describe('AI Knowledge Chat (/ai-chat)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  // 1. capabilities load → 4 source badges + empty-state title
  test('renders all four source badges and the empty-state title when capabilities are fully on', async ({ page }) => {
    await mockCaps(page, capsJson());
    await openChat(page);

    // Sources label + all four badges (Docs / Workflows & operations / Source code / Database).
    // Scope to #np-main-scroll: the sidebar ALSO has a "Database" nav link (visible to Admin), so
    // an unscoped /^Database$/i matches 2 elements (strict-mode violation). The badges live in the
    // page content area, never in the sidebar.
    const main = page.locator('#np-main-scroll');
    await expect(main.getByText(/^Sources:$/i)).toBeVisible();
    await expect(main.getByText(/^Docs$/i)).toBeVisible();
    await expect(main.getByText(/^Workflows & operations$/i)).toBeVisible();
    await expect(main.getByText(/^Source code$/i)).toBeVisible();
    await expect(main.getByText(/^Database$/i)).toBeVisible();

    // Empty-state title (no messages yet).
    await expect(page.getByRole('heading', { name: /Ask NodePilot anything/i })).toBeVisible();
  });

  // 2. composer send → SSE stream (two delta frames) → token append + final answer + usage footer + send re-enabled
  test('streams a two-delta answer and shows the usage footer, then re-enables the composer', async ({ page }) => {
    await mockCaps(page, capsJson());
    // Two delta frames → "Hello World"; done with tokens → usageTokensTps footer.
    await mockAsk(page, [
      deltaFrame('Hello '),
      deltaFrame('World'),
      doneFrame({ model: 'knowledge-model', durationMs: 12, promptTokens: 10, completionTokens: 20 }),
    ]);
    await openChat(page);

    const composer = page.getByRole('textbox', { name: /Ask about NodePilot/i });
    await composer.fill('What is NodePilot?');
    await page.getByTitle(/^Send$/i).click();

    // Final assembled answer (both deltas appended to the last assistant bubble).
    await expect(page.getByText(/Hello World/i)).toBeVisible();

    // Usage footer carries the model name. The footer lives in the actions row of the last
    // assistant bubble (opacity-0 until hover); hover the bubble to reveal it, then assert.
    await page.getByText(/Hello World/i).hover();
    await expect(page.getByText(/knowledge-model/i)).toBeVisible();

    // Sending ended: the Stop button is gone and the Send button is back.
    await expect(page.getByTitle(/^Stop$/i)).toHaveCount(0);
    await expect(page.getByTitle(/^Send$/i)).toBeVisible();
  });

  // 3. tool_call + tool_result events render (tool name code + "checked" done label)
  test('renders a tool-call indicator with the tool name and the checked label after tool_result', async ({ page }) => {
    await mockCaps(page, capsJson());
    await mockAsk(page, [
      toolCallFrame('list_db_tables', 'tool-1'),
      toolResultFrame('list_db_tables', 'tool-1'),
      deltaFrame('Found 3 tables.'),
      doneFrame({ model: 'knowledge-model', durationMs: 8 }),
    ]);
    await openChat(page);

    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('Which tables exist?');
    await page.getByTitle(/^Send$/i).click();

    // Tool name rendered inside a <code> element.
    await expect(page.getByText('list_db_tables', { exact: true })).toBeVisible();
    // Tool-done label (ai:chat.toolDone = "checked").
    await expect(page.getByText(/^checked$/i)).toBeVisible();
    // The prose answer still streams after the tool round-trip.
    await expect(page.getByText(/Found 3 tables/i)).toBeVisible();
  });

  // 4a. disabled-state card when capabilities.enabled=false (composer absent)
  test('shows the disabled-state card and no composer when capabilities.enabled is false', async ({ page }) => {
    await mockCaps(page, capsJson({ enabled: false }));
    await openChat(page);

    await expect(page.getByText(/AI Chat is disabled/i)).toBeVisible();
    await expect(page.getByText(/administrator can enable it/i)).toBeVisible();
    // Composer must NOT mount in the disabled state.
    await expect(page.getByRole('textbox', { name: /Ask about NodePilot/i })).toHaveCount(0);
  });

  // 4b. 503 on the ASK endpoint → error alert with Try-again button
  test('surfaces an error alert with a retry button when the ask endpoint returns 503', async ({ page }) => {
    await mockCaps(page, capsJson());
    await page.route('**/api/ai/knowledge/ask**', (route) =>
      route.fulfill({
        status: 503,
        contentType: 'application/json',
        body: JSON.stringify({ code: 'KNOWLEDGE_DISABLED', message: 'Der KI-Chat ist deaktiviert.' }),
      }),
    );
    await openChat(page);

    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('Hello?');
    await page.getByTitle(/^Send$/i).click();

    // errorPrefix = "AI error: {{message}}"; the alert has role="alert".
    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible();
    await expect(alert.getByText(/AI error:/i)).toBeVisible();
    // ai:chat.retry = "Try again".
    await expect(alert.getByRole('button', { name: /Try again/i })).toBeVisible();
  });

  // 5. thread create / switch / persistence across reload
  test('creates a new thread, switches back, and persists messages across a reload', async ({ page }) => {
    await mockCaps(page, capsJson());
    await mockAsk(page, [
      deltaFrame('Hi there.'),
      doneFrame({ model: 'knowledge-model', durationMs: 5 }),
    ]);
    await openChat(page);

    // Send a message in the default thread ("Chat 1").
    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('Hello');
    await page.getByTitle(/^Send$/i).click();
    await expect(page.getByText(/Hi there/i)).toBeVisible();

    // Open the thread menu (aria-label "Chats") and create a new thread.
    await page.getByRole('button', { name: /^Chats$/i }).click();
    await page.getByRole('button', { name: /New chat/i }).click();
    // New thread → empty state, the previous reply is gone from view.
    await expect(page.getByRole('heading', { name: /Ask NodePilot anything/i })).toBeVisible();
    await expect(page.getByText(/Hi there/i)).toHaveCount(0);

    // Switch back to "Chat 1" — the reply reappears.
    await page.getByRole('button', { name: /^Chats$/i }).click();
    await page.getByRole('button', { name: /^Chat 1$/i }).click();
    await expect(page.getByText(/Hi there/i)).toBeVisible();

    // Reload — the store rehydrates from localStorage (key "nodepilot-aichat"); the active
    // thread ("Chat 1") and its messages survive.
    await page.reload();
    await expect(page.locator('#np-main-scroll').getByRole('heading', { name: /^AI Chat$/i })).toBeVisible();
    await expect(page.getByText(/Hi there/i)).toBeVisible();
  });

  // 6. export-to-markdown download
  test('exports the current thread to a Markdown file download', async ({ page }) => {
    await mockCaps(page, capsJson());
    await mockAsk(page, [
      deltaFrame('Exported reply.'),
      doneFrame({ model: 'knowledge-model', durationMs: 4 }),
    ]);
    await openChat(page);

    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('Export me');
    await page.getByTitle(/^Send$/i).click();
    await expect(page.getByText(/Exported reply/i)).toBeVisible();

    // Export button (icon-only, title/aria-label = "Export as Markdown"). The download
    // filename pattern is `nodepilot-ai-chat-<slug>-<date>.md` (chatExport.ts).
    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.getByTitle(/Export as Markdown/i).click(),
    ]);
    expect(download.suggestedFilename()).toMatch(/nodepilot-ai-chat-.*\.md$/);
  });

  // 7. regenerate re-sends the last user question
  test('regenerate re-sends the last user question to the ask endpoint', async ({ page }) => {
    await mockCaps(page, capsJson());

    const askBodies: { question?: string }[] = [];
    await page.route('**/api/ai/knowledge/ask**', (route) => {
      askBodies.push(route.request().postDataJSON() as { question?: string });
      route.fulfill({
        status: 200,
        contentType: 'text/event-stream',
        body: deltaFrame('Answer.') + doneFrame({ model: 'knowledge-model', durationMs: 3 }),
      });
    });
    await openChat(page);

    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('What is NodePilot?');
    await page.getByTitle(/^Send$/i).click();
    await expect(page.getByText(/Answer/i)).toBeVisible();

    // Hover the last assistant bubble to reveal the actions row (opacity-0 → group-hover),
    // then click the regenerate button (title/aria-label = "Regenerate answer").
    await page.getByText(/Answer/i).hover();
    await page.getByTitle(/Regenerate answer/i).click();

    // The second ask request must carry the same question as the first.
    await expect.poll(() => askBodies.length).toBe(2);
    expect(askBodies[1].question).toBe('What is NodePilot?');
  });

  // 8. Viewer role: Source-Code + DB badges hidden; Docs + Operational visible; composer usable
  test('hides Source-Code and Database badges for Viewer role but keeps the composer working', async ({ page }) => {
    // Override /api/auth/me to Viewer (last-registered route wins over installDefaultMocks).
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }),
      }),
    );
    // Backend gates SourceCode/Db via User.IsPrivileged() (Admin/Operator only) → Viewer sees false.
    await mockCaps(page, capsJson({ sourceCode: false, db: false }));
    await mockAsk(page, [
      deltaFrame('Viewer reply.'),
      doneFrame({ model: 'knowledge-model', durationMs: 6 }),
    ]);
    await openChat(page);

    // Docs + Workflows & operations badges visible.
    await expect(page.getByText(/^Docs$/i)).toBeVisible();
    await expect(page.getByText(/^Workflows & operations$/i)).toBeVisible();
    // Source code + Database badges absent.
    await expect(page.getByText(/^Source code$/i)).toHaveCount(0);
    await expect(page.getByText(/^Database$/i)).toHaveCount(0);

    // Composer is usable — Viewer can still ask questions.
    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('What can I see?');
    await page.getByTitle(/^Send$/i).click();
    await expect(page.getByText(/Viewer reply/i)).toBeVisible();
  });
});