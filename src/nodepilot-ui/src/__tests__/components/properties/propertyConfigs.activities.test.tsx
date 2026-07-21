import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { WaitForConditionConfig } from '../../../components/designer/properties/activities/WaitForConditionConfig';
import { LogConfig } from '../../../components/designer/properties/activities/LogConfig';
import { JunctionConfig } from '../../../components/designer/properties/activities/JunctionConfig';
import { ReturnDataConfig } from '../../../components/designer/properties/activities/ReturnDataConfig';
import { StartProgramConfig } from '../../../components/designer/properties/activities/StartProgramConfig';
import { DelayConfig } from '../../../components/designer/properties/activities/DelayConfig';
import { GenerateTextConfig } from '../../../components/designer/properties/activities/GenerateTextConfig';
import { EmailConfig } from '../../../components/designer/properties/activities/EmailConfig';
import { StartWorkflowConfig } from '../../../components/designer/properties/activities/StartWorkflowConfig';
import { ForEachConfig } from '../../../components/designer/properties/activities/ForEachConfig';
import { FileHashConfig } from '../../../components/designer/properties/activities/FileHashConfig';
import { ZipOperationConfig } from '../../../components/designer/properties/activities/ZipOperationConfig';
import { ScheduledTaskConfig } from '../../../components/designer/properties/activities/ScheduledTaskConfig';
import { DecisionConfig } from '../../../components/designer/properties/activities/DecisionConfig';
import { JsonQueryConfig } from '../../../components/designer/properties/activities/JsonQueryConfig';
import { XmlQueryConfig } from '../../../components/designer/properties/activities/XmlQueryConfig';
import { FileOperationConfig } from '../../../components/designer/properties/activities/FileOperationConfig';
import { FolderOperationConfig } from '../../../components/designer/properties/activities/FolderOperationConfig';
import { ServiceManagementConfig } from '../../../components/designer/properties/activities/ServiceManagementConfig';
import { RegistryConfig } from '../../../components/designer/properties/activities/RegistryConfig';
import { WmiQueryConfig } from '../../../components/designer/properties/activities/WmiQueryConfig';
import { PowerManagementConfig } from '../../../components/designer/properties/activities/PowerManagementConfig';
import { RestApiConfig } from '../../../components/designer/properties/activities/RestApiConfig';

/**
 * Sister file to propertyConfigs.test.tsx — covers the 20 activity configs not in the
 * P1 sweep. Each config gets a render-with-empty plus the most consequential interaction
 * (conditional sections, partial-patch shape, default values). The bar is "regression
 * canary", not exhaustive — anything that requires CodeMirror / ConditionBuilder
 * interaction is intentionally skipped, since those would be flaky in jsdom.
 */

beforeEach(() => {
  vi.spyOn(globalThis, 'fetch').mockResolvedValue(
    new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }),
  );
});

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe('RestApiConfig', () => {
  it('rendersObjectHeadersAsText_withoutCrashing', () => {
    // Regression: node data stores `headers` as an object (the engine's ParseHeaders accepts
    // object + newline-string). The field must render it as text, not feed the object into the
    // template validator — `value.includes` would throw and crash the whole designer.
    wrap(
      <RestApiConfig
        config={{ method: 'GET', headers: { 'Content-Type': 'application/json', 'X-Test': 'np' } }}
        onUpdate={vi.fn()}
        upstreamVars={[]}
      />,
    );
    expect(screen.getByDisplayValue(/Content-Type: application\/json/)).toBeInTheDocument();
  });

  it('editingHeaders_persistsTheStringForm', () => {
    const onUpdate = vi.fn();
    wrap(<RestApiConfig config={{ method: 'GET', headers: { Accept: 'text/plain' } }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue(/Accept: text\/plain/), { target: { value: 'Accept: application/json' } });
    expect(onUpdate).toHaveBeenCalledWith({ headers: 'Accept: application/json' });
  });
});

describe('WaitForConditionConfig', () => {
  it('rendersWithEmptyConfig', () => {
    wrap(<WaitForConditionConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Condition Script/i)).toBeInTheDocument();
    expect(screen.getByText('Poll interval (seconds)')).toBeInTheDocument();
  });

  it('snippetButton_replacesScriptWithCannedSnippet', () => {
    const onUpdate = vi.fn();
    wrap(<WaitForConditionConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.click(screen.getByRole('button', { name: /Service running/i }));
    expect(onUpdate).toHaveBeenCalledWith({ script: expect.stringContaining('Get-Service') });
  });

  it('intervalSeconds_negativeInput_clampedToOneMinimum', () => {
    // Pin the floor: a negative typo gets bumped to 1, never persisted as <1 which
    // would saturate the engine with a tight poll loop. (Note: typing "0" is caught by
    // the `|| 5` fallback ahead of Math.max, so 0 silently resets to the default 5 —
    // that's a separate UX path that's also fine.)
    const onUpdate = vi.fn();
    wrap(<WaitForConditionConfig config={{ intervalSeconds: 5 }} onUpdate={onUpdate} upstreamVars={[]} />);

    const input = screen.getByDisplayValue('5') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '-3' } });

    expect(onUpdate).toHaveBeenCalledWith({ intervalSeconds: 1 });
  });
});

describe('LogConfig', () => {
  it('rendersWithEmptyConfig_defaultsToInfoLevel', () => {
    wrap(<LogConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('Info')).toBeInTheDocument();
  });

  it('levelChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<LogConfig config={{ level: 'info' }} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.change(screen.getByDisplayValue('Info'), { target: { value: 'error' } });
    expect(onUpdate).toHaveBeenCalledWith({ level: 'error' });
  });
});

describe('JunctionConfig', () => {
  it('defaultsToWaitAll_doesNotShowRequiredCount', () => {
    wrap(<JunctionConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText('Required Count (N)')).not.toBeInTheDocument();
  });

  it('waitNofM_revealsRequiredCountField', () => {
    wrap(<JunctionConfig config={{ mode: 'waitNofM' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    // Per the workflow-styleguide pin: waitNofM uses requiredCount, not the "n" alias.
    expect(screen.getByText('Required Count (N)')).toBeInTheDocument();
  });

  it('waitAny_showsSkipWarning', () => {
    // Operators rely on the warning banner before saving — without it they assume
    // "wait for any" still completes the sibling branches.
    wrap(<JunctionConfig config={{ mode: 'waitAny' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Skipped/i)).toBeInTheDocument();
  });
});

describe('ReturnDataConfig', () => {
  it('emptyData_showsHint', () => {
    wrap(<ReturnDataConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/No fields/i)).toBeInTheDocument();
  });

  it('addField_emitsPatchWithEmptyKeyValue', () => {
    const onUpdate = vi.fn();
    wrap(<ReturnDataConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    fireEvent.click(screen.getByRole('button', { name: /\+ Field/i }));

    expect(onUpdate).toHaveBeenCalledWith({ data: { '': '' } });
  });

  it('existingKeys_renderInOrder', () => {
    wrap(<ReturnDataConfig config={{ data: { foo: 'a', bar: 'b' } }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('foo')).toBeInTheDocument();
    expect(screen.getByDisplayValue('bar')).toBeInTheDocument();
  });
});

describe('StartProgramConfig', () => {
  // The timeout field no longer renders inline here — it moved to the separate Timeout
  // section in PropertiesPanel, shown only when `config.waitForExit` is set.
  it('rendersWithEmptyConfig_waitForExitDefaultTrue', () => {
    wrap(<StartProgramConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Auf Beendigung warten/i)).toBeInTheDocument();
    expect(screen.getByText(/Success Exit Codes/i)).toBeInTheDocument();
    expect(screen.queryByText('Timeout (Sekunden)')).not.toBeInTheDocument();
  });

  it('waitForExitFalse_hidesExitCodes', () => {
    wrap(<StartProgramConfig config={{ waitForExit: false }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Success Exit Codes/i)).not.toBeInTheDocument();
  });

  it('useShellExecuteToggle_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<StartProgramConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);

    const checkboxes = screen.getAllByRole('checkbox');
    // First checkbox is useShellExecute, second is waitForExit.
    fireEvent.click(checkboxes[0]);
    expect(onUpdate).toHaveBeenCalledWith({ useShellExecute: true });
  });
});

describe('DelayConfig', () => {
  it('defaultsTo5Seconds_whenConfigEmpty', () => {
    wrap(<DelayConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('5')).toBeInTheDocument();
  });

  it('secondsChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<DelayConfig config={{ seconds: 5 }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue('5'), { target: { value: '30' } });
    expect(onUpdate).toHaveBeenCalledWith({ seconds: 30 });
  });
});

describe('GenerateTextConfig', () => {
  it('defaultsToAlphanumericAndLength16_whenConfigEmpty', () => {
    wrap(<GenerateTextConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect((screen.getByRole('combobox') as HTMLSelectElement).value).toBe('alphanumeric');
    expect(screen.getByDisplayValue('16')).toBeInTheDocument();
  });

  it('modeChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<GenerateTextConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'guid' } });
    expect(onUpdate).toHaveBeenCalledWith({ mode: 'guid' });
  });

  it('guidMode_hidesLengthField', () => {
    wrap(<GenerateTextConfig config={{ mode: 'guid' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByDisplayValue('16')).not.toBeInTheDocument();
  });

  it('customMode_showsCustomCharsetField', () => {
    wrap(<GenerateTextConfig config={{ mode: 'custom' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByPlaceholderText('ABCDEF0123456789')).toBeInTheDocument();
  });

  it('nonCustomMode_hidesCustomCharsetField', () => {
    wrap(<GenerateTextConfig config={{ mode: 'alphanumeric' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByPlaceholderText('ABCDEF0123456789')).not.toBeInTheDocument();
  });

  it('lengthChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<GenerateTextConfig config={{ length: 16 }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue('16'), { target: { value: '32' } });
    expect(onUpdate).toHaveBeenCalledWith({ length: 32 });
  });
});

describe('EmailConfig', () => {
  it('rendersAllFields', () => {
    wrap(<EmailConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('To')).toBeInTheDocument();
    expect(screen.getByText('Subject')).toBeInTheDocument();
    expect(screen.getByText('Body')).toBeInTheDocument();
    expect(screen.getByLabelText(/HTML body/i)).toBeInTheDocument();
  });

  it('isHtmlToggle_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<EmailConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.click(screen.getByLabelText(/HTML body/i));
    expect(onUpdate).toHaveBeenCalledWith({ isHtml: true });
  });
});

describe('StartWorkflowConfig', () => {
  // The timeout field no longer renders inline here — it moved to the separate Timeout
  // section in PropertiesPanel, shown only when `config.waitForCompletion` is set.
  it('renders_waitForCompletionDefaultTrue', () => {
    wrap(<StartWorkflowConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Synchron \(warten\)/i)).toBeInTheDocument();
    expect(screen.queryByText('Timeout (Sekunden)')).not.toBeInTheDocument();
  });

  it('waitForCompletionFalse_showsFireAndForget', () => {
    wrap(<StartWorkflowConfig config={{ waitForCompletion: false }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText('Timeout (Sekunden)')).not.toBeInTheDocument();
    expect(screen.getByText(/Fire-and-forget/i)).toBeInTheDocument();
  });

  it('libraryPickerButton_callsCallback', () => {
    const onPicker = vi.fn();
    wrap(<StartWorkflowConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} onOpenWorkflowPicker={onPicker} />);
    fireEvent.click(screen.getByRole('button', { name: /Aus Library wählen/i }));
    expect(onPicker).toHaveBeenCalled();
  });
});

describe('ForEachConfig', () => {
  it('defaultsItemAndIndexParameterNames', () => {
    wrap(<ForEachConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('item')).toBeInTheDocument();
    expect(screen.getByDisplayValue('index')).toBeInTheDocument();
  });

  it('parallelismChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<ForEachConfig config={{ maxParallelism: 1 }} onUpdate={onUpdate} upstreamVars={[]} />);

    // Two number inputs: parallelism (default 1, value=1) + timeoutSecondsPerItem (default 3600).
    const inputs = screen.getAllByRole('spinbutton');
    const parallelism = inputs.find(i => (i as HTMLInputElement).value === '1') as HTMLInputElement;
    fireEvent.change(parallelism, { target: { value: '8' } });

    expect(onUpdate).toHaveBeenCalledWith({ maxParallelism: 8 });
  });

  it('continueOnError_togglesPatch', () => {
    const onUpdate = vi.fn();
    wrap(<ForEachConfig config={{ continueOnError: false }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.click(screen.getByRole('checkbox'));
    expect(onUpdate).toHaveBeenCalledWith({ continueOnError: true });
  });
});

describe('FileHashConfig', () => {
  it('defaultsToSha256', () => {
    wrap(<FileHashConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('SHA256')).toBeInTheDocument();
  });

  it('algorithmChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<FileHashConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue('SHA256'), { target: { value: 'MD5' } });
    expect(onUpdate).toHaveBeenCalledWith({ algorithm: 'MD5' });
  });
});

describe('ZipOperationConfig', () => {
  it('compressMode_showsCompressionLevel', () => {
    wrap(<ZipOperationConfig config={{ operation: 'compress' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('Compression Level')).toBeInTheDocument();
  });

  it('extractMode_hidesCompressionLevel', () => {
    wrap(<ZipOperationConfig config={{ operation: 'extract' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText('Compression Level')).not.toBeInTheDocument();
  });

  it('operationChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<ZipOperationConfig config={{ operation: 'compress' }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue('Compress (zip)'), { target: { value: 'extract' } });
    expect(onUpdate).toHaveBeenCalledWith({ operation: 'extract' });
  });

  // Dropdown defaults visually to "compress", but if user never touched it the saved
  // JSON had no `operation` key and runs failed with "'operation' is required". The
  // panel now backfills the default the moment it opens.
  it('missingOperation_persistsCompressDefaultOnMount', () => {
    const onUpdate = vi.fn();
    wrap(<ZipOperationConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).toHaveBeenCalledWith({ operation: 'compress' });
  });

  it('operationAlreadySet_doesNotEmitDefault', () => {
    const onUpdate = vi.fn();
    wrap(<ZipOperationConfig config={{ operation: 'extract' }} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).not.toHaveBeenCalled();
  });
});

describe('ScheduledTaskConfig', () => {
  it('defaultsToGetAction_doesNotShowRegisterFields', () => {
    wrap(<ScheduledTaskConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Programm \(executable\)/i)).not.toBeInTheDocument();
  });

  it('registerAction_revealsProgramAndTriggerFields', () => {
    wrap(<ScheduledTaskConfig config={{ action: 'register' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Programm \(executable\)/i)).toBeInTheDocument();
    expect(screen.getByText('Trigger-Typ')).toBeInTheDocument();
  });

  it('weeklyTrigger_showsDayButtonsAndIntervalField', () => {
    wrap(<ScheduledTaskConfig config={{ action: 'register', triggerType: 'weekly' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('Wochentage')).toBeInTheDocument();
    expect(screen.getByText('Intervall (Wochen)')).toBeInTheDocument();
  });

  it('unregisterAction_showsDangerWarning', () => {
    wrap(<ScheduledTaskConfig config={{ action: 'unregister' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Löscht den Task dauerhaft/i)).toBeInTheDocument();
  });

  // The dropdown defaults visually to "get", but the saved JSON had no `action` key,
  // so workflows would run with action='' and fail with "unknown action ''". The panel
  // now backfills the default the moment it opens.
  it('missingAction_persistsGetDefaultOnMount', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduledTaskConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).toHaveBeenCalledWith({ action: 'get' });
  });

  it('actionAlreadySet_doesNotEmitDefault', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduledTaskConfig config={{ action: 'start' }} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).not.toHaveBeenCalled();
  });

  // Same bug pattern within the register branch: triggerType dropdown shows
  // "Täglich (daily)" but doesn't persist. Backend's BuildRegisterScript throws on
  // missing triggerType, so the persistence must follow when action flips to register.
  it('registerWithoutTriggerType_persistsDailyDefault', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduledTaskConfig config={{ action: 'register' }} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).toHaveBeenCalledWith({ triggerType: 'daily' });
  });

  it('registerWithTriggerType_doesNotEmitTriggerTypeDefault', () => {
    const onUpdate = vi.fn();
    wrap(<ScheduledTaskConfig config={{ action: 'register', triggerType: 'weekly' }} onUpdate={onUpdate} upstreamVars={[]} />);
    // Only one call expected: action is already set, so the action-effect skips.
    // The triggerType-effect should also skip since 'weekly' is already present.
    expect(onUpdate).not.toHaveBeenCalled();
  });
});

describe('DecisionConfig', () => {
  it('rendersWithEmptyCases_showsAddButtonAndDefaultName', () => {
    wrap(<DecisionConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByRole('button', { name: /Add case/i })).toBeInTheDocument();
    expect(screen.getByDisplayValue('default')).toBeInTheDocument();
  });

  it('addCase_appendsCaseEntry', () => {
    const onUpdate = vi.fn();
    wrap(<DecisionConfig config={{ cases: [] }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.click(screen.getByRole('button', { name: /Add case/i }));
    expect(onUpdate).toHaveBeenCalledWith({
      cases: expect.arrayContaining([expect.objectContaining({ name: 'case1' })]),
    });
  });

  it('defaultCaseName_emitsPatchOnChange', () => {
    const onUpdate = vi.fn();
    wrap(<DecisionConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue('default'), { target: { value: 'fallback' } });
    expect(onUpdate).toHaveBeenCalledWith({ defaultCaseName: 'fallback' });
  });
});

describe('JsonQueryConfig', () => {
  it('defaultsToInlineSource', () => {
    wrap(<JsonQueryConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('JSON Content')).toBeInTheDocument();
    expect(screen.queryByText('File Path')).not.toBeInTheDocument();
  });

  it('fileSource_revealsPathField', () => {
    wrap(<JsonQueryConfig config={{ source: 'file' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('File Path')).toBeInTheDocument();
    expect(screen.queryByText('JSON Content')).not.toBeInTheDocument();
  });

  it('jsonPathInput_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<JsonQueryConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    const input = screen.getByPlaceholderText(/\$\.items/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: '$.users[0].id' } });
    expect(onUpdate).toHaveBeenCalledWith({ jsonPath: '$.users[0].id' });
  });
});

describe('XmlQueryConfig', () => {
  it('defaultsToInlineSource_showsXmlContent', () => {
    wrap(<XmlQueryConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('XML Content')).toBeInTheDocument();
  });

  it('xpathInput_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<XmlQueryConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    const input = screen.getByPlaceholderText('//book/title') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '//item/@id' } });
    expect(onUpdate).toHaveBeenCalledWith({ xpath: '//item/@id' });
  });
});

describe('FileOperationConfig', () => {
  it('copyOperation_showsDestinationField', () => {
    wrap(<FileOperationConfig config={{ operation: 'copy' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Copy to \(Destination\)/i)).toBeInTheDocument();
  });

  it('moveOperation_showsDestinationField', () => {
    wrap(<FileOperationConfig config={{ operation: 'move' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Move to \(Destination\)/i)).toBeInTheDocument();
  });

  it('deleteOperation_hidesDestinationAndNewName', () => {
    wrap(<FileOperationConfig config={{ operation: 'delete' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/New name/i)).not.toBeInTheDocument();
  });

  it('existsOperation_hidesDestinationAndNewName', () => {
    wrap(<FileOperationConfig config={{ operation: 'exists' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/New name/i)).not.toBeInTheDocument();
  });

  it('renameOperation_showsNewNameField_hidesDestination', () => {
    wrap(<FileOperationConfig config={{ operation: 'rename' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/New name/i)).toBeInTheDocument();
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
  });

  it('emptyConfig_initialisesOperationToCopy', () => {
    // useEffect on mount writes the default — pin the auto-init behaviour so a refactor
    // that drops the effect doesn't silently leave the saved workflow with operation=undefined.
    const onUpdate = vi.fn();
    wrap(<FileOperationConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).toHaveBeenCalledWith({ operation: 'copy' });
  });

  it('operationDropdown_doesNotOfferFolderOnlyOps', () => {
    // File activity has no list option (folder-exclusive). create exists but is labelled
    // "Create (empty file)", so a regex anchored on the folder-only "Create" label proves
    // the ambiguity is resolved.
    wrap(<FileOperationConfig config={{ operation: 'copy' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByRole('option', { name: /List Contents/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('option', { name: /^Create$/ })).not.toBeInTheDocument();
    // But the new file-create is offered with its own label
    expect(screen.getByRole('option', { name: /Create \(empty file\)/i })).toBeInTheDocument();
  });

  it('createOperation_showsOnlyPathField', () => {
    wrap(<FileOperationConfig config={{ operation: 'create' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/New name/i)).not.toBeInTheDocument();
  });

  it('pathInput_emitsPatch', () => {
    // Use 'delete' so only the path input renders — copy/move would also render a destination
    // input with an overlapping placeholder ("…file.txt"), making the lookup ambiguous.
    const onUpdate = vi.fn();
    wrap(<FileOperationConfig config={{ operation: 'delete' }} onUpdate={onUpdate} upstreamVars={[]} />);
    const input = screen.getByPlaceholderText('C:\\Temp\\file.txt') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'C:\\test.txt' } });
    expect(onUpdate).toHaveBeenCalledWith({ path: 'C:\\test.txt' });
  });
});

describe('FolderOperationConfig', () => {
  it('copyOperation_showsDestinationField', () => {
    wrap(<FolderOperationConfig config={{ operation: 'copy' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/Copy to \(Destination\)/i)).toBeInTheDocument();
  });

  it('createOperation_showsOnlyPathField', () => {
    wrap(<FolderOperationConfig config={{ operation: 'create' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/New name/i)).not.toBeInTheDocument();
  });

  it('listOperation_showsOnlyPathField', () => {
    wrap(<FolderOperationConfig config={{ operation: 'list' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/New name/i)).not.toBeInTheDocument();
  });

  it('renameOperation_showsNewNameField', () => {
    wrap(<FolderOperationConfig config={{ operation: 'rename' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText(/New name/i)).toBeInTheDocument();
  });

  it('deleteOperation_hidesDestinationAndNewName', () => {
    wrap(<FolderOperationConfig config={{ operation: 'delete' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/Destination/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/New name/i)).not.toBeInTheDocument();
  });

  it('emptyConfig_initialisesOperationToCopy', () => {
    const onUpdate = vi.fn();
    wrap(<FolderOperationConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).toHaveBeenCalledWith({ operation: 'copy' });
  });

  it('operationDropdown_offersAllSevenFolderOps', () => {
    // Pin the full op-set so a refactor that drops one (list/create are folder-exclusive)
    // breaks here instead of mysteriously disappearing in the UI.
    wrap(<FolderOperationConfig config={{ operation: 'copy' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByRole('option', { name: 'Copy' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Move' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Rename' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Delete' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Check Exists' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'List Contents' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Create' })).toBeInTheDocument();
  });

  it('pathInput_emitsPatch', () => {
    // Use 'delete' so only the path input renders (no destination row with overlapping placeholder).
    const onUpdate = vi.fn();
    wrap(<FolderOperationConfig config={{ operation: 'delete' }} onUpdate={onUpdate} upstreamVars={[]} />);
    const input = screen.getByPlaceholderText('C:\\Temp\\Folder') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'C:\\TestFolder' } });
    expect(onUpdate).toHaveBeenCalledWith({ path: 'C:\\TestFolder' });
  });
});

describe('ServiceManagementConfig', () => {
  it('defaultsToStatusAction', () => {
    wrap(<ServiceManagementConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('Get Status')).toBeInTheDocument();
  });

  it('serviceNameInput_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<ServiceManagementConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    const input = screen.getByPlaceholderText('Spooler') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'wuauserv' } });
    expect(onUpdate).toHaveBeenCalledWith({ serviceName: 'wuauserv' });
  });
});

describe('RegistryConfig', () => {
  it('readOperation_doesNotShowValueOrTypeField', () => {
    wrap(<RegistryConfig config={{ operation: 'read' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText('Wert')).not.toBeInTheDocument();
    expect(screen.queryByText('Value-Typ')).not.toBeInTheDocument();
  });

  it('writeOperation_showsValueAndType', () => {
    wrap(<RegistryConfig config={{ operation: 'write' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('Wert')).toBeInTheDocument();
    expect(screen.getByText('Value-Typ')).toBeInTheDocument();
  });

  it('deleteKeyOperation_hidesValueName', () => {
    wrap(<RegistryConfig config={{ operation: 'deleteKey' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/^Value-Name/i)).not.toBeInTheDocument();
  });

  it('createKeyOperation_hidesValueName', () => {
    wrap(<RegistryConfig config={{ operation: 'createKey' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/^Value-Name/i)).not.toBeInTheDocument();
  });

  it('listSubKeysOperation_hidesValueName', () => {
    wrap(<RegistryConfig config={{ operation: 'listSubKeys' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText(/^Value-Name/i)).not.toBeInTheDocument();
  });

  it('writeOperation_changesValueTypeEmitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RegistryConfig config={{ operation: 'write' }} onUpdate={onUpdate} upstreamVars={[]} />);
    const selects = screen.getAllByRole('combobox') as HTMLSelectElement[];
    const typeSelect = selects[selects.length - 1];
    fireEvent.change(typeSelect, { target: { value: 'DWord' } });
    expect(onUpdate).toHaveBeenCalledWith({ valueType: 'DWord' });
  });

  it('opChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<RegistryConfig config={{ operation: 'read' }} onUpdate={onUpdate} upstreamVars={[]} />);
    const opSelect = screen.getAllByRole('combobox')[0] as HTMLSelectElement;
    fireEvent.change(opSelect, { target: { value: 'listSubKeys' } });
    expect(onUpdate).toHaveBeenCalledWith({ operation: 'listSubKeys' });
  });
});

describe('WmiQueryConfig', () => {
  it('defaultsToCimv2Namespace', () => {
    wrap(<WmiQueryConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByDisplayValue('root\\cimv2')).toBeInTheDocument();
  });

  it('classNameInput_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<WmiQueryConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    const input = screen.getByPlaceholderText('Win32_OperatingSystem') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'Win32_Process' } });
    expect(onUpdate).toHaveBeenCalledWith({ className: 'Win32_Process' });
  });
});

describe('PowerManagementConfig', () => {
  it('defaultsToShutdown_showsDelayAndForce', () => {
    wrap(<PowerManagementConfig config={{}} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.getByText('Delay (seconds)')).toBeInTheDocument();
    expect(screen.getByText(/Force close running apps/i)).toBeInTheDocument();
  });

  it('logoffAction_hidesDelayAndForce', () => {
    // logoff/abort/hibernate ignore delay+force — the UI must not show fields that don't apply.
    wrap(<PowerManagementConfig config={{ action: 'logoff' }} onUpdate={vi.fn()} upstreamVars={[]} />);
    expect(screen.queryByText('Delay (seconds)')).not.toBeInTheDocument();
    expect(screen.queryByText(/Force close running apps/i)).not.toBeInTheDocument();
  });

  it('actionChange_emitsPatch', () => {
    const onUpdate = vi.fn();
    wrap(<PowerManagementConfig config={{ action: 'shutdown' }} onUpdate={onUpdate} upstreamVars={[]} />);
    fireEvent.change(screen.getByDisplayValue('Shutdown (power off)'), { target: { value: 'restart' } });
    expect(onUpdate).toHaveBeenCalledWith({ action: 'restart' });
  });

  // Dropdown defaults visually to "shutdown", but if user never touched it the saved
  // JSON had no `action` key and runs failed with "'action' is required". The panel
  // now backfills the default the moment it opens.
  it('missingAction_persistsShutdownDefaultOnMount', () => {
    const onUpdate = vi.fn();
    wrap(<PowerManagementConfig config={{}} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).toHaveBeenCalledWith({ action: 'shutdown' });
  });

  it('actionAlreadySet_doesNotEmitDefault', () => {
    const onUpdate = vi.fn();
    wrap(<PowerManagementConfig config={{ action: 'logoff' }} onUpdate={onUpdate} upstreamVars={[]} />);
    expect(onUpdate).not.toHaveBeenCalled();
  });
});
