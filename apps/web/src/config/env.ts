export const API_BASE = import.meta.env.VITE_API_BASE_URL ?? (import.meta.env.DEV ? 'http://127.0.0.1:8000/api' : '/api');

export const APP_VERSION = import.meta.env.VITE_APP_VERSION || 'development';
