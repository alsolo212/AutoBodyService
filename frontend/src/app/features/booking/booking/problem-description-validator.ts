import { AbstractControl, ValidationErrors } from '@angular/forms';

// Keep the accepted description rules aligned with ProblemDescriptionValidator in Domain.
const links = /(?:https?:\/\/|www\.)[^\s<>"'«»“”(),;!?–—]+(?:\?[^\s<>"'«»“”()!–—]+)?|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}(?:\/[^\s<>"'«»“”(),;!?–—]+(?:\?[^\s<>"'«»“”()!–—]+)?)?/giu;
const words = /[\p{L}\p{M}]+/gu;
const linkPrompts = new Set([
  'дивіться', 'дивись', 'дивитися', 'подивіться', 'гляньте', 'перегляньте', 'переглянути', 'відео', 'фото', 'зображення', 'посилання', 'посиланням', 'за', 'на', 'по', 'і', 'й', 'та', 'ось', 'тут', 'це', 'моє', 'мій', 'будь', 'ласка',
  'смотрите', 'смотри', 'смотреть', 'посмотрите', 'гляньте', 'видео', 'фото', 'изображение', 'ссылка', 'ссылке', 'ссылки', 'см', 'и', 'в', 'вот', 'здесь', 'ниже', 'это', 'моё', 'мое', 'мой', 'пожалуйста',
  'see', 'watch', 'view', 'video', 'photo', 'image', 'link', 'the', 'and', 'this', 'my', 'url', 'please', 'here', 'below',
]);

export function problemDescriptionValidator(control: AbstractControl): ValidationErrors | null {
  const value = control.value;
  if (typeof value !== 'string' || !value.trim()) return { required: true };
  links.lastIndex = 0;
  if (!links.test(value)) return null;
  links.lastIndex = 0;
  const remaining = value.replace(links, ' ');
  const meaningful = [...remaining.matchAll(words)].some(match => !linkPrompts.has(match[0].toLowerCase()));
  return meaningful ? null : { descriptionRequired: true };
}
