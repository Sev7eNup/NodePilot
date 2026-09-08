import { describe, it, expect } from 'vitest';
import i18n from '../../i18n';
import { checkRequiredActivityConfig, type ActivityConfig } from '../../lib/activityConfigFacts';
import de from '../../i18n/locales/de/activities.json';
import en from '../../i18n/locales/en/activities.json';

const cases: [string, ActivityConfig, keyof typeof en.validation, Record<string, string>?][] = [
  ["runScript", {}, "scriptRequired"],
  ["fileOperation",{},"pathRequired"],
  ["fileOperation",{"path":"x"},"destinationRequired"],
  ["folderOperation",{"path":"x","operation":"rename"},"newNameRequired"],
  ["fileOperation",{"path":"x","operation":"bogus"},"unknownOperationAllowed",{"operation":"bogus","allowed":"copy, move, delete, exists, create, rename"}],
  ["serviceManagement",{},"serviceNameRequired"],
  ["serviceManagement",{"serviceName":"s"},"actionRequired"],
  ["serviceManagement",{"serviceName":"s","action":"create"},"binaryPathRequired"],
  ["serviceManagement",{"serviceName":"s","action":"setStartType"},"startupTypeRequired"],
  ["registryOperation",{},"registryPathRequired"],
  ["registryOperation",{"keyPath":"x"},"operationRequired"],
  ["registryOperation",{"keyPath":"x","operation":"bogus"},"unknownOperation",{"operation":"bogus"}],
  ["registryOperation",{"keyPath":"x","operation":"write"},"registryWriteValueRequired"],
  ["registryOperation",{"keyPath":"x","operation":"write","valueName":"x","valueType":"bogus"},"unknownValueType",{"valueType":"bogus"}],
  ["registryOperation",{"keyPath":"x","operation":"deleteValue"},"registryDeleteValueRequired"],
  ["wmiQuery",{"mode":"wql"},"wqlQueryRequired"],
  ["wmiQuery",{},"wmiClassRequired"],
  ["wmiQuery",{"mode":"invokeMethod","className":"x"},"wmiMethodRequired"],
  ["startProgram",{},"startProgramPathRequired"],
  ["startProgram",{"filePath":"app.exe"},"startProgramNotAbsolute"],
  ["startProgram",{"filePath":"\\\\host\\share\\app.exe"},"startProgramUncPath"],
  ["powerManagement",{},"actionRequired"],
  ["restApi",{},"urlRequired"],
  ["sql",{},"sqlConnectionRequired"],
  ["sql",{"server":"x"},"sqlQueryRequired"],
  ["textFileEdit",{"path":"x","operation":"append"},"contentRequired",{"operation":"append"}],
  ["textFileEdit",{"path":"x","operation":"insert","content":"x"},"lineNumberRequired",{"operation":"insert"}],
  ["textFileEdit",{"path":"x","operation":"delete"},"deleteSelectorRequired"],
  ["textFileEdit",{"path":"x","operation":"delete","lineNumber":1,"matchPattern":"x"},"deleteSelectorExclusive"],
  ["textFileEdit",{"path":"x","operation":"replace"},"matchPatternRequired"],
  ["textFileEdit",{"path":"x","operation":"replace","matchPattern":"x"},"replacementRequired"],
  ["emailNotification",{},"recipientRequired"],
  ["emailNotification",{"to":"x"},"subjectRequired"],
  ["delay",{},"delayRequired"],
  ["delay",{"seconds":0},"delayPositive"],
  ["generateText",{"mode":"custom"},"customCharsetRequired"],
  ["llmQuery",{},"promptRequired"],
  ["llmQuery",{"prompt":"x","baseUrl":"ftp://x"},"baseUrlAbsolute"],
  ["llmQuery",{"prompt":"x","temperature":3},"temperatureRange"],
  ["llmQuery",{"prompt":"x","maxTokens":0},"maxTokensPositive"],
  ["xmlQuery",{},"pathOrContentRequired"],
  ["xmlQuery",{"path":"x"},"xpathRequired"],
  ["jsonQuery",{"content":"{}"},"jsonPathRequired"],
];

describe('activity validation translations', () => {
  it.each(['de', 'en'])('renders every validation branch in %s', async (language) => {
    const original = i18n.language;
    try {
      await i18n.changeLanguage(language);
      for (const [type, config, key, values] of cases) {
        expect(de.validation[key], key).toBeTruthy();
        expect(en.validation[key], key).toBeTruthy();
        expect(checkRequiredActivityConfig(type, config), type + ': ' + key)
          .toBe(i18n.t('activities:validation.' + key, values ?? {}));
      }
    } finally {
      await i18n.changeLanguage(original);
    }
  });
});
