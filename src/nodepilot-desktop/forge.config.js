// Electron Forge config. Produces an unpacked Windows app under out/ that the Inno Setup
// installer bundles into C:\Program Files\NodePilot\desktop. Electron 43.2.0 (Chromium + Node)
// is pinned in package.json and shipped in full for offline installs; auto-update is NOT used
// (updates go through the signed all-in-one installer). The primary step is `electron-forge package`.
module.exports = {
  packagerConfig: {
    name: 'NodePilot',
    executableName: 'NodePilot',
    appCopyright: 'NodePilot',
    // Windows app/exe icon supplied by the build script (Build-DesktopInstaller.ps1) as assets/icon.ico.
    icon: 'assets/icon',
    asar: true,
    // Only the runtime artefacts ship — never the TypeScript sources or dev config.
    ignore: [
      /^\/src\//,
      /^\/scripts\//,
      /^\/tsconfig\.json$/,
      /^\/forge\.config\.js$/,
      /^\/\.gitignore$/
    ]
  },
  makers: [
    { name: '@electron-forge/maker-zip', platforms: ['win32'] }
  ]
};
