import { SERVICE_NAME } from './config';

/**
 * Builds the PowerShell command that restarts the API service through an elevated (UAC)
 * PowerShell. The service name is interpolated bare: it is validated to a quote-free charset, and
 * quoting it inside the already single-quoted inner command would end that string early and make
 * the parser reject the whole line.
 */
export function restartServiceCommand(serviceName: string): string {
  if (!SERVICE_NAME.test(serviceName)) {
    throw new Error('Service name contains unsupported characters.');
  }
  const inner = `Restart-Service -Name ${serviceName} -Force`;
  return `Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-Command','${inner}')`;
}
