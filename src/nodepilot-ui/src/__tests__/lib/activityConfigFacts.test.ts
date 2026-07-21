import { describe, it, expect } from 'vitest';
import { checkRequiredActivityConfig, summarizeActivityConfig } from '../../lib/activityConfigFacts';

/**
 * Activity summaries are the one-line previews shown in the node header on the canvas
 * (e.g. "GET https://…", "Wait 5 seconds"). Each summarizer collapses a config blob into
 * a single string. Pin the substring/key choices so a refactor that drops a field doesn't
 * silently break the in-canvas preview.
 */

describe('summarizeActivityConfig', () => {
  describe('runScript', () => {
    it('emptyScript_returnsPlaceholder', () => {
      expect(summarizeActivityConfig('runScript', {})).toBe('No script defined');
    });

    it('shortScript_returnedVerbatim', () => {
      expect(summarizeActivityConfig('runScript', { script: 'Get-Service' })).toBe('Get-Service');
    });

    it('multilineScript_truncatedAt3LinesWithEllipsis', () => {
      const script = 'a\nb\nc\nd\ne';
      expect(summarizeActivityConfig('runScript', { script })).toBe('a\nb\nc\n...');
    });

    it('multilineScript_filtersBlankLines', () => {
      // Empty lines from .filter(Boolean) should not count toward the 3-line limit.
      expect(summarizeActivityConfig('runScript', { script: 'a\n\nb' })).toBe('a\nb');
    });
  });

  describe('llmQuery', () => {
    it('requiresPrompt', () => {
      expect(checkRequiredActivityConfig('llmQuery', {})).toContain('Prompt');
      expect(checkRequiredActivityConfig('llmQuery', { prompt: 'hi' })).toBeNull();
    });

    it('acceptsTemplateBaseUrlButRejectsInvalidLiteral', () => {
      // Template baseUrl is resolved by the StepRunner → allowed at design time.
      expect(checkRequiredActivityConfig('llmQuery', { prompt: 'hi', baseUrl: '{{globals.LLM_BASE_URL}}' })).toBeNull();
      expect(checkRequiredActivityConfig('llmQuery', { prompt: 'hi', baseUrl: 'ftp://x' })).toContain('http');
    });

    it('rejectsOutOfRangeTemperatureAndNonPositiveMaxTokens', () => {
      expect(checkRequiredActivityConfig('llmQuery', { prompt: 'hi', temperature: 5 })).toContain('Temperature');
      expect(checkRequiredActivityConfig('llmQuery', { prompt: 'hi', maxTokens: 0 })).toContain('maxTokens');
    });

    it('summarizesModelAndPrompt', () => {
      expect(summarizeActivityConfig('llmQuery', { model: 'llama3', prompt: 'Summarize the log' }))
        .toBe('llama3 · Summarize the log');
    });
  });

  describe('fileOperation', () => {
    it('copy_rendersOperationPathAndDestination', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'copy', path: 'C:\\src.txt', destination: 'D:\\dst.txt' }))
        .toBe('copy: C:\\src.txt → D:\\dst.txt');
    });

    it('move_rendersOperationPathAndDestination', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'move', path: 'C:\\src.txt', destination: 'D:\\dst.txt' }))
        .toBe('move: C:\\src.txt → D:\\dst.txt');
    });

    it('delete_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'delete', path: 'C:\\old.txt' }))
        .toBe('delete: C:\\old.txt');
    });

    it('exists_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'exists', path: 'C:\\f.txt' }))
        .toBe('exists: C:\\f.txt');
    });

    it('rename_rendersPathAndNewName', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'rename', path: 'C:\\old.txt', newName: 'new.txt' }))
        .toBe('rename: C:\\old.txt → new.txt');
    });

    it('rename_missingNewName_showsPlaceholder', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'rename', path: 'C:\\old.txt' }))
        .toBe('rename: C:\\old.txt → (no new name)');
    });

    it('copy_missingDestination_showsPlaceholder', () => {
      expect(summarizeActivityConfig('fileOperation', { operation: 'copy', path: 'C:\\src.txt' }))
        .toBe('copy: C:\\src.txt → (no destination)');
    });

    it('emptyConfig_usesDefaultsAndPlaceholder', () => {
      expect(summarizeActivityConfig('fileOperation', {})).toBe('copy: (no path) → (no destination)');
    });
  });

  describe('folderOperation', () => {
    it('copy_rendersOperationPathAndDestination', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'copy', path: 'C:\\src', destination: 'D:\\dst' }))
        .toBe('copy: C:\\src → D:\\dst');
    });

    it('move_rendersOperationPathAndDestination', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'move', path: 'C:\\src', destination: 'D:\\dst' }))
        .toBe('move: C:\\src → D:\\dst');
    });

    it('delete_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'delete', path: 'C:\\old' }))
        .toBe('delete: C:\\old');
    });

    it('list_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'list', path: 'C:\\dir' }))
        .toBe('list: C:\\dir');
    });

    it('create_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'create', path: 'C:\\new' }))
        .toBe('create: C:\\new');
    });

    it('exists_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'exists', path: 'C:\\dir' }))
        .toBe('exists: C:\\dir');
    });

    it('rename_rendersPathAndNewName', () => {
      expect(summarizeActivityConfig('folderOperation', { operation: 'rename', path: 'C:\\old', newName: 'new' }))
        .toBe('rename: C:\\old → new');
    });

    it('emptyConfig_usesDefaultsAndPlaceholder', () => {
      expect(summarizeActivityConfig('folderOperation', {})).toBe('copy: (no path) → (no destination)');
    });
  });

  describe('legacy fileSystemOperation type is removed', () => {
    it('returnsEmptyString_indicatingTheActivityIsNoLongerKnown', () => {
      // Pin the clean-cut behaviour: the legacy type is gone, summarizer falls through
      // to the default empty-string branch. A smoke-test that catches a partial revert.
      expect(summarizeActivityConfig('fileSystemOperation', { operation: 'copy', path: 'X', destination: 'Y' }))
        .toBe('');
    });
  });

  describe('serviceManagement', () => {
    it('rendersActionAndQuotedService', () => {
      expect(summarizeActivityConfig('serviceManagement', { action: 'restart', serviceName: 'Spooler' }))
        .toBe('restart "Spooler"');
    });

    it('emptyConfig_defaultsToStatus', () => {
      expect(summarizeActivityConfig('serviceManagement', {})).toBe('status "..."');
    });
  });

  describe('restApi', () => {
    it('rendersMethodAndUrl', () => {
      expect(summarizeActivityConfig('restApi', { method: 'POST', url: 'https://api.test' }))
        .toBe('POST https://api.test');
    });

    it('emptyConfig_defaultsToGet', () => {
      expect(summarizeActivityConfig('restApi', {})).toBe('GET ...');
    });
  });

  describe('sql', () => {
    it('emptyQuery_returnsPlaceholder', () => {
      expect(summarizeActivityConfig('sql', {})).toBe('(no query)');
    });

    it('shortQuery_renderedWithProvider', () => {
      expect(summarizeActivityConfig('sql', { query: 'SELECT 1', provider: 'sqlite' }))
        .toBe('sqlite: SELECT 1');
    });

    it('longQuery_truncatedAt60Chars', () => {
      const longQuery = 'SELECT a, b, c, d, e, f FROM very_long_table_name WHERE x = 1 ORDER BY a';
      const summary = summarizeActivityConfig('sql', { query: longQuery });
      expect(summary).toMatch(/^sqlserver: /);
      expect(summary).toContain('…');
    });

    it('multilineQuery_onlyFirstLineUsed', () => {
      expect(summarizeActivityConfig('sql', { query: 'SELECT 1\nFROM t' }))
        .toBe('sqlserver: SELECT 1');
    });
  });

  describe('emailNotification', () => {
    it('renderedWithRecipient', () => {
      expect(summarizeActivityConfig('emailNotification', { to: 'a@b.c' })).toBe('To: a@b.c');
    });

    it('emptyConfig_showsPlaceholder', () => {
      expect(summarizeActivityConfig('emailNotification', {})).toBe('To: (no recipient)');
    });
  });

  describe('delay', () => {
    it('rendersSeconds', () => {
      expect(summarizeActivityConfig('delay', { seconds: 30 })).toBe('Wait 30 seconds');
    });

    it('emptyConfig_defaultsTo5', () => {
      expect(summarizeActivityConfig('delay', {})).toBe('Wait 5 seconds');
    });
  });

  describe('powerManagement', () => {
    it('immediateAction_omitsDelay', () => {
      expect(summarizeActivityConfig('powerManagement', { action: 'restart' })).toBe('restart');
    });

    it('delayedAction_includesDelay', () => {
      expect(summarizeActivityConfig('powerManagement', { action: 'shutdown', delaySeconds: 60 }))
        .toBe('shutdown in 60s');
    });

    it('emptyConfig_defaultsToShutdown', () => {
      expect(summarizeActivityConfig('powerManagement', {})).toBe('shutdown');
    });
  });

  describe('triggers', () => {
    it('manualTrigger_usesTitleOrFallback', () => {
      expect(summarizeActivityConfig('manualTrigger', { title: 'Run nightly' })).toBe('Run nightly');
      expect(summarizeActivityConfig('manualTrigger', {})).toBe('Manual start');
    });

    it('scheduleTrigger_rendersCron', () => {
      expect(summarizeActivityConfig('scheduleTrigger', { cronExpression: '0 0 * * * ? *' }))
        .toBe('0 0 * * * ? *');
    });

    it('webhookTrigger_rendersMethodAndPath', () => {
      expect(summarizeActivityConfig('webhookTrigger', { method: 'GET', path: 'incoming' }))
        .toBe('GET incoming');
    });

    it('webhookTrigger_hmacMode_appendsBadge', () => {
      expect(summarizeActivityConfig('webhookTrigger', { method: 'POST', path: 'hook', signatureMode: 'nodepilot-hmac-v2' }))
        .toBe('POST hook · HMAC v2');
    });

    it('fileWatcherTrigger_rendersWatchTypeAndDir', () => {
      expect(summarizeActivityConfig('fileWatcherTrigger', { watchType: 'changed', directory: 'C:\\inbox' }))
        .toBe('changed: C:\\inbox');
    });

    it('databaseTrigger_renderedAsTruncatedQuery', () => {
      expect(summarizeActivityConfig('databaseTrigger', { query: 'SELECT * FROM jobs' }))
        .toBe('SELECT * FROM jobs');
      expect(summarizeActivityConfig('databaseTrigger', {})).toBe('No query set');
    });

    it('eventLogTrigger_includesEventIdWhenSet', () => {
      expect(summarizeActivityConfig('eventLogTrigger', { logName: 'Security', eventId: 4625 }))
        .toBe('Security Event ID: 4625');
      expect(summarizeActivityConfig('eventLogTrigger', {})).toBe('Application');
    });
  });

  describe('junction', () => {
    it('waitAll_default', () => {
      expect(summarizeActivityConfig('junction', {})).toBe('Wait for all branches');
    });

    it('waitAny_specific', () => {
      expect(summarizeActivityConfig('junction', { mode: 'waitAny' })).toBe('Wait for any 1 branch');
    });

    it('waitNofM_includesRequiredCount', () => {
      expect(summarizeActivityConfig('junction', { mode: 'waitNofM', requiredCount: 3 }))
        .toBe('Wait for 3 branches');
    });

    it('waitNofM_missingRequiredCount_defaultsTo2', () => {
      expect(summarizeActivityConfig('junction', { mode: 'waitNofM' })).toBe('Wait for 2 branches');
    });
  });

  describe('generateText', () => {
    it('summarizesLengthAndMode', () => {
      expect(summarizeActivityConfig('generateText', { length: 12, mode: 'hex' })).toBe('12 chars (hex)');
    });

    it('emptyConfig_defaultsTo16Alphanumeric', () => {
      expect(summarizeActivityConfig('generateText', {})).toBe('16 chars (alphanumeric)');
    });

    // Pre-publish guard mirrors the backend TryBuildCharset rule: mode=custom needs a charset.
    it('customModeWithoutCharset_returnsError', () => {
      expect(checkRequiredActivityConfig('generateText', { mode: 'custom' })).toContain('customCharset');
    });

    it('customModeWithCharset_returnsNull', () => {
      expect(checkRequiredActivityConfig('generateText', { mode: 'custom', customCharset: 'ABC' })).toBeNull();
    });

    it('nonCustomMode_returnsNull', () => {
      expect(checkRequiredActivityConfig('generateText', { mode: 'alphanumeric' })).toBeNull();
    });
  });

  describe('textFileEdit', () => {
    // Pre-publish guard: without this entry the designer would happily publish a
    // textFileEdit step with no path / no content / no selector, and the runtime
    // would surface the misuse only at execution time. Each branch below pins one
    // of the C#-side ValidateOpRequirements rules so the two validators stay in sync.

    it('missingPath_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'append', content: 'x' }))
        .toContain('Pfad');
    });

    it('unknownOperation_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'truncate', path: 'C:\\f.txt' }))
        .toContain('Unbekannte Operation');
    });

    it('appendWithoutContent_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'append', path: 'C:\\f.txt' }))
        .toContain("'append' benötigt 'content'");
    });

    it('insertWithoutLineNumber_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'insert', path: 'C:\\f.txt', content: 'x' }))
        .toContain('lineNumber');
    });

    it('deleteWithoutSelector_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'delete', path: 'C:\\f.txt' }))
        .toContain('genau eines');
    });

    it('deleteWithMultipleSelectors_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', {
        operation: 'delete', path: 'C:\\f.txt', lineNumber: 1, matchPattern: 'x',
      })).toContain('nur eines');
    });

    it('replaceWithoutMatchPattern_returnsError', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'replace', path: 'C:\\f.txt', replace: 'b' }))
        .toContain('matchPattern');
    });

    it('replaceWithEmptyReplaceString_isAccepted', () => {
      // An empty `replace` is intentional — "delete the matches in-place". The validator
      // must distinguish "key missing" from "key === empty string".
      expect(checkRequiredActivityConfig('textFileEdit', {
        operation: 'replace', path: 'C:\\f.txt', matchPattern: 'a', replace: '',
      })).toBeNull();
    });

    it('validAppendConfig_returnsNull', () => {
      expect(checkRequiredActivityConfig('textFileEdit', { operation: 'append', path: 'C:\\f.txt', content: 'line' }))
        .toBeNull();
    });

    it('templatePath_isAccepted', () => {
      // The validator treats {{template}}-expressions as filled — actual resolution
      // happens at run-time and might still come up empty, but the design-time check
      // can't second-guess that.
      expect(checkRequiredActivityConfig('textFileEdit', {
        operation: 'append', path: '{{step.param.path}}', content: 'x',
      })).toBeNull();
    });

    it('summarize_rendersOperationAndPath', () => {
      expect(summarizeActivityConfig('textFileEdit', { operation: 'replace', path: 'C:\\hosts' }))
        .toBe('replace: C:\\hosts');
    });

    it('summarize_emptyConfig_usesDefaultsAndPlaceholder', () => {
      expect(summarizeActivityConfig('textFileEdit', {})).toBe('append: (no path)');
    });
  });

  describe('unknown type', () => {
    it('returnsEmptyString', () => {
      // Unknown activity types fall through — empty string is the contract; the
      // PropertiesPanel handles "no preview" by hiding the preview row.
      expect(summarizeActivityConfig('definitelyNotARealType', {})).toBe('');
    });
  });
});
