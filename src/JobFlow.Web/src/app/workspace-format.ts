export const runStatusLabels: Record<string, string> = {
  Running: 'Выполняется', Succeeded: 'Завершён', Failed: 'Ошибка выполнения',
  Interrupted: 'Прерван', NeedsAttention: 'Завершён, нужна проверка', Cancelled: 'Отменён',
};

export function workspaceTime(value: string, compact = false): string {
  if (!value) return '—';
  const zoned = /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : value + 'Z';
  const date = new Date(zoned);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('ru-RU', {dateStyle: 'short', timeStyle: compact ? 'short' : 'medium'});
}
