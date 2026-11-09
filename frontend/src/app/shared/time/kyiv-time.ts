const zone = 'Europe/Kyiv';
const keyFormatter = new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
const offsetFormatter = new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' });

/** Add the offset when Kyiv's local time occurs twice during the autumn transition. */
export function kyivTime(value: string, withDate = false): string {
  const instant = new Date(value);
  const formatted = new Intl.DateTimeFormat('uk-UA', {
    timeZone: zone, ...(withDate ? { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' } : { hour: '2-digit', minute: '2-digit' }),
  }).format(instant);
  const time = instant.getTime();
  const key = keyFormatter.format(instant);
  const ambiguous = [-3600000, 3600000].some(delta => keyFormatter.format(new Date(time + delta)) === key);
  if (!ambiguous) return formatted;
  const offset = offsetFormatter.formatToParts(instant).find(part => part.type === 'timeZoneName')?.value.replace('GMT', 'UTC');
  return `${formatted} (${offset})`;
}
