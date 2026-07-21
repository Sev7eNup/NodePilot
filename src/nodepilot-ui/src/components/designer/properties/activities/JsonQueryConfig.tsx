import { Code, DataBase } from '@carbon/icons-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ConfigProps } from '../shared';
import { StructuredQueryConfig } from './structuredQueryShared';
import { JsonPathTree } from '../JsonPathTree';
import { tryParseJson } from '../../../../lib/jsonPathBuilder';
import type { UpstreamVariable } from '../../../../lib/upstreamVariables';

type JsonCandidate = { variable: UpstreamVariable; parsed: unknown };

export function JsonQueryConfig(props: Readonly<ConfigProps>) {
  const { t } = useTranslation('properties');
  const extraFields = (
    <JsonPathPicker
      config={props.config}
      onUpdate={props.onUpdate}
      upstreamVars={props.upstreamVars ?? []}
      lastStepsByStepId={props.lastStepsByStepId}
    />
  );

  return (
    <StructuredQueryConfig
      {...props}
      language="json"
      contentLabel={t('config.structuredQuery.jsonContent')}
      filePlaceholder="C:\\Data\\input.json"
      queryField="jsonPath"
      queryLabel={t('config.structuredQuery.jsonPathLabel')}
      queryPlaceholder="$.items[0].name"
      extraFields={extraFields}
    />
  );
}

function JsonPathPicker({
  config,
  onUpdate,
  upstreamVars,
  lastStepsByStepId,
}: Readonly<Pick<ConfigProps, 'config' | 'onUpdate' | 'upstreamVars' | 'lastStepsByStepId'>>) {
  const { t } = useTranslation('properties');
  const candidates = useMemo(() => {
    const result: JsonCandidate[] = [];
    for (const variable of upstreamVars ?? []) {
      if (!variable.variable.endsWith('.output')) continue;
      const parsed = tryParseJson(lastStepsByStepId?.get(variable.stepId)?.output);
      if (parsed !== null) result.push({ variable, parsed });
    }
    return result;
  }, [upstreamVars, lastStepsByStepId]);
  const [selectedExpression, setSelectedExpression] = useState<string>(() => {
    const content = (config.content as string) || '';
    return content.startsWith('{{') ? content : '';
  });
  const selected = candidates.find((c) => c.variable.expression === selectedExpression) ?? candidates[0] ?? null;

  if (candidates.length === 0) {
    return (
      <div className="rounded-md border border-outline-variant/30 bg-surface-high/50 p-2 text-[11px] font-label text-on-surface-variant">
        {t('config.structuredQuery.jsonPickerEmpty')}
      </div>
    );
  }

  const pickPath = (jsonPath: string) => {
    if (!selected) return;
    onUpdate({
      source: 'inline',
      content: selected.variable.expression,
      jsonPath,
    });
  };

  return (
    <div className="space-y-2 rounded-md border border-outline-variant/30 bg-surface-container p-2">
      <div className="flex items-center gap-2">
        <DataBase size={12} className="text-primary/70 shrink-0" />
        <span className="text-[10px] font-label font-bold uppercase tracking-widest text-on-surface-variant">
          {t('config.structuredQuery.jsonPickerTitle')}
        </span>
      </div>
      <div className="flex items-center gap-2">
        <Code size={12} className="text-on-surface-variant shrink-0" />
        <select
          value={selected?.variable.expression ?? ''}
          onChange={(e) => setSelectedExpression(e.target.value)}
          className="input-field text-xs"
        >
          {candidates.map((candidate) => (
            <option key={candidate.variable.expression} value={candidate.variable.expression}>
              {candidate.variable.label}
            </option>
          ))}
        </select>
      </div>
      {selected && <JsonPathTree value={selected.parsed} onPick={pickPath} />}
      <p className="text-[10px] font-label text-on-surface-variant">
        {t('config.structuredQuery.jsonPickerHint')}
      </p>
    </div>
  );
}
