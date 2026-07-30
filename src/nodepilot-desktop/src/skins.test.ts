import { describe, it, expect } from 'vitest';
import { join } from 'node:path';
import { defaultIcons, skinFromFaviconUrl, skinIcons, skinIconsForFavicons } from './skins';

const ASSETS = join('C:', 'app', 'assets');
const skinPath = (name: string) => join(ASSETS, 'skins', name);

/** Stand-in for a generated icon set covering exactly these skins. */
const generatedFor = (...skins: string[]) => {
  const files = new Set(skins.flatMap((s) => [skinPath(`${s}.png`), skinPath(`${s}-tray.png`)]));
  return (path: string) => files.has(path);
};

describe('defaultIcons', () => {
  it('points at the build-time default pair next to the app', () => {
    expect(defaultIcons(ASSETS)).toEqual({
      window: join(ASSETS, 'icon.png'),
      tray: join(ASSETS, 'tray.png'),
    });
  });
});

describe('skinFromFaviconUrl', () => {
  it.each([
    ['https://localhost:5001/appicon-dark.png', 'dark'],
    ['https://localhost:5001/appicon-light.png', 'light'],
    ['https://localhost:5001/appicon-dark-sparkasse.png', 'dark-sparkasse'],
    ['https://127.0.0.1:5001/sub/path/appicon-dark-nebula.png?v=2', 'dark-nebula'],
  ])('extracts the skin id from %s', (url, expected) => {
    expect(skinFromFaviconUrl(url)).toBe(expected);
  });

  it.each([
    ['https://localhost:5001/appicon.png'],          // untinted source art, not a skin
    ['https://localhost:5001/favicon.svg'],
    ['https://localhost:5001/appicon-.png'],
    ['https://localhost:5001/appicon-Dark.png'],     // ids are lowercase
    ['https://localhost:5001/appicon-dark.png.exe'],
    ['not a url'],
    [''],
  ])('rejects %s', (url) => {
    expect(skinFromFaviconUrl(url)).toBeNull();
  });

  it('rejects a traversal attempt smuggled into the favicon path', () => {
    expect(skinFromFaviconUrl('https://localhost:5001/appicon-..%2F..%2Fevil.png')).toBeNull();
  });
});

describe('skinIcons', () => {
  it('resolves both sizes of a generated skin', () => {
    expect(skinIcons(ASSETS, 'dark-lila', generatedFor('dark-lila'))).toEqual({
      window: skinPath('dark-lila.png'),
      tray: skinPath('dark-lila-tray.png'),
    });
  });

  it('returns null for a skin this build has no icons for', () => {
    expect(skinIcons(ASSETS, 'dark-nebula', generatedFor('dark'))).toBeNull();
  });

  it('returns null when only one of the two sizes exists', () => {
    expect(skinIcons(ASSETS, 'dark', (p) => p === skinPath('dark.png'))).toBeNull();
  });

  it('rejects an id outside the allowed charset before touching the filesystem', () => {
    expect(skinIcons(ASSETS, '../../../windows/system32/evil', () => true)).toBeNull();
    expect(skinIcons(ASSETS, 'DARK', () => true)).toBeNull();
    expect(skinIcons(ASSETS, '', () => true)).toBeNull();
  });
});

describe('skinIconsForFavicons', () => {
  it('takes the first favicon that resolves to a generated skin', () => {
    const icons = skinIconsForFavicons(
      ASSETS,
      [
        'https://localhost:5001/favicon.svg',
        'https://localhost:5001/appicon-dark-nebula.png',
        'https://localhost:5001/appicon-dark.png',
      ],
      generatedFor('dark', 'dark-nebula'),
    );
    expect(icons).toEqual({ window: skinPath('dark-nebula.png'), tray: skinPath('dark-nebula-tray.png') });
  });

  it('skips a known-but-ungenerated skin and keeps looking', () => {
    const icons = skinIconsForFavicons(
      ASSETS,
      ['https://localhost:5001/appicon-dark-nebula.png', 'https://localhost:5001/appicon-light.png'],
      generatedFor('light'),
    );
    expect(icons).toEqual({ window: skinPath('light.png'), tray: skinPath('light-tray.png') });
  });

  it('returns null for an empty report, so the current icon stays', () => {
    expect(skinIconsForFavicons(ASSETS, [], () => true)).toBeNull();
  });

  it('returns null when nothing in the report is a skin icon', () => {
    expect(skinIconsForFavicons(ASSETS, ['https://localhost:5001/favicon.ico'], () => true)).toBeNull();
  });
});
