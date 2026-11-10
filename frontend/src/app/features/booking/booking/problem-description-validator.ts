import { AbstractControl, ValidationErrors } from '@angular/forms';

// Keep the accepted description rules aligned with ProblemDescriptionValidator in Domain.
const linkPrefix = /(?:https?:\/\/|www\.|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}(?=[^a-z0-9-]|$))/iu;
const words = /[\p{L}\p{M}]+/gu;
const linkPrompts = new Set([
  'дивіться', 'дивись', 'дивитися', 'подивіться', 'гляньте', 'перегляньте', 'переглянути', 'відео', 'фото', 'зображення', 'посилання', 'посиланням', 'за', 'на', 'по', 'і', 'й', 'та', 'ось', 'тут', 'це', 'моє', 'мій', 'будь', 'ласка',
  'смотрите', 'смотри', 'смотреть', 'посмотрите', 'гляньте', 'видео', 'фото', 'изображение', 'ссылка', 'ссылке', 'ссылки', 'см', 'и', 'в', 'вот', 'здесь', 'ниже', 'это', 'моё', 'мое', 'мой', 'пожалуйста',
  'see', 'watch', 'view', 'video', 'photo', 'image', 'link', 'the', 'and', 'this', 'my', 'url', 'please', 'here', 'below',
]);

export function problemDescriptionValidator(control: AbstractControl): ValidationErrors | null {
  const value = control.value;
  if (typeof value !== 'string' || !value.trim()) return { required: true };
  let hasLink = false;
  // A URL extends to whitespace; separate any following description with a space or newline.
  const remaining = value.replace(/\S+/gu, token => {
    const prefix = linkPrefix.exec(token);
    if (!prefix) return token;
    let candidate = token.slice(prefix.index).replace(/[.,;:!?\)\]}»”]+$/u, '');
    if (!/^https?:\/\//iu.test(candidate)) candidate = `https://${candidate}`;
    try {
      const url = new URL(candidate);
      if (!['http:', 'https:'].includes(url.protocol) || !url.hostname) return token;
    } catch { return token; }
    hasLink = true;
    return token.slice(0, prefix.index) + ' ';
  });
  const text = hasLink ? remaining.replace(words, word => linkPrompts.has(word.toLowerCase()) ? ' ' : word) : remaining;
  return /\p{L}/u.test(text) ? null : { descriptionRequired: true };
}
