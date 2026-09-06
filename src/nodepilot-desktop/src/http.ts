/**
 * The subset of Electron's ClientRequest / IncomingMessage the status probe uses, declared here
 * so the deadline logic can be tested without an Electron runtime.
 */
export interface StatusResponse {
  statusCode: number;
  on(event: 'data', listener: (chunk: Buffer) => void): this;
  on(event: 'end', listener: () => void): this;
}

export interface StatusRequest {
  setHeader(name: string, value: string): void;
  on(event: 'response', listener: (response: StatusResponse) => void): this;
  on(event: 'error', listener: (error: Error) => void): this;
  write(chunk: string): void;
  end(): void;
  abort(): void;
}

export interface StatusRequestOptions {
  headers?: Record<string, string>;
  body?: string;
  /** Upper bound for the whole exchange. A backend that accepts the connection but never answers
   *  would otherwise hold the caller forever; Electron's request has no timeout of its own. */
  timeoutMs: number;
}

/** Sends the request and resolves with the HTTP status code, or rejects on error or deadline. */
export function requestStatus(request: StatusRequest, options: StatusRequestOptions): Promise<number> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      request.abort();
      reject(new Error(`No response within ${options.timeoutMs} ms.`));
    }, options.timeoutMs);
    const finish = (settle: () => void): void => {
      clearTimeout(timer);
      settle();
    };

    if (options.headers) {
      for (const [name, value] of Object.entries(options.headers)) request.setHeader(name, value);
    }
    request.on('response', (response) => {
      response.on('data', () => { /* drain */ });
      response.on('end', () => finish(() => resolve(response.statusCode)));
    });
    request.on('error', (error) => finish(() => reject(error)));
    if (options.body !== undefined) request.write(options.body);
    request.end();
  });
}
