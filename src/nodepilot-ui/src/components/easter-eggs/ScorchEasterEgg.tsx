import { useEffect } from 'react';

interface Props {
  onClose: () => void;
}

const CSS = `
@keyframes anzeige-fade-bg {
  from { background: rgba(0,0,0,0); }
  to   { background: rgba(30,20,10,0.72); }
}
@keyframes anzeige-slam {
  0%   { transform: scale(1.22) translateY(-28px); opacity: 0; filter: blur(3px); }
  38%  { transform: scale(0.97) translateY(4px);   opacity: 1; filter: blur(0);   }
  58%  { transform: scale(1.013) translateY(-1px); }
  78%  { transform: scale(0.998) translateY(0.5px); }
  100% { transform: scale(1) translateY(0); }
}
@keyframes flame-flicker {
  0%   { transform: scaleY(1)    skewX(0deg);   opacity: 0.95; }
  25%  { transform: scaleY(1.06) skewX(2.5deg); opacity: 1;    }
  55%  { transform: scaleY(0.93) skewX(-2deg);  opacity: 0.88; }
  80%  { transform: scaleY(1.03) skewX(1deg);   opacity: 0.97; }
  100% { transform: scaleY(1)    skewX(0deg);   opacity: 0.95; }
}
`;

function CandleSvg() {
  return (
    <svg
      viewBox="0 0 24 64"
      style={{ width: 22, height: 64, overflow: 'visible', flexShrink: 0 }}
      aria-hidden="true"
    >
      <g style={{ animation: 'flame-flicker 1.1s ease-in-out infinite', transformOrigin: '12px 10px' }}>
        <ellipse cx="12" cy="8"  rx="4"   ry="6"   fill="#F97316" opacity="0.9" />
        <ellipse cx="12" cy="10" rx="2.2" ry="3.5" fill="#FCD34D" opacity="0.85" />
        <ellipse cx="12" cy="12" rx="1.1" ry="1.8" fill="#FFFBEB" opacity="0.75" />
      </g>
      <line x1="12" y1="14" x2="12" y2="19" stroke="#5a4a2a" strokeWidth="1" />
      <rect x="7" y="18" width="10" height="34" rx="1.5" fill="#f5efe2" stroke="#d6caa8" strokeWidth="0.6" />
      <path d="M8.5 22 Q7.5 28 8.5 33" stroke="#ede4cc" strokeWidth="2" fill="none" opacity="0.55" />
      <ellipse cx="12" cy="52" rx="8" ry="2.5" fill="#d6caa8" />
      <rect x="4" y="50" width="16" height="5" rx="1" fill="#c8bc96" />
    </svg>
  );
}

function TombstoneSvg() {
  return (
    <svg
      viewBox="0 0 90 110"
      style={{ width: 90, height: 110 }}
      aria-hidden="true"
    >
      {/* Tombstone body with a rounded arch on top */}
      <path
        d="M10 50 L10 95 L80 95 L80 50 Q80 10 45 10 Q10 10 10 50Z"
        fill="#e8e4dc"
        stroke="#9a8a6a"
        strokeWidth="1.5"
      />
      {/* Base */}
      <rect x="4" y="93" width="82" height="12" rx="2" fill="#d4c9a8" stroke="#9a8a6a" strokeWidth="1" />
      {/* Cross on the stone */}
      <rect x="41.5" y="28" width="7" height="28" fill="#9a8a6a" />
      <rect x="29"   y="36" width="32" height="7"  fill="#9a8a6a" />
    </svg>
  );
}

export function ScorchEasterEgg({ onClose }: Readonly<Props>) {
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    globalThis.addEventListener('keydown', handler);
    return () => globalThis.removeEventListener('keydown', handler);
  }, [onClose]);

  return (
    <div
      className="fixed inset-0 z-[9999] flex items-center justify-center cursor-pointer overflow-auto py-10 px-4"
      style={{ animation: 'anzeige-fade-bg 0.5s ease forwards', background: 'rgba(30,20,10,0.72)' }}
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <style>{CSS}</style>

      {/* Obituary card */}
      <div
        style={{
          animation: 'anzeige-slam 0.65s cubic-bezier(0.25,0.46,0.45,0.94) forwards',
          background: '#fdfaf5',
          border: '1.5px solid #1a1209',
          boxShadow:
            'inset 0 0 0 6px #fdfaf5, inset 0 0 0 8px #1a1209, 0 24px 70px rgba(0,0,0,0.55)',
          maxWidth: 460,
          width: 'min(92vw, 460px)',
          padding: '36px 42px 28px',
          textAlign: 'center',
          fontFamily: 'Georgia, "Palatino Linotype", "Times New Roman", serif',
          color: '#1a1209',
          lineHeight: 1,
        }}
      >
        {/* Large cross */}
        <div style={{ fontSize: 36, marginBottom: 18, letterSpacing: '0.05em', opacity: 0.85 }}>✝</div>

        {/* Opening verse */}
        <p style={{
          fontSize: 12.5,
          fontStyle: 'italic',
          lineHeight: 1.85,
          marginBottom: 22,
          color: '#3a2e1a',
        }}>
          Als Gott sah, dass er von vielen gehasst,<br />
          von wenigen geliebt<br />
          und von der Welt nie wirklich verstanden wurde,<br />
          legte er seinen Arm um ihn und sprach:<br />
          <span style={{ fontStyle: 'normal', fontWeight: 600 }}>
            „Komm heim. Du warst nie production-ready."
          </span>
        </p>

        {/* Divider */}
        <div style={{ borderTop: '0.5px solid #9a8a6a', margin: '0 16px 22px' }} />

        {/* Name */}
        <h1 style={{
          fontSize: 36,
          fontWeight: 700,
          letterSpacing: '0.14em',
          marginBottom: 8,
          lineHeight: 1.1,
        }}>
          SCORCH
        </h1>
        <p style={{ fontSize: 13, marginBottom: 14, letterSpacing: '0.04em', color: '#3a2e1a' }}>
          Microsoft System Center Orchestrator
        </p>

        {/* Life dates (born/died) */}
        <p style={{ fontSize: 12.5, letterSpacing: '0.14em', marginBottom: 22, color: '#666' }}>
          * Oktober 2012 &nbsp;&nbsp;&nbsp;&nbsp; † 2026
        </p>

        {/* Divider */}
        <div style={{ borderTop: '0.5px solid #9a8a6a', margin: '0 16px 22px' }} />

        {/* Tombstone SVG flanked by candles */}
        <div style={{
          display: 'flex',
          alignItems: 'flex-end',
          justifyContent: 'center',
          gap: 24,
          marginBottom: 22,
        }}>
          <CandleSvg />
          <TombstoneSvg />
          <CandleSvg />
        </div>

        {/* Central epitaph line */}
        <p style={{ fontSize: 13.5, fontStyle: 'italic', marginBottom: 18, lineHeight: 1.75, color: '#1a1209' }}>
          Von vielen gehasst, von wenigen geliebt,<br />
          von niemandem vermisst.
        </p>

        {/* Mourners */}
        <p style={{ fontSize: 12, lineHeight: 1.95, color: '#3a2e1a', marginBottom: 20 }}>
          Er hinterlässt tausende XML-Runbooks,<br />
          viele frustrierte IT-Admins<br />
          und unzählige Bugs im Runbook Designer.
        </p>

        {/* Divider */}
        <div style={{ borderTop: '0.5px solid #9a8a6a', margin: '0 16px 18px' }} />

        {/* Signature */}
        <p style={{ fontSize: 12, lineHeight: 1.9, color: '#3a2e1a' }}>
          In stiller Erleichterung:<br />
          <span style={{ fontStyle: 'italic' }}>
            Die OE-5161<br />
            und alle, die nie wieder VBScript schreiben müssen.
          </span>
        </p>

        {/* Close hint */}
        <p style={{ fontSize: 10, color: '#aaa', marginTop: 20, letterSpacing: '0.06em' }}>
          Klick oder ESC zum Schließen
        </p>
      </div>
    </div>
  );
}
