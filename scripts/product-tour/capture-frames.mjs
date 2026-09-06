import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

// Record lossless PNG frames with elapsed capture timestamps. This avoids
// a lossy VP8 intermediate before the final H.264 export and preserves quiet pauses.
export async function startFrameCapture(page, out, name) {
  const directory = path.join(out, 'raw', `${name}-frames`);
  await mkdir(directory, { recursive: true });
  await page.screenshot({ path: path.join(directory, '000000.png') });
  const frames = [{ name: '000000.png', at: 0 }];
  const pending = new Set();
  const failures = [];
  let stopping = false;
  let counter = 0;
  const session = await page.context().newCDPSession(page);
  const started = performance.now();
  session.on('Page.screencastFrame', ({ data, sessionId }) => {
    if (stopping) return;
    const filename = `${String(++counter).padStart(6, '0')}.png`;
    frames.push({ name: filename, at: (performance.now() - started) / 1000 });
    const save = writeFile(path.join(directory, filename), Buffer.from(data, 'base64'))
      .catch(error => failures.push(error))
      .finally(() => pending.delete(save));
    pending.add(save);
    const ack = session.send('Page.screencastFrameAck', { sessionId })
      .catch(error => { if (!stopping) failures.push(error); })
      .finally(() => pending.delete(ack));
    pending.add(ack);
  });
  await session.send('Page.startScreencast', {
    format: 'png', maxWidth: 2560, maxHeight: 1440, everyNthFrame: 1,
  });
  return {
    started,
    async stop(duration) {
      stopping = true;
      await session.send('Page.stopScreencast');
      await Promise.all(pending);
      await session.detach();
      if (failures.length) throw failures[0];
      const used = frames.filter(frame => frame.at < duration);
      const lines = ['ffconcat version 1.0'];
      used.forEach((frame, i) => {
        const next = used[i + 1]?.at ?? duration;
        lines.push(`file '${name}-frames/${frame.name}'`, 'option framerate 1000', `duration ${(next - frame.at).toFixed(6)}`);
      });
      // A final repeated frame lets concat honour the preceding frame's duration.
      lines.push(`file '${name}-frames/${used.at(-1).name}'`, 'option framerate 1000');
      await writeFile(path.join(out, 'raw', `${name}.ffconcat`), lines.join('\n') + '\n');
      return { frames: used.length, duration, capture: 'lossless-png', width: 2560, height: 1440 };
    },
  };
}
