import { api } from './client';

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
}

/** Reads every server page. Array handling keeps isolated UI mocks backwards-compatible. */
export async function getAllPages<T>(path: string, pageSize = 200): Promise<T[]> {
  const separator = path.includes('?') ? '&' : '?';
  const first = await api.get<PagedResponse<T> | T[]>(
    `${path}${separator}page=1&pageSize=${pageSize}`,
  );
  if (Array.isArray(first)) return first;

  const result = [...first.items];
  for (let page = 2; page <= first.totalPages; page += 1) {
    const next = await api.get<PagedResponse<T>>(
      `${path}${separator}page=${page}&pageSize=${pageSize}`,
    );
    result.push(...next.items);
  }
  return result;
}
