// Deterministic demo response for the real global AI Chat UI. No LLM is contacted.
export const demoQuestion = 'What happened in the latest fleet health check?';
export const demoAnswer = `The latest **Morning Fleet Health Check** completed successfully.

| Activity | Result |
| --- | --- |
| Windows service | WinRM is running |
| PowerShell check | C: has 142.6 GB free (57.1%) |
| File Copy | Daily log archived to D:\\OpsArchive |
| LLM Query | Operations summary generated |

**No action required.** Open the execution history to inspect step timings and output.`;

export function demoChatStream(request) {
  if (request.question !== demoQuestion) throw new Error('Unexpected demo chat question');
  const event = (name, data) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`;
  return event('delta', { text: demoAnswer }) + event('done', { model: 'Demo response', durationMs: 0 });
}
