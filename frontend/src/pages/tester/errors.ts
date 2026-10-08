import axios from 'axios';

/** Prefer the server's `message` (the controller's friendly 400 text) over axios's generic one. */
export function describeError(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as { message?: string; title?: string; errors?: Record<string, string[]> } | undefined;
    if (data?.message) return data.message;
    if (data?.errors) {
      const first = Object.values(data.errors).flat()[0];
      if (first) return first;
    }
    if (data?.title) return data.title;
    if (err.code === 'ECONNABORTED') return 'Таймаут запроса — попробуйте меньший период или более крупный таймфрейм';
    if (err.response?.status) return `Ошибка сервера (HTTP ${err.response.status})`;
  }
  return (err as Error)?.message || 'Ошибка симуляции';
}
