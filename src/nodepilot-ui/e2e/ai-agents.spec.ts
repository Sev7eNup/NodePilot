import { test, expect } from '@playwright/test';
import { installDefaultMocks, MOCK_USER } from './fixtures/mockApi';

const workflowId = 'abababab-1111-2222-3333-444444444444';

test.describe('AI agent teams', () => {
  test.use({ viewport: { width: 1800, height: 1050 } });

  test('pins an MCP read approval and prevents selecting a writing tool', async ({ page }) => {
    await installDefaultMocks(page);
    const server = { id: workflowId, name: 'Reviewed MCP', enabled: true, transport: 'streamableHttp',
      endpoint: 'https://example.invalid/mcp', command: null, arguments: [], hasSecrets: true, updatedAt: '2026-09-19T12:00:00Z' };
    const contractSha256 = 'a'.repeat(64);
    let saved: Record<string, unknown> | null = null;
    await page.route('**/api/admin/settings/Agents', route => {
      if (route.request().method() === 'PUT') saved = route.request().postDataJSON();
      return route.fulfill({ json: { sectionPath: 'Agents', payload: { enabled: true, allowServiceIdentity: false,
        maxConcurrentRuns: 2, readOnlyMcpTools: saved?.ReadOnlyMcpTools ?? [] }, etag: '"v1"', effectiveSource: {}, isHotReloadable: false } });
    });
    await page.route('**/api/agents/mcp-servers', route => route.fulfill({ json: [server] }));
    await page.route(`**/api/agents/mcp-servers/${workflowId}/tools`, route => route.fulfill({ json: [
      { name: 'reader_tool', description: 'Reads status', schema: {}, readOnly: true, contractSha256 },
      { name: 'writer_tool', description: 'Changes status', schema: {}, readOnly: false, contractSha256: 'b'.repeat(64) },
    ] }));
    await page.goto('/settings?tab=system&section=agents');
    await page.getByRole('combobox', { name: /add tools from an MCP server|werkzeuge von einem MCP-server/i }).selectOption(workflowId);
    await expect(page.getByRole('checkbox', { name: /writer_tool/ })).toBeDisabled();
    await page.getByRole('checkbox', { name: /reader_tool/ }).check();
    await page.getByRole('button', { name: /^save$|^speichern$/i }).click();
    await expect.poll(() => saved?.ReadOnlyMcpTools).toEqual([{ serverId: workflowId, toolName: 'reader_tool', serverUpdatedAt: server.updatedAt, contractSha256 }]);
    await page.getByRole('button', { name: /^remove$|^entfernen$/i }).click();
    await page.getByRole('button', { name: /^save$|^speichern$/i }).click();
    await expect.poll(() => saved?.ReadOnlyMcpTools).toEqual([]);
  });

  test('registers a fixed MCP destination with masked write-only credentials', async ({ page }) => {
    await installDefaultMocks(page);
    await page.route('**/api/admin/settings/Agents', route => route.fulfill({ json: { sectionPath: 'Agents', payload: {
      enabled: true, allowServiceIdentity: false, maxConcurrentRuns: 2, singleModelCalls: 20, singleToolCalls: 40,
      singleTimeoutSeconds: 1200, teamModelCalls: 40, teamToolCalls: 80, teamDelegations: 20, teamTimeoutSeconds: 1800,
      maxContextCharacters: 128000, maxToolOutputCharacters: 16000, maxResultCharacters: 64000,
    }, etag: '"v1"', effectiveSource: {}, isHotReloadable: false } }));
    const servers: Record<string, unknown>[] = [];
    let submitted: Record<string, unknown> | null = null;
    await page.route('**/api/agents/mcp-servers', route => route.fulfill({ json: servers }));
    await page.route('**/api/agents/mcp-servers/*', route => {
      submitted = route.request().postDataJSON();
      const { secrets: _secrets, ...publicData } = submitted!;
      const result = { ...publicData, id: route.request().url().split('/').at(-1), hasSecrets: true, updatedAt: '2026-09-18T12:00:00Z' };
      servers.push(result);
      return route.fulfill({ json: result });
    });
    await page.goto('/settings?tab=system&section=agents');
    await page.getByRole('button', { name: /new MCP server|neuer MCP-server/i }).click();
    await page.getByLabel(/^name$/i).fill('Evidence source');
    await page.getByLabel(/^transport/i).selectOption('streamableHttp');
    await page.getByLabel(/Streamable HTTP endpoint|Streamable-HTTP-Endpunkt/i).fill('https://example.invalid/mcp');
    const secret = page.getByLabel(/secrets as JSON|geheimnisse als JSON/i);
    await expect(secret).toHaveAttribute('type', 'password');
    await secret.fill('{"Authorization":"Bearer test-only-secret"}');
    await page.locator('form').getByRole('button', { name: /^save$|^speichern$/i }).click();
    await expect.poll(() => submitted?.secrets).toEqual({ Authorization: 'Bearer test-only-secret' });
    await expect(page.locator('strong').filter({ hasText: 'Evidence source' })).toBeVisible();
    await page.getByRole('button', { name: /^edit$|^bearbeiten$/i }).click();
    await expect(secret).toHaveValue('');
    await page.screenshot({ path: 'test-results/ai-agent-settings.png', fullPage: true });
  });

  test('assigns the team lead separately, edits member tools and saves only the outer workflow step', async ({ page }) => {
    await installDefaultMocks(page);
    let definition = JSON.stringify({ nodes: [{ id: 'team', type: 'activity', position: { x: 50, y: 50 }, data: {
      label: 'Investigation', activityType: 'aiAgentTeam', config: { task: 'Investigate and review evidence.', members: [
        { id: 'lead', role: 'Coordinator', instructions: 'Delegate sequentially.', isSupervisor: true, tools: [], skillIds: [] },
        { id: 'researcher', role: 'Researcher', instructions: 'Collect evidence.', tools: [], skillIds: ['00000000-0000-0000-0000-000000000000'] },
      ] },
    } }], edges: [] });
    let saves = 0;
    await page.route(`**/api/workflows/${workflowId}`, route => {
      if (route.request().method() === 'PUT') { definition = route.request().postDataJSON().definitionJson; saves++; }
      return route.fulfill({ json: { id: workflowId, name: 'Agent team', description: '', isEnabled: false,
        checkedOutByUserId: MOCK_USER.id, checkedOutByUserName: MOCK_USER.username, version: 1, definitionJson: definition } });
    });
    await page.goto(`/workflows/${workflowId}`);
    const team = page.getByTestId('agent-team-node');
    await expect(team).toBeVisible();
    await team.locator('[data-agent-member-id="researcher"]').click();
    const missingSkill = page.getByText(/unresolved skill|skill nicht zugeordnet/i);
    await expect(missingSkill).toBeVisible();
    await missingSkill.locator('..').getByRole('button', { name: /^remove$|^entfernen$/i }).click();
    await expect(missingSkill).toHaveCount(0);
    const role = page.getByRole('textbox', { name: /^role$|^rolle$/i });
    await expect(role).toHaveValue('Researcher');
    await role.fill('Reviewer');
    const teamFunction = page.getByRole('combobox', { name: /team function|funktion im team/i });
    await teamFunction.selectOption('reviewer');
    await page.getByRole('checkbox', { name: /^PowerShell$/ }).check();
    await page.getByRole('checkbox', { name: /service identity|dienstidentität/i }).check();
    const teamLead = page.getByRole('combobox', { name: /team lead \(supervisor\)|teamleitung \(supervisor\)/i });
    const removeMember = page.getByRole('button', { name: /remove member from team|mitglied aus dem team entfernen/i });
    await expect(teamLead).toHaveValue('lead');
    await expect(page.getByRole('radio', { name: /^supervisor$/i })).toHaveCount(0);
    await teamLead.selectOption('researcher');
    await expect(teamFunction).toHaveValue('supervisor');
    await expect(teamFunction).toBeDisabled();
    await expect(role).toHaveValue('Reviewer');
    await expect(removeMember).toBeDisabled();
    await expect(page.getByText(/choose another member as team lead first|wähle zuerst ein anderes mitglied als teamleitung/i)).toBeVisible();
    await page.getByRole('tab', { name: 'Coordinator', exact: true }).click();
    await expect(role).toHaveValue('Coordinator');
    await teamFunction.selectOption('reviewer');
    await expect(page.getByRole('checkbox', { name: /^PowerShell$/ })).not.toBeChecked();
    await expect(removeMember).toBeDisabled();
    await expect(page.getByText(/a team needs at least two members|ein team benötigt mindestens zwei mitglieder/i)).toBeVisible();
    await page.getByRole('button', { name: /add member|mitglied hinzufügen/i }).click();
    await expect(role).toHaveValue(/Specialist 1|Spezialist 1/);
    await page.getByRole('button', { name: /add member|mitglied hinzufügen/i }).click();
    await expect(role).toHaveValue(/Specialist 2|Spezialist 2/);
    await expect(removeMember).toBeEnabled();
    await removeMember.click();
    await expect(role).toHaveValue('Reviewer');
    await page.getByRole('tab', { name: /^(Specialist|Spezialist) 1$/ }).click();
    await removeMember.click();
    await expect(role).toHaveValue('Reviewer');
    await page.getByRole('button', { name: /save in place|zwischen.?speichern|speichern|^save/i }).first().click();
    await expect.poll(() => saves).toBeGreaterThan(0);
    const saved = JSON.parse(definition);
    expect(saved.nodes).toHaveLength(1);
    expect(saved.edges).toHaveLength(0);
    expect(saved.nodes[0].data.config.members).toHaveLength(2);
    expect(saved.nodes[0].data.config.members[1]).toMatchObject({ id: 'researcher', role: 'Reviewer', isSupervisor: true, useServiceIdentity: true, tools: [{ name: 'powershell' }] });
    expect(saved.nodes[0].data.config.members.filter((member: { isSupervisor?: boolean }) => member.isSupervisor)).toHaveLength(1);
    expect(saved.nodes[0].data.config.members[0].isSupervisor).toBe(false);
    expect(saved.nodes[0].data.config.members[0].isReviewer).toBe(true);
    expect(saved.nodes[0].data.config.members[1].isReviewer).toBe(false);
    expect(saved.nodes[0].data.config.members[1].skillIds).toEqual([]);
    expect(saved.nodes[0].data.config.members[0].tools).toEqual([]);
    await page.reload();
    await expect(team.locator('[data-agent-member-id="researcher"]')).toContainText('Reviewer');
    await team.locator('[data-agent-member-id="researcher"]').click();
    await expect(role).toHaveValue('Reviewer');
    await expect(page.getByRole('checkbox', { name: /^PowerShell$/ })).toBeChecked();
    await expect(teamLead).toHaveValue('researcher');
    await expect(team.locator('[data-agent-member-id="lead"]')).toContainText('Reviewer');
    await teamLead.scrollIntoViewIfNeeded();
    await page.screenshot({ path: 'test-results/ai-agent-team.png', fullPage: true });
  });
});
