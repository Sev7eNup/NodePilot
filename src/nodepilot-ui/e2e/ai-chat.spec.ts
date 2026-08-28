import { test, expect, type Page } from '@playwright/test';
import { installDefaultMocks, MOCK_USER, capsJson, mockCaps } from './fixtures/mockApi';

/**
 * E2ETests.md — Global "AI Chat" knowledge assistant (/ai-chat). Read-only Q&A over NodePilot
 * docs, workflows/operations, source code, and database (text2sql). Covers SSE-streamed answers,
 * tool-call indicators, thread persistence, export to Markdown, regenerate, and role-gated
 * source badges (Source-Code requires Admin/Operator; DB/text2sql requires global Admin).
 *
 * Hermetic: every API call is mocked via `page.route`, including POST /api/ai/knowledge/ask,
 * whose SSE response is served as `text/event-stream` body frames. The SPA renders English under
 * Playwright. No SignalR, no canvas, no real LLM.
 *
 * `installDefaultMocks` installs a default capabilities object with `enabled: false`, so the
 * page's `caps && !caps.enabled` guard renders the disabled-state card. Tests that need the
 * composer install their own `mockCaps(page, capsJson({...}))`, which overrides the default;
 * only the disabled-state test relies on it.
 */

interface ChatDoneMeta {
  model: string;
  /** End-to-end wall clock, including prefill and tool calls. */
  durationMs: number;
  /** The pure generation window; the footer divides the token count by this for tok/s. */
  generationMs?: number | null;
  promptTokens?: number | null;
  completionTokens?: number | null;
}

// ---- Mock-factory helpers ---------------------------------------------------------------

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
  // Scope to #np-main-scroll: the sidebar nav also renders an <h1 title="AI Chat">, so an
  // unscoped getByRole(heading) matches two elements and trips strict mode.
  await expect(page.locator('#np-main-scroll').getByRole('heading', { name: /^AI Chat$/i }))
    .toBeVisible({ timeout: 20_000 });
}

test.describe('AI Knowledge Chat (/ai-chat)', () => {
  test.beforeEach(async ({ page }) => {
    await installDefaultMocks(page);
  });

  // 1. Loaded capabilities render four source badges plus the empty-state title.
  test('renders all four source badges and the empty-state title when capabilities are fully on', async ({ page }) => {
    await mockCaps(page, capsJson());
    await openChat(page);

    // Sources label plus all four badges (Docs / Workflows & operations / Source code / Database).
    // Scope to #np-main-scroll: the sidebar also has a "Database" nav link (visible to Admin), so
    // an unscoped /^Database$/i matches two elements and trips strict mode. The badges live in the
    // page content area, never in the sidebar.
    const main = page.locator('#np-main-scroll');
    await expect(main.getByText(/^Sources:$/i)).toBeVisible();
    await expect(main.getByText(/^Docs$/i)).toBeVisible();
    await expect(main.getByText(/^Workflows & operations$/i)).toBeVisible();
    await expect(main.getByText(/^Source code$/i)).toBeVisible();
    await expect(main.getByText(/^Database$/i)).toBeVisible();

    // Empty-state title (no messages yet).
    await expect(page.getByRole('heading', { name: /Ask NodePilot anything/i })).toBeVisible();

    // With db:true the operational starter prompts appear, not the docs-only lite set.
    await expect(main.getByRole('button', { name: /last 10 failed runs/i })).toBeVisible();
    await expect(main.getByRole('button', { name: /webhook trigger/i })).toHaveCount(0);
  });

  // 2. Sending appends both delta frames, shows the usage footer, and re-enables the composer.
  test('streams a two-delta answer and shows the usage footer, then re-enables the composer', async ({ page }) => {
    await mockCaps(page, capsJson());
    // The two delta frames assemble to "Hello World". The done frame carries tokens and a
    // generation window, which is what the usageTokensTps footer needs for its tok/s figure.
    await mockAsk(page, [
      deltaFrame('Hello '),
      deltaFrame('World'),
      doneFrame({ model: 'knowledge-model', durationMs: 12, generationMs: 4, promptTokens: 10, completionTokens: 20 }),
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

    // Once sending ends, the Stop button is gone and the Send button is back.
    await expect(page.getByTitle(/^Stop$/i)).toHaveCount(0);
    await expect(page.getByTitle(/^Send$/i)).toBeVisible();
  });

  // 3. tool_call and tool_result events render the tool name and the "checked" done label.
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

  // 4a. capabilities.enabled=false renders the disabled-state card and no composer.
  test('shows the disabled-state card and no composer when capabilities.enabled is false', async ({ page }) => {
    await mockCaps(page, capsJson({ enabled: false }));
    await openChat(page);

    await expect(page.getByText(/AI Chat is disabled/i)).toBeVisible();
    await expect(page.getByText(/administrator can enable it/i)).toBeVisible();
    // The composer must not mount in the disabled state.
    await expect(page.getByRole('textbox', { name: /Ask about NodePilot/i })).toHaveCount(0);
  });

  // 4b. A 503 from the ask endpoint shows an error alert with a Try-again button.
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

  // 5. Threads: create, switch back, and persist across a reload.
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
    // A new thread starts empty, so the previous reply is gone from view.
    await expect(page.getByRole('heading', { name: /Ask NodePilot anything/i })).toBeVisible();
    await expect(page.getByText(/Hi there/i)).toHaveCount(0);

    // Switching back to "Chat 1" brings the reply back.
    await page.getByRole('button', { name: /^Chats$/i }).click();
    await page.getByRole('button', { name: /^Chat 1$/i }).click();
    await expect(page.getByText(/Hi there/i)).toBeVisible();

    // On reload the store rehydrates from this tab's sessionStorage (key "nodepilot-aichat"), so
    // the active thread ("Chat 1") and its messages survive.
    await page.reload();
    await expect(page.locator('#np-main-scroll').getByRole('heading', { name: /^AI Chat$/i })).toBeVisible();
    await expect(page.getByText(/Hi there/i)).toBeVisible();
  });

  // 6. Export the current thread as a Markdown download.
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

  // 7. Regenerate re-sends the last user question.
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

    // Hover the last assistant bubble to reveal the actions row (opacity-0 until group-hover),
    // then click the regenerate button (title/aria-label = "Regenerate answer").
    await page.getByText(/Answer/i).hover();
    await page.getByTitle(/Regenerate answer/i).click();

    // The second ask request must carry the same question as the first.
    await expect.poll(() => askBodies.length).toBe(2);
    expect(askBodies[1].question).toBe('What is NodePilot?');
  });

  // 8. Viewer: Source-Code and DB badges hidden, Docs and Operational visible, composer usable.
  test('hides Source-Code and Database badges for Viewer role but keeps the composer working', async ({ page }) => {
    // Override /api/auth/me to Viewer (last-registered route wins over installDefaultMocks).
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, role: 'Viewer' }),
      }),
    );
    // The backend gates SourceCode by privilege and DB by global Admin, so a Viewer gets both
    // as false.
    await mockCaps(page, capsJson({ sourceCode: false, db: false }));
    await mockAsk(page, [
      deltaFrame('Viewer reply.'),
      doneFrame({ model: 'knowledge-model', durationMs: 6 }),
    ]);
    await openChat(page);

    // Docs and Workflows & operations badges are visible.
    await expect(page.getByText(/^Docs$/i)).toBeVisible();
    await expect(page.getByText(/^Workflows & operations$/i)).toBeVisible();
    // Source code and Database badges are absent.
    await expect(page.getByText(/^Source code$/i)).toHaveCount(0);
    await expect(page.getByText(/^Database$/i)).toHaveCount(0);

    // Without the DB source the starter prompts fall back to the lite set, so a Viewer never sees
    // ops questions that could only answer "database source is not available".
    const main = page.locator('#np-main-scroll');
    await expect(main.getByRole('button', { name: /webhook trigger/i })).toBeVisible();
    await expect(main.getByRole('button', { name: /last 10 failed runs/i })).toHaveCount(0);

    // The composer stays usable: a Viewer can still ask questions.
    await page.getByRole('textbox', { name: /Ask about NodePilot/i }).fill('What can I see?');
    await page.getByTitle(/^Send$/i).click();
    await expect(page.getByText(/Viewer reply/i)).toBeVisible();
  });

  // 9. Operator role: typed/source knowledge stays available; raw database tools do not.
  test('shows Source-Code but hides Database for Operator capabilities', async ({ page }) => {
    await page.route('**/api/auth/me', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ ...MOCK_USER, role: 'Operator' }),
      }),
    );
    await mockCaps(page, capsJson({ sourceCode: true, db: false }));
    await openChat(page);

    const main = page.locator('#np-main-scroll');
    await expect(main.getByText(/^Source code$/i)).toBeVisible();
    await expect(main.getByText(/^Database$/i)).toHaveCount(0);
    await expect(main.getByRole('button', { name: /webhook trigger/i })).toBeVisible();
    await expect(main.getByRole('button', { name: /last 10 failed runs/i })).toHaveCount(0);
  });
});
