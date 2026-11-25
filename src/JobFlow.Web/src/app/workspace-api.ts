import type { Row } from './workspace-sections';

export async function requestApi(path: string, method = 'GET', body?: unknown, signal?: AbortSignal) {
  const headers: Record<string, string> = {'X-JobFlow-Client': 'local-ui'};
  const form = body instanceof FormData;
  if (body !== undefined && !form) headers['Content-Type'] = 'application/json';
  const response = await fetch('/api/' + path, {
    method, headers, signal,
    body: body === undefined ? undefined : form ? body : JSON.stringify(body),
  });
  const text = await response.text();
  let result;
  try {
    result = text ? JSON.parse(text) : null;
  } catch {
    throw new Error('Неожиданный ответ API. Проверьте сервер.');
  }
  if (!response.ok) throw new Error(result?.error ?? result?.detail ?? 'Ошибка сервера: ' + response.status);
  return result;
}

export async function readCollections(keys: string[], signal: AbortSignal) {
  const deadline = AbortSignal.any([signal, AbortSignal.timeout(30000)]);
  const results = await Promise.allSettled(keys.map(key =>
    requestApi(key === 'vacancies' ? 'vacancies?includeArchived=true' : key, 'GET', undefined, deadline)));
  const records: Record<string, Row[]> = {};
  const failed: string[] = [];
  results.forEach((result, index) => {
    const key = keys[index];
    if (result.status === 'fulfilled' && Array.isArray(result.value)) records[key] = result.value;
    else failed.push(key);
  });
  return {records, failed};
}
