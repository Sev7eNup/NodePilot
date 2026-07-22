import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { RunScriptConfig } from '../../../components/designer/properties/activities/RunScriptConfig';
import { SqlConfig } from '../../../components/designer/properties/activities/SqlConfig';
import { RestApiConfig } from '../../../components/designer/properties/activities/RestApiConfig';
import { WebhookTriggerConfig } from '../../../components/designer/properties/triggers/WebhookTriggerConfig';
import { ScheduleTriggerConfig } from '../../../components/designer/properties/triggers/ScheduleTriggerConfig';

/**
 * Property-Config tests — each Config receives `config` (current values) and `onUpdate`
 * (patch handler) plus optional `upstreamVars`. We assert two contracts per config:
 *
 *   1. Initial render reflects the values in `config` (no crash on empty input).
 *   2. User input fires `onUpdate` with the right shape — partial patches, NOT replacements.
 *
 * Critical: configs are called repeatedly by PropertiesPanel during editing, so a regression
 * that replaces the entire config object on every keystroke would be invisible to existing
 * snapshot tests but break the auto-save merge logic.
 */

// VariableInsertField → GlobalVariablePicker uses React Query for /global-variables. Stub
// fetch so the picker doesn't fan out to a non-existent backend during render.
beforeEach(() => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(
    new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
});

function wrap(ui: React.ReactElement) {
  // retry:false stops React Query from re-fetching on the inevitable miss in jsdom.
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe('RunScriptConfig', () => {
  it('rendersWithEmptyConfig_doesNotThrow', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    // Engine select defaults to "auto" — visible label as option text.
    expect(screen.getByText('Auto (PS7 → PS5.1)')).toBeInTheDocument();
  });

  it('engineSelect_changesEmitPartialPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ engine: 'auto' }} onUpdate={onUpdate} upstreamVars={[]} />);

    const select = screen.getByRole('combobox') as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'pwsh' } });

    expect(onUpdate).toHaveBeenCalledWith({ engine: 'pwsh' });
    // Critical: partial patch, not a full config replacement that would clobber `script`.
    expect(onUpdate).not.toHaveBeenCalledWith(expect.objectContaining({ script: expect.anything() }));
  });

  it('transcriptCheckbox_togglesPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ transcript: false }} onUpdate={onUpdate} upstreamVars={[]} />);

    const checkbox = screen.getByRole('checkbox', { name: 'Auto-Logging' }) as HTMLInputElement;
    expect(checkbox.checked).toBe(false);

    fireEvent.click(checkbox);

    expect(onUpdate).toHaveBeenCalledWith({ transcript: true });
  });

  it('isolatedCheckbox_togglesPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ isolated: false }} onUpdate={onUpdate} upstreamVars={[]} />);

    const checkbox = screen.getByRole('checkbox', { name: 'Process isolation' }) as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    expect(checkbox.disabled).toBe(false); // isLocalTarget defaults to true

    fireEvent.click(checkbox);

    expect(onUpdate).toHaveBeenCalledWith({ isolated: true });
  });

  it('isolatedCheckbox_disabledWhenRemoteTarget', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ isolated: false }} onUpdate={onUpdate} upstreamVars={[]} isLocalTarget={false} />);

    const checkbox = screen.getByRole('checkbox', { name: 'Process isolation' }) as HTMLInputElement;
    expect(checkbox.disabled).toBe(true);
    // Remote-only hint is shown.
    expect(screen.getByText(/only applies to local execution/i)).toBeInTheDocument();
  });

  it('memoryLimitField_emitsPatch_whenIsolatedAndLocal', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ isolated: true }} onUpdate={onUpdate} upstreamVars={[]} />);

    const spinButtons = screen.getAllByRole('spinbutton') as HTMLInputElement[];
    expect(spinButtons).toHaveLength(2); // memory + max processes only render when isolated & local

    fireEvent.change(spinButtons[0], { target: { value: '512' } });
    expect(onUpdate).toHaveBeenCalledWith({ memoryLimitMb: 512 });
  });

  it('capFields_hiddenWhenNotIsolated', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ isolated: false }} onUpdate={onUpdate} upstreamVars={[]} />);

    expect(screen.queryByRole('spinbutton')).toBeNull();
  });

  it('memoryLimitField_zeroRemovesKey', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ isolated: true, memoryLimitMb: 512 }} onUpdate={onUpdate} upstreamVars={[]} />);

    const spin = screen.getAllByRole('spinbutton')[0] as HTMLInputElement;
    fireEvent.change(spin, { target: { value: '0' } });

    // 0 / cleared must drop the key (unbounded) — never send 0 to the backend as a real cap.
    expect(onUpdate).toHaveBeenCalledWith({ memoryLimitMb: undefined });
  });

  it('isolatedCheckbox_preservesCheckedStateWhenRemote', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ isolated: true }} onUpdate={onUpdate} upstreamVars={[]} isLocalTarget={false} />);

    const checkbox = screen.getByRole('checkbox', { name: 'Process isolation' }) as HTMLInputElement;
    expect(checkbox.checked).toBe(true);  // stored config value preserved
    expect(checkbox.disabled).toBe(true); // but not editable on a remote target
  });

  it('successExitCodes_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const input = screen.getByPlaceholderText(/error-based/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: '0,1' } });

    expect(onUpdate).toHaveBeenCalledWith({ successExitCodes: '0,1' });
  });

  it('successExitCodes_emptyRemovesKey', () => {
    const onUpdate = vi.fn();
    wrap(<RunScriptConfig config={{ successExitCodes: '0' }} onUpdate={onUpdate} upstreamVars={[]} />);

    const input = screen.getByPlaceholderText(/error-based/i) as HTMLInputElement;
    expect(input.value).toBe('0');
    fireEvent.change(input, { target: { value: '' } });

    // Empty must drop the key (pure error-based), never send "" as a gate.
    expect(onUpdate).toHaveBeenCalledWith({ successExitCodes: undefined });
  });
});

describe('SqlConfig', () => {
  it('providerDefault_isSqlServer', () => {
    wrap(<SqlConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    // The first <select> is the provider; "SQL Server" is its visible label.
    expect(screen.getByDisplayValue('SQL Server')).toBeInTheDocument();
  });

  it('providerChange_emitsPatch_clearsOldBuilderFields', () => {
    const onUpdate = vi.fn();
    wrap(<SqlConfig config={{ provider: 'sqlserver', server: 'db01', database: 'App' }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('SQL Server'), { target: { value: 'sqlite' } });
    // Provider patch must drop the old provider's builder fields — otherwise stale `server`
    // would survive next to the new sqlite `dataSource` and the backend resolver would pick
    // the wrong path.
    expect(onUpdate).toHaveBeenCalledWith(expect.objectContaining({
      provider: 'sqlite',
      server: undefined,
      database: undefined,
    }));
  });

  it('rawMode_placeholderReflectsProvider', () => {
    // Raw connection-string mode must show a per-provider example so operators get a quick
    // reminder of the syntax shape. The Builder is the default — the test pins the raw
    // mode is reachable via the connectionMode marker.
    wrap(<SqlConfig config={{ provider: 'sqlite', connectionMode: 'raw' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText(/Data Source=.*sqlite/)).toBeInTheDocument();
  });

  it('rawMode_connectionStringInput_emitsPatchOnChange', () => {
    const onUpdate = vi.fn();
    wrap(<SqlConfig config={{ connectionMode: 'raw' }} onUpdate={onUpdate} upstreamVars={[]} />);

    const cs = screen.getByPlaceholderText(/Server=/i) as HTMLInputElement;
    fireEvent.change(cs, { target: { value: 'Server=db01;Database=test' } });

    expect(onUpdate).toHaveBeenCalledWith({ connectionString: 'Server=db01;Database=test' });
  });

  it('builderMode_isDefaultForFreshConfig_andRendersServerField', () => {
    // Fresh nodes default to Builder mode. The Server field carries a recognisable instance
    // example as placeholder — pinning it stops a regression where Builder silently goes
    // back to a single Connection-String textarea.
    wrap(<SqlConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText(/db01\\\\SQLEXPRESS/)).toBeInTheDocument();
  });

  it('builderMode_serverInput_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<SqlConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const server = screen.getByPlaceholderText(/db01\\\\SQLEXPRESS/) as HTMLInputElement;
    fireEvent.change(server, { target: { value: 'prod-sql01' } });

    expect(onUpdate).toHaveBeenCalledWith({ server: 'prod-sql01' });
  });

  it('builderMode_sqlAuth_revealsUsernameAndPasswordFields', () => {
    // Integrated auth (default) hides credentials. Switching to SQL-login must surface
    // them — guard against a regression that left Operators with nowhere to enter the
    // service-account password.
    wrap(<SqlConfig config={{ authentication: 'sql' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText(/svc-app/)).toBeInTheDocument();
    expect(screen.getByPlaceholderText(/SQL_PASSWORD/)).toBeInTheDocument();
  });

  it('builderMode_postgresShowsHostAndPortFields', () => {
    wrap(<SqlConfig config={{ provider: 'postgres' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText(/pg01\.example\.com/)).toBeInTheDocument();
    // Port defaults to 5432 — pinned so a typo'd default can't silently route to a wrong
    // listener on a multi-tenant Postgres host.
    expect(screen.getByDisplayValue('5432')).toBeInTheDocument();
  });

  it('switchToRawMode_clearsBuilderFields', () => {
    const onUpdate = vi.fn();
    wrap(<SqlConfig config={{ server: 'db01', database: 'App' }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.click(screen.getByRole('tab', { name: /connection string/i }));
    // Backend resolves the path by looking at which fields are present: Builder fields must
    // be cleared on mode-switch, otherwise a stale `server` would override the user's
    // freshly typed raw connection string.
    expect(onUpdate).toHaveBeenCalledWith(expect.objectContaining({
      connectionMode: 'raw',
      server: undefined,
      database: undefined,
    }));
  });
});

describe('RestApiConfig', () => {
  it('methodGET_doesNotShowBodyField', () => {
    wrap(<RestApiConfig config={{ method: 'GET' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText('Body (JSON)')).not.toBeInTheDocument();
  });

  it('methodPOST_showsBodyField', () => {
    wrap(<RestApiConfig config={{ method: 'POST' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('Body (JSON)')).toBeInTheDocument();
  });

  it('methodChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RestApiConfig config={{ method: 'GET' }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('GET'), { target: { value: 'POST' } });
    expect(onUpdate).toHaveBeenCalledWith({ method: 'POST' });
  });

  it('proxyModeCustom_revealsProxyAddressField', () => {
    wrap(<RestApiConfig config={{ proxyMode: 'custom' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText(/proxy\.corp\.local/i)).toBeInTheDocument();
  });

  it('proxyModeDefault_hidesProxyAddressField', () => {
    wrap(<RestApiConfig config={{ proxyMode: 'default' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByPlaceholderText(/proxy\.corp\.local/i)).not.toBeInTheDocument();
  });

  it('urlInput_emitsPatchOnChange', () => {
    const onUpdate = vi.fn();
    wrap(<RestApiConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const url = screen.getByPlaceholderText(/api\.example\.com/i) as HTMLInputElement;
    fireEvent.change(url, { target: { value: 'https://api.acme.com/users' } });

    expect(onUpdate).toHaveBeenCalledWith({ url: 'https://api.acme.com/users' });
  });
});

describe('WebhookTriggerConfig', () => {
  it('methodDefault_isPost', () => {
    wrap(<WebhookTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('POST')).toBeInTheDocument();
  });

  it('secretInput_isPasswordType_andEmitsPatch', () => {
    const onUpdate = vi.fn();
    const { container } = wrap(
      <WebhookTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />,
    );

    // type=password ensures shoulder-surfing protection in the editor and stops the value
    // from being captured in browser autofill / form-saver heuristics. Pin the type.
    const secret = container.querySelector('input[type="password"]') as HTMLInputElement;
    expect(secret).toBeTruthy();

    fireEvent.change(secret, { target: { value: 'super-secret-123' } });
    expect(onUpdate).toHaveBeenCalledWith({ secret: 'super-secret-123' });
  });

  it('pathInput_emitsPatchOnChange', () => {
    const onUpdate = vi.fn();
    wrap(<WebhookTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const path = screen.getByPlaceholderText(/api\/webhooks\/my-workflow/i) as HTMLInputElement;
    fireEvent.change(path, { target: { value: 'github-push' } });

    expect(onUpdate).toHaveBeenCalledWith({ path: 'github-push' });
  });

  it('nodePilotHmacV2Selection_emitsExplicitVersionedMode', () => {
    const onUpdate = vi.fn();
    wrap(<WebhookTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const verification = screen.getByDisplayValue(/Shared secret header/i);
    fireEvent.change(verification, { target: { value: 'nodepilot-hmac-v2' } });

    expect(onUpdate).toHaveBeenCalledWith({ signatureMode: 'nodepilot-hmac-v2' });
  });

  it('legacyHmacMode_rendersMigrationWarning', () => {
    wrap(
      <WebhookTriggerConfig
        config={{ signatureMode: 'hmac' }}
        onUpdate={vi.fn()}
        upstreamVars={[]}
      />,
    );

    expect(screen.getByText(/legacy signatureMode 'hmac' is rejected/i)).toBeInTheDocument();
  });
});

describe('ScheduleTriggerConfig', () => {
  it('renders_withEmptyCron_noPreviewBlockShown', () => {
    wrap(<ScheduleTriggerConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Next fire times/i)).not.toBeInTheDocument();
  });

  it('cronInput_emitsPatchOnChange', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduleTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const cron = screen.getByPlaceholderText('0 */5 * * * ?') as HTMLInputElement;
    fireEvent.change(cron, { target: { value: '0 0 8 * * ?' } });

    expect(onUpdate).toHaveBeenCalledWith({ cronExpression: '0 0 8 * * ?' });
  });

  it('presetButton_emitsPresetCron', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduleTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.click(screen.getByRole('button', { name: /Mon-Fri 8 AM/i }));

    expect(onUpdate).toHaveBeenCalledWith({ cronExpression: '0 0 8 ? * MON-FRI' });
  });

  it('validCron_showsPreviewBlock', () => {
    // "Every hour" preset cron renders 5 fire times. Pinning that the preview block surfaces
    // for valid syntax — the previous regression let users save a typo-laden cron and only
    // discover it from the TriggerOrchestrator log.
    wrap(<ScheduleTriggerConfig config={{ cronExpression: '0 0 * * * ?' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Next fire times/i)).toBeInTheDocument();
  });

  it('descriptionInput_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduleTriggerConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const desc = screen.getByPlaceholderText(/weekday at 8 AM/i) as HTMLInputElement;
    fireEvent.change(desc, { target: { value: 'Hourly housekeeping' } });

    expect(onUpdate).toHaveBeenCalledWith({ description: 'Hourly housekeeping' });
  });
});
