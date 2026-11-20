import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { finalize, switchMap } from 'rxjs';

interface Contacts { phone: string | null; address: string | null; version: number; }

@Component({
  selector: 'app-workshop-contacts', imports: [ReactiveFormsModule],
  template: `
    <section class="workspace-panel" aria-labelledby="contacts-heading" [attr.aria-busy]="loading() || saving()">
      <div class="panel-heading"><div><p class="panel-eyebrow">КОНТАКТИ</p><h2 id="contacts-heading">Контакти майстерні</h2></div></div>
      <p>Ці дані бачать клієнти на головній сторінці та після запису на огляд.</p>
      @if (loading()) { <p role="status">Завантажуємо контакти…</p> }
      @if (error()) { <div class="auth-alert" role="alert">{{ error() }} <button type="button" class="text-button" (click)="load()" [disabled]="loading() || saving()">Оновити дані</button></div> }
      @if (notice()) { <p class="profile-success" role="status">{{ notice() }}</p> }
      @if (version() !== null) {
        <form [formGroup]="form" (ngSubmit)="save()" novalidate>
          <div class="field"><label class="field__label" for="workshop-phone">Телефон майстерні</label><input id="workshop-phone" class="field__input" type="tel" autocomplete="tel" maxlength="32" formControlName="phone" [readOnly]="saving()" [attr.aria-invalid]="form.controls.phone.touched && form.controls.phone.invalid" aria-describedby="workshop-phone-hint" /><p class="hint" id="workshop-phone-hint">Український номер, наприклад +380XXXXXXXXX.</p>@if (form.controls.phone.touched && form.controls.phone.invalid) { <p class="field__error">Вкажіть коректний номер телефону.</p> }</div>
          <div class="field"><label class="field__label" for="workshop-address">Адреса майстерні</label><input id="workshop-address" class="field__input" autocomplete="street-address" maxlength="200" formControlName="address" [readOnly]="saving()" [attr.aria-invalid]="form.controls.address.touched && form.controls.address.invalid" />@if (form.controls.address.touched && form.controls.address.invalid) { <p class="field__error">Вкажіть адресу до 200 символів без керівних символів.</p> }</div>
          <button type="submit" class="button button--primary" [disabled]="saving() || loading() || conflict()">{{ saving() ? 'Зберігаємо…' : 'Зберегти контакти' }}</button>
        </form>
      }
    </section>
  `,
  styles: `form { display: grid; gap: 1rem; } form button { justify-self: start; } .hint { color: var(--muted); font-size: .875rem; margin: .35rem 0 0; }`,
})
export class WorkshopContacts {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(NonNullableFormBuilder);
  readonly loading = signal(false); readonly saving = signal(false); readonly conflict = signal(false);
  readonly error = signal(''); readonly notice = signal(''); readonly version = signal<number | null>(null);
  readonly form = this.fb.group({
    phone: ['', [Validators.required, control => {
      const value = String(control.value ?? '');
      if (value.length > 32 || /[^\d+ ()-]/.test(value)) return { phone: true };
      const compact = value.replace(/[ ()-]/g, '');
      return /^(?:\+380\d{9}|380\d{9}|0\d{9})$/.test(compact) ? null : { phone: true };
    }]],
    address: ['', [Validators.maxLength(200), control => !String(control.value ?? '').trim() || /\p{Cc}/u.test(String(control.value ?? '')) ? { address: true } : null]],
  });
  constructor() { this.load(); }
  load() {
    if (this.loading() || this.saving()) return;
    this.loading.set(true); this.error.set(''); this.notice.set('');
    this.http.get<Contacts>('/api/admin/workshop-contacts').pipe(finalize(() => this.loading.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: value => { this.version.set(value.version); this.form.reset({ phone: value.phone ?? '', address: value.address ?? '' }); this.conflict.set(false); },
      error: () => this.error.set('Не вдалося завантажити контакти майстерні.'),
    });
  }
  save() {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.version() === null || this.saving() || this.loading() || this.conflict()) return;
    this.saving.set(true); this.error.set(''); this.notice.set('');
    const body = { ...this.form.getRawValue(), version: this.version() };
    this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => this.http.put<Contacts>('/api/admin/workshop-contacts', body)), finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef)).subscribe({
      next: value => { this.version.set(value.version); this.form.reset({ phone: value.phone ?? '', address: value.address ?? '' }); this.notice.set('Контакти збережено.'); },
      error: (error: HttpErrorResponse) => { this.conflict.set(error.status === 409); this.error.set(error.status === 409 ? 'Контакти змінилися. Оновіть дані перед збереженням.' : error.status === 401 || error.status === 403 ? 'Доступ заборонено або сесію завершено.' : 'Не вдалося зберегти контакти.'); },
    });
  }
}
