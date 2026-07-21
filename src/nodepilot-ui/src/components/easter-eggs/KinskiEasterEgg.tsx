import { useEffect } from 'react';

interface Props {
  onClose: () => void;
}

const GRAIN_SVG = '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="200"><filter id="n"><feTurbulence type="fractalNoise" baseFrequency="0.75" numOctaves="4" stitchTiles="stitch"/></filter><rect width="100%" height="100%" filter="url(#n)"/></svg>';
const GRAIN_URL = `url("data:image/svg+xml,${encodeURIComponent(GRAIN_SVG)}")`;

const CSS = `
@keyframes kinski-flash {
  0%   { background: rgba(160,0,0,0.5); }
  40%  { background: rgba(0,0,0,0.55);  }
  100% { background: rgba(0,0,0,0.55);  }
}
@keyframes kinski-slam {
  0%   { transform: scale(2.4) rotate(-5deg); opacity: 0; }
  35%  { transform: scale(0.9) rotate(1.5deg); opacity: 1; }
  55%  { transform: scale(1.05) rotate(-0.5deg); }
  72%  { transform: scale(0.97) rotate(0.3deg); }
  88%  { transform: scale(1.01) rotate(-0.1deg); }
  100% { transform: scale(1) rotate(0deg); }
}
@keyframes kinski-grain-flicker {
  0%   { opacity: 0.05; }
  33%  { opacity: 0.12; }
  66%  { opacity: 0.07; }
  100% { opacity: 0.10; }
}
`;

export function KinskiEasterEgg({ onClose }: Readonly<Props>) {
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    globalThis.addEventListener('keydown', handler);
    return () => globalThis.removeEventListener('keydown', handler);
  }, [onClose]);

  return (
    <div
      className="fixed inset-0 z-[9999] flex items-center justify-center cursor-pointer overflow-hidden"
      style={{ animation: 'kinski-flash 0.45s ease forwards', backdropFilter: 'blur(8px)' }}
      onClick={onClose}
      onKeyDown={(e) => e.key === 'Escape' && onClose()}
      role="presentation"
      tabIndex={-1}
    >
      <style>{CSS}</style>

      {/* Vignette overlay */}
      <div
        className="absolute inset-0 pointer-events-none z-10"
        style={{ background: 'radial-gradient(ellipse at 50% 50%, transparent 30%, rgba(0,0,0,0.88) 100%)' }}
      />

      {/* Film grain overlay */}
      <div
        className="absolute inset-0 pointer-events-none z-20"
        style={{
          backgroundImage: GRAIN_URL,
          backgroundSize: '200px 200px',
          mixBlendMode: 'overlay',
          animation: 'kinski-grain-flicker 0.13s linear infinite',
        }}
      />

      {/* slam animation */}
      <div style={{ animation: 'kinski-slam 0.6s ease forwards', position: 'relative', zIndex: 5 }}>
        <img
          src="/kinskimeme.jpg"
          alt="Aufgeben kannst du bei der Post."
          draggable={false}
          style={{
            maxWidth: 'min(90vw, 580px)',
            maxHeight: '84vh',
            objectFit: 'contain',
            filter: 'contrast(1.12) saturate(0.88) brightness(0.93)',
            boxShadow: '0 0 40px rgba(180,0,0,0.5), 0 8px 60px rgba(0,0,0,0.9)',
            display: 'block',
            userSelect: 'none',
          }}
        />
      </div>
    </div>
  );
}
