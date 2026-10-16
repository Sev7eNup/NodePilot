import { FuseState, FuseV1Options, FuseVersion, flipFuses, getCurrentFuseWire } from '@electron/fuses';

/**
 * Fuse settings for the packaged executable: no Node mode, no NODE_OPTIONS or inspector flags,
 * the app loads only from the integrity-checked app.asar, and cookies are encrypted at rest.
 * Strict mode fails packaging when an Electron upgrade adds a fuse that has no setting here yet.
 */
export const ELECTRON_FUSES = {
  version: FuseVersion.V1,
  strictlyRequireAllFuses: true,
  [FuseV1Options.RunAsNode]: false,
  [FuseV1Options.EnableCookieEncryption]: true,
  [FuseV1Options.EnableNodeOptionsEnvironmentVariable]: false,
  [FuseV1Options.EnableNodeCliInspectArguments]: false,
  [FuseV1Options.EnableEmbeddedAsarIntegrityValidation]: true,
  [FuseV1Options.OnlyLoadAppFromAsar]: true,
  [FuseV1Options.LoadBrowserProcessSpecificV8Snapshot]: false,
  [FuseV1Options.GrantFileProtocolExtraPrivileges]: false,
  [FuseV1Options.WasmTrapHandlers]: true,
};

/**
 * Writes the fuses into the packaged executable and reads them back. Runs before signing,
 * because changing the binary invalidates a signature.
 */
export async function hardenElectronFuses(executablePath) {
  await flipFuses(executablePath, ELECTRON_FUSES);

  const wire = await getCurrentFuseWire(executablePath);
  for (const option of Object.values(FuseV1Options).filter((value) => typeof value === 'number')) {
    const expected = ELECTRON_FUSES[option] ? FuseState.ENABLE : FuseState.DISABLE;
    if (wire[option] !== expected) {
      throw new Error(`Electron fuse ${FuseV1Options[option]} did not take the configured value.`);
    }
  }
}
