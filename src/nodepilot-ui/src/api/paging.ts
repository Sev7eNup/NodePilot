import { api } from './client';

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
}

function withPaging(path: string, page: number, pageSize: number): string {
  const separator = path.includes('?') ? '&' : '?';
  return `${path}${separator}page=${page}&pageSize=${pageSize}`;
}

/** Reads one server page. Array handling keeps isolated UI mocks backwards-compatible. */
export async function getPage<T>(path: string, page = 1, pageSize = 200): Promise<PagedResponse<T>> {
  const response = await api.get<PagedResponse<T> | T[]>(withPaging(path, page, pageSize));
  if (!Array.isArray(response)) return response;

  return {
    items: response,
    page: 1,
    pageSize,
    total: response.length,
    totalPages: response.length > 0 ? 1 : 0,
  };
}

/** Reads every server page. Array handling keeps isolated UI mocks backwards-compatible. */
export async function getAllPages<T>(path: string, pageSize = 200): Promise<T[]> {
  const first = await getPage<T>(path, 1, pageSize);

  const result = [...first.items];
  for (let page = 2; page <= first.totalPages; page += 1) {
    const next = await getPage<T>(path, page, pageSize);
    result.push(...next.items);
  }
  return result;
}
