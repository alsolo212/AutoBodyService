import { kyivTime } from '../../../shared/time/kyiv-time';

const zone = 'Europe/Kyiv';
const partsFormatter = new Intl.DateTimeFormat('en-GB', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
const offsetFormatter = new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' });

export function kyivInput(value: string): { local: string; offset: string } {
  const instant = new Date(value);
  const parts = partsFormatter.formatToParts(instant);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(item => item.type === type)?.value ?? '';
  return { local: `${part('year')}-${part('month')}-${part('day')}T${part('hour')}:${part('minute')}:${part('second')}`,
    offset: offsetFormatter.formatToParts(instant).find(item => item.type === 'timeZoneName')?.value.replace('GMT', '') ?? '' };
}

/** Rejects invalid local times and mismatched offsets, including DST gaps. */
export function kyivLocalToUtc(local: string, offset: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?$/.test(local) || !['+02:00', '+03:00'].includes(offset)) return null;
  const normalized = local.length === 16 ? `${local}:00` : local;
  const instant = new Date(`${normalized}${offset}`);
  if (!Number.isFinite(instant.getTime())) return null;
  const actual = kyivInput(instant.toISOString());
  return actual.local === normalized && actual.offset === offset ? instant.toISOString() : null;
}

/** Possible offsets for a Kyiv wall time; none means the time does not exist. */
export function kyivOffsets(local: string): string[] {
  return ['+03:00', '+02:00'].filter(offset => kyivLocalToUtc(local, offset) !== null);
}

export function kyivWithOffset(value: string): string {
  return kyivTime(value, true);
}
