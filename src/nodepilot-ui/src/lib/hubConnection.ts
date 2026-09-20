import * as signalR from '@microsoft/signalr';
import type { HubConnection } from '@microsoft/signalr';
import { readCsrfToken } from '../api/csrf';

/**
 * Single place the execution hub connection is built.
 *
 * Both consumers (`useSignalR`, `useLiveOpsFeed`) used to construct an identical builder
 * chain. Routing them through one factory also gives non-browser hosts a seam: the factory is
 * replaceable, so a caller can supply an in-memory hub without `src/` ever importing it.
 * The dependency only ever points inward.
 */
export type ExecutionHubFactory = () => HubConnection;

const defaultFactory: ExecutionHubFactory = () =>
  // The httpOnly np_auth cookie travels on the negotiate POST and the WebSocket upgrade
  // automatically (same-origin, `withCredentials` default for SignalR browser transport).
  new signalR.HubConnectionBuilder()
    .withUrl('/hubs/execution', { headers: { 'X-CSRF-Token': readCsrfToken() } })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();

let factory: ExecutionHubFactory = defaultFactory;

export function createExecutionHubConnection(): HubConnection {
  return factory();
}

/** Replaces the factory. Restores the real builder when called with no argument. */
export function setExecutionHubFactory(next?: ExecutionHubFactory): void {
  factory = next ?? defaultFactory;
}
