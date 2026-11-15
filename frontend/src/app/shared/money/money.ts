// Decimal is kept as text until it is inserted as a validated JSON number.
export function decimalLiteral(value: string): string | null {
  const input = value.trim().replace(',', '.');
  if (!/^\d+(?:\.\d+)?$/.test(input) || input.length > 100) return null;
  let [whole, fraction = ''] = input.split('.');
  whole = whole.replace(/^0+(?=\d)/, '');
  fraction = fraction.replace(/0+$/, '');
  if (fraction.length > 28) return null;
  const digits = (whole + fraction).replace(/^0+/, '') || '0';
  if (BigInt(digits) > 79228162514264337593543950335n) return null;
  return whole + (fraction ? '.' + fraction : '');
}

export function formatUah(value: string): string {
  const negative = value.startsWith('-');
  const [whole, fraction = ''] = (negative ? value.slice(1) : value).split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');
  return `${negative ? '−' : ''}${grouped},${fraction.padEnd(2, '0')} ₴`;
}
