const dateTime = new Intl.DateTimeFormat("ru-RU", {
  day: "2-digit",
  month: "2-digit",
  year: "numeric",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});

const time = new Intl.DateTimeFormat("ru-RU", { hour: "2-digit", minute: "2-digit", second: "2-digit" });

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  return dateTime.format(new Date(value));
}

/** Время, а для не сегодняшних дат — дата и время. */
export function formatMoment(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  const now = new Date();
  return date.toDateString() === now.toDateString() ? time.format(date) : dateTime.format(date);
}

/** Размер файла: «12 КБ», «3,4 МБ». */
export function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "—";
  if (bytes < 1024) return `${bytes} Б`;
  const units = ["КБ", "МБ", "ГБ"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toLocaleString("ru-RU", { maximumFractionDigits: value < 10 ? 1 : 0 })} ${units[unit]}`;
}

export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return "—";
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds} с`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} мин ${seconds % 60} с`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} ч ${minutes % 60} мин`;
  return `${Math.floor(hours / 24)} дн ${hours % 24} ч`;
}

export function durationBetween(start: string, end: string | null | undefined): string {
  return formatDuration((end ? new Date(end).getTime() : Date.now()) - new Date(start).getTime());
}

/** «через 3 мин» / «2 мин назад»; в пределах нескольких секунд — «только что» / «сейчас». */
export function relative(value: string | null | undefined): string {
  if (!value) return "";
  const diff = new Date(value).getTime() - Date.now();
  if (Math.abs(diff) < 5000) return diff >= 0 ? "сейчас" : "только что";
  const text = formatDuration(Math.abs(diff));
  return diff >= 0 ? `через ${text}` : `${text} назад`;
}

export function plural(n: number, one: string, few: string, many: string): string {
  const mod10 = n % 10;
  const mod100 = n % 100;
  if (mod10 === 1 && mod100 !== 11) return one;
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return few;
  return many;
}

export function preview(value: unknown, max = 80): string {
  if (value === null || value === undefined) return "—";
  const text = typeof value === "string" ? value : JSON.stringify(value);
  return text.length > max ? `${text.slice(0, max)}…` : text;
}
