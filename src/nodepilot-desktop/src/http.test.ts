import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { requestStatus, type StatusRequest, type StatusResponse } from './http';

class FakeResponse implements StatusResponse {
  private onEnd: (() => void) | undefined;

  constructor(public statusCode: number) {}

  on(event: 'data' | 'end', listener: (chunk?: Buffer) => void): this {
    if (event === 'end') this.onEnd = listener;
    return this;
  }

  finish(): void {
    this.onEnd?.();
  }
}

class FakeRequest implements StatusRequest {
  headers: Record<string, string> = {};
  body: string | undefined;
  ended = false;
  aborted = false;
  private onResponse: ((response: StatusResponse) => void) | undefined;
  private onError: ((error: Error) => void) | undefined;

  setHeader(name: string, value: string): void {
    this.headers[name] = value;
  }

  on(event: 'response' | 'error', listener: (argument: never) => void): this {
    if (event === 'response') this.onResponse = listener as (response: StatusResponse) => void;
    else this.onError = listener as (error: Error) => void;
    return this;
  }

  write(chunk: string): void {
    this.body = chunk;
  }

  end(): void {
    this.ended = true;
  }

  abort(): void {
    this.aborted = true;
  }

  respond(statusCode: number): void {
    const response = new FakeResponse(statusCode);
    this.onResponse?.(response);
    response.finish();
  }

  fail(error: Error): void {
    this.onError?.(error);
  }
}

describe('requestStatus', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('resolves with the status code and cancels the deadline once the response ended', async () => {
    const request = new FakeRequest();
    const pending = requestStatus(request, { timeoutMs: 1000 });

    request.respond(503);
    await expect(pending).resolves.toBe(503);

    vi.advanceTimersByTime(5000);
    expect(request.aborted).toBe(false);
  });

  it('aborts the request and rejects when nothing answers within the deadline', async () => {
    const request = new FakeRequest();
    const pending = requestStatus(request, { timeoutMs: 1000 });

    vi.advanceTimersByTime(1000);

    await expect(pending).rejects.toThrow(/No response within 1000 ms/);
    expect(request.aborted).toBe(true);
  });

  it('rejects with the transport error', async () => {
    const request = new FakeRequest();
    const pending = requestStatus(request, { timeoutMs: 1000 });

    request.fail(new Error('ECONNREFUSED'));

    await expect(pending).rejects.toThrow('ECONNREFUSED');
  });

  it('sends headers and body and ends the request', async () => {
    const request = new FakeRequest();
    const pending = requestStatus(request, {
      headers: { 'Content-Type': 'application/json', 'X-Setup-Token': 't' },
      body: '{"username":"admin"}',
      timeoutMs: 1000,
    });

    expect(request.headers).toEqual({ 'Content-Type': 'application/json', 'X-Setup-Token': 't' });
    expect(request.body).toBe('{"username":"admin"}');
    expect(request.ended).toBe(true);

    request.respond(200);
    await expect(pending).resolves.toBe(200);
  });
});
