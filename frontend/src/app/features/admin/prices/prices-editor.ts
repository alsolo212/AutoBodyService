import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { finalize, switchMap } from 'rxjs';

interface PriceItem { id: string; name: string; description: string; priceExact: string; isFrom: boolean; sortOrder: number; isPublished: boolean; isArchived: boolean; version: number; }

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
const textValidator: ValidatorFn = control => {
  const value = String(control.value ?? '');
  return value.trim() && !/\p{Cc}/u.test(value) ? null : { invalidText: true };
};
const descriptionValidator: ValidatorFn = control => /\p{Cc}/u.test(String(control.value ?? '')) ? { invalidText: true } : null;
const amountValidator: ValidatorFn = control => decimalLiteral(String(control.value ?? '')) === null ? { invalidAmount: true } : null;
const orderValidator: ValidatorFn = control => {
  const value = control.value;
  return Number.isInteger(value) && value >= -2147483648 && value <= 2147483647 ? null : { invalidOrder: true };
};

@Component({ selector: 'app-prices-editor', imports: [ReactiveFormsModule], templateUrl: './prices-editor.html', styleUrl: './prices-editor.scss' })
export class PricesEditor {
  private readonly http = inject(HttpClient);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly nameInput = viewChild.required<ElementRef<HTMLInputElement>>('nameInput');
  readonly items = signal<PriceItem[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly loadError = signal('');
  readonly actionError = signal('');
  readonly notice = signal('');
  readonly editing = signal<PriceItem | null>(null);
  readonly form = this.fb.group({
    name: ['', [textValidator, Validators.maxLength(200)]],
    description: ['', [descriptionValidator, Validators.maxLength(1000)]],
    price: ['', amountValidator],
    sortOrder: [0, orderValidator],
    isFrom: [false],
    isPublished: [false],
    isArchived: [false],
  });
  constructor() { this.load(); }
  load() {
    if (this.loading() || this.saving()) return;
    this.loading.set(true); this.loadError.set('');
    this.http.get<PriceItem[]>('/api/admin/prices').pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: items => this.items.set(items), error: error => this.loadError.set(this.message(error, 'Не вдалося завантажити послуги.')),
    });
  }
  edit(item: PriceItem) {
    if (this.saving()) return;
    this.editing.set(item); this.actionError.set(''); this.notice.set('');
    this.form.reset({ name: item.name, description: item.description, price: item.priceExact.replace('.', ','), sortOrder: item.sortOrder, isFrom: item.isFrom, isPublished: item.isPublished, isArchived: item.isArchived });
    this.nameInput().nativeElement.focus();
  }
  cancel() {
    this.editing.set(null); this.form.reset({ name: '', description: '', price: '', sortOrder: 0, isFrom: false, isPublished: false, isArchived: false }); this.actionError.set('');
  }
  save() {
    if (this.saving() || this.loading() || this.loadError()) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const value = this.form.getRawValue();
    if (value.isArchived && value.isPublished) { this.actionError.set('Архівну послугу не можна публікувати.'); return; }
    const price = decimalLiteral(value.price);
    if (price === null) return;
    const current = this.editing();
    const fields = { name: value.name.trim(), description: value.description.trim(), isFrom: value.isFrom, sortOrder: value.sortOrder, isPublished: value.isPublished };
    const body = JSON.stringify(current ? { version: current.version, ...fields, isArchived: value.isArchived } : fields);
    const json = body.slice(0, -1) + ',"price":' + price + '}';
    this.saving.set(true); this.actionError.set(''); this.notice.set('');
    const headers = new HttpHeaders({ 'Content-Type': 'application/json' });
    this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => current
      ? this.http.patch<PriceItem>(`/api/admin/prices/${encodeURIComponent(current.id)}`, json, { headers })
      : this.http.post<PriceItem>('/api/admin/prices', json, { headers })),
      finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: item => {
        this.items.update(items => [...items.filter(old => old.id !== item.id), item].sort((a, b) => a.sortOrder - b.sortOrder || a.id.localeCompare(b.id)));
        this.cancel(); this.notice.set(current ? 'Зміни збережено.' : 'Послугу додано.');
      },
      error: error => this.actionError.set(this.message(error, 'Не вдалося зберегти послугу.')),
    });
  }
  private message(error: unknown, fallback: string) {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401) return 'Сесію завершено. Увійдіть знову.';
      if (error.status === 403) return 'Доступ до прайсу заборонено.';
      if (error.status === 409) return 'Дані послуги змінилися. Оновіть список і повторіть редагування.';
      if (error.status === 400) return 'Сервер відхилив дані. Перевірте введені значення.';
      if (error.status === 404) return 'Послугу не знайдено. Оновіть список.';
    }
    return fallback + ' Перевірте з’єднання та спробуйте ще раз.';
  }
}
