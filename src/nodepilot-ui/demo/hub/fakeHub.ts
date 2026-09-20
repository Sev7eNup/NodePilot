/**
 * In-memory stand-in for the execution hub.
 *
 * It satisfies the contract `connectPersistently` documents — `state`, `start`, `stop`, `on`,
 * `invoke` and the three lifecycle hooks — and nothing else, because that is all the
 * consumers touch.
 *
 * Group semantics are reproduced exactly, and getting them wrong is the difference between a
 * dead canvas and a chatty one:
 *   - the workflow group carries only `ExecutionStatusChanged`
 *   - step events reach a client only after `JoinExecution`
 *   - the operations feed carries only `LiveEventsBatch`
 */
import type { HubConnection } from '@microsoft/signalr';
import type {
  ExecutionUpdate,
  StepCompletedEvent,
  StepPausedEvent,
  StepResumedEvent,
  StepStartedEvent,
} from '../../src/hooks/signalrTypes';

type Listener = (...args: unknown[]) => void;

const DISCONNECTED = 'Disconnected';
const CONNECTED = 'Connected';

class FakeHubConnection {
  state: string = DISCONNECTED;
  private readonly listeners = new Map<string, Listener[]>();
  readonly workflows = new Set<string>();
  readonly executions = new Set<string>();
  opsFeed = false;

  on(name: string, callback: Listener): void {
    const list = this.listeners.get(name) ?? [];
    list.push(callback);
    this.listeners.set(name, list);
  }

  off(name: string, callback?: Listener): void {
    if (!callback) { this.listeners.delete(name); return; }
    const list = (this.listeners.get(name) ?? []).filter((cb) => cb !== callback);
    this.listeners.set(name, list);
  }

  async invoke(method: string, ...args: unknown[]): Promise<void> {
    const first = typeof args[0] === 'string' ? args[0] : undefined;
    switch (method) {
      case 'JoinWorkflow': if (first) this.workflows.add(first); break;
      case 'JoinExecution': if (first) this.executions.add(first); break;
      case 'LeaveExecution': if (first) this.executions.delete(first); break;
      case 'JoinOperationsFeed': this.opsFeed = true; break;
      case 'LeaveOperationsFeed': this.opsFeed = false; break;
      default: break;
    }
  }

  async start(): Promise<void> {
    this.state = CONNECTED;
    connections.add(this);
  }

  async stop(): Promise<void> {
    this.state = DISCONNECTED;
    this.workflows.clear();
    this.executions.clear();
    this.opsFeed = false;
    connections.delete(this);
  }

  // The lifecycle hooks exist so `connectPersistently` finds them; the fake never drops.
  onclose(): void { /* the demo connection never closes on its own */ }
  onreconnecting(): void { /* never reconnects: it never disconnects */ }
  onreconnected(): void { /* never reconnects: it never disconnects */ }

  dispatch(name: string, payload: unknown): void {
    for (const listener of this.listeners.get(name) ?? []) listener(payload);
  }
}

const connections = new Set<FakeHubConnection>();

/** Factory installed over the real `HubConnectionBuilder` in the demo build. */
export function createFakeHubConnection(): HubConnection {
  return new FakeHubConnection() as unknown as HubConnection;
}

/** Drops every live connection. Used on world reset and by tests. */
export function resetFakeHub(): void {
  connections.clear();
}

/** Live connections, for assertions in tests. */
export function fakeHubConnectionCount(): number {
  return connections.size;
}

/**
 * One batch item. The payload key is `evt`, not `event`: the operations feed reads
 * `item.Event ?? item.evt` and would drop a lowercase `event` on the floor, leaving the
 * dashboard and Live-Ops on their polling interval instead of updating live. The designer's
 * reducer accepts all three spellings, which is why only the feed was affected.
 */
function batchItem(type: string, evt: unknown) {
  return { events: [{ type, evt }] };
}

/**
 * Execution-level status change. Goes to the workflow group and, wrapped in a batch, to the
 * operations feed — the two surfaces that learn about a run without having joined it.
 */
export function emitExecutionStatus(update: ExecutionUpdate): void {
  for (const connection of connections) {
    if (connection.workflows.has(update.workflowId)) {
      connection.dispatch('ExecutionStatusChanged', update);
    }
    if (connection.opsFeed) {
      connection.dispatch('LiveEventsBatch', batchItem('ExecutionStatusChanged', update));
    }
  }
}

type StepEvent =
  | { name: 'StepStarted'; event: StepStartedEvent }
  | { name: 'StepCompleted'; event: StepCompletedEvent }
  | { name: 'StepPaused'; event: StepPausedEvent }
  | { name: 'StepResumed'; event: StepResumedEvent };

/** Step-level event. Reaches only clients that joined this execution. */
export function emitStepEvent({ name, event }: StepEvent): void {
  for (const connection of connections) {
    if (connection.executions.has(event.executionId)) connection.dispatch(name, event);
  }
}
