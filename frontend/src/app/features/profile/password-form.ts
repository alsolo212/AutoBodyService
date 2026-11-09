import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, input, output, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { finalize } from 'rxjs';
import { AuthService } from '../../core/auth';
import { passwordValidator, passwordsMatch } from '../auth/auth-validation';

@Component({ selector: 'app-password-form', imports: [ReactiveFormsModule], template: `
<form class="profile-form" [formGroup]="form" (ngSubmit)="submit()" novalidate>
  @if (error()) { <div class="auth-alert" role="alert">{{ error() }}</div> }
  @if (success() && !forced()) { <div class="profile-success" role="status">Пароль змінено.</div> }
  <div class="field"><label class="field__label" for="current-password">{{ forced() ? 'Тимчасовий пароль' : 'Поточний пароль' }}</label>
    <input class="field__input" id="current-password" type="password" autocomplete="current-password" formControlName="currentPassword" [attr.aria-invalid]="invalid('currentPassword')" [attr.aria-describedby]="invalid('currentPassword') ? 'current-password-error' : null" />
    @if (invalid('currentPassword')) { <p class="field__error" id="current-password-error">Введіть поточний пароль (не більше 256 символів).</p> }
  </div>
  <div class="field"><label class="field__label" for="new-password">Новий пароль</label>
    <input class="field__input" id="new-password" type="password" autocomplete="new-password" formControlName="password" [attr.aria-invalid]="invalid('password')" aria-describedby="new-password-help" />
    <p id="new-password-help" [class.field__error]="invalid('password')" class="field__hint">Щонайменше 12 символів: великі й малі латинські літери, цифра та спеціальний символ. Новий пароль має відрізнятися від поточного.</p>
  </div>
  <div class="field"><label class="field__label" for="confirm-new-password">Повторіть новий пароль</label>
    <input class="field__input" id="confirm-new-password" type="password" autocomplete="new-password" formControlName="confirmPassword" [attr.aria-invalid]="confirmInvalid()" [attr.aria-describedby]="confirmInvalid() ? 'confirm-new-password-error' : null" />
    @if (confirmInvalid()) { <p class="field__error" id="confirm-new-password-error">Паролі мають збігатися.</p> }
  </div>
  <button class="button button--primary" type="submit" [disabled]="loading()">{{ loading() ? 'Зберігаємо…' : 'Змінити пароль' }}</button>
</form>` })
export class PasswordForm {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly auth = inject(AuthService);
  readonly forced = input(false);
  readonly changed = output<void>();
  readonly loading = signal(false); readonly error = signal(''); readonly success = signal(false);
  readonly form = this.fb.group({ currentPassword: ['', [Validators.required, Validators.maxLength(256)]], password: ['', [Validators.required, passwordValidator]], confirmPassword: ['', Validators.required] }, { validators: passwordsMatch });
  invalid(name: 'currentPassword' | 'password') { const field = this.form.controls[name]; return field.touched && field.invalid; }
  confirmInvalid() { const field = this.form.controls.confirmPassword; return field.touched && (field.invalid || this.form.hasError('mismatch')); }
  submit() {
    if (this.loading()) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    const { currentPassword, password } = this.form.getRawValue();
    this.error.set(''); this.success.set(false);
    if (currentPassword === password) { this.error.set('Новий пароль має відрізнятися від поточного.'); return; }
    this.loading.set(true);
    this.auth.changePassword(currentPassword, password).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: () => { this.form.reset(); this.success.set(true); this.changed.emit(); },
      error: (error: unknown) => this.error.set(error instanceof HttpErrorResponse && error.status === 400 ? 'Не вдалося змінити пароль. Перевірте поточний пароль і вимоги до нового.' : error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' : error instanceof HttpErrorResponse && error.status === 409 ? 'Обліковий запис змінився. Увійдіть знову та повторіть спробу.' : error instanceof HttpErrorResponse && error.status === 429 ? 'Забагато спроб. Зачекайте та спробуйте ще раз.' : 'Не вдалося змінити пароль. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
}
