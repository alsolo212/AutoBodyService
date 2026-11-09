import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService, destination } from '../../core/auth';
import { phoneValidator } from './auth-validation';

@Component({
  selector: 'app-login', imports: [ReactiveFormsModule, RouterLink],
  template: `
  <div class="auth-page"><header class="auth-header"><a class="brand" routerLink="/" aria-label="AutoBodyService — головна"><span class="brand__name">AutoBody<span>Service</span><small>СЕРВІС КУЗОВНОГО РЕМОНТУ</small></span></a><a class="auth-back" routerLink="/">← На головну</a></header>
  <main class="auth-main"><div class="auth-card"><p class="eyebrow"><span class="eyebrow__line"></span> ОСОБИСТИЙ КАБІНЕТ</p><h1>Вітаємо знову.</h1><p class="auth-intro">Увійдіть, щоб переглядати свої записи та перебіг ремонту.</p>
  @if (error()) { <div class="auth-alert" role="alert">{{ error() }}</div> }
  <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
    <div class="field"><label class="field__label" for="login-phone">Номер телефону</label><input class="field__input" id="login-phone" formControlName="phone" type="tel" autocomplete="tel" inputmode="tel" placeholder="+380" [attr.aria-invalid]="invalid('phone')" aria-describedby="login-phone-help" /><p id="login-phone-help" [class.field__error]="invalid('phone')" class="field__hint">{{ invalid('phone') ? 'Введіть коректний номер України.' : 'Наприклад, +380 та 9 цифр номера.' }}</p></div>
    <div class="field"><div class="auth-label-row"><label class="field__label" for="login-password">Пароль</label><a routerLink="/forgot-password">Забули пароль?</a></div><input class="field__input" id="login-password" formControlName="password" type="password" autocomplete="current-password" [attr.aria-invalid]="invalid('password')" [attr.aria-describedby]="invalid('password') ? 'login-password-error' : null" />@if (invalid('password')) { <p class="field__error" id="login-password-error">Введіть пароль.</p> }</div>
    <button class="button button--primary auth-submit" type="submit" [disabled]="loading()">{{ loading() ? 'Входимо…' : 'Увійти' }} <span aria-hidden="true">↗</span></button>
  </form><p class="auth-switch">Ще не маєте облікового запису? <a routerLink="/register">Зареєструватися</a></p></div></main></div>`
})
export class Login {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly loading = signal(false);
  readonly error = signal(inject(ActivatedRoute).snapshot.queryParamMap.get('session') === 'unavailable' ? 'Не вдалося перевірити сесію. Перевірте з’єднання та спробуйте ще раз.' : '');
  readonly form = this.fb.group({ phone: ['', [Validators.required, phoneValidator]], password: ['', [Validators.required, Validators.maxLength(256)]] });
  invalid(name: 'phone' | 'password') { const field = this.form.controls[name]; return field.touched && field.invalid; }
  submit() {
    if (this.loading()) return;
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    this.error.set(''); this.loading.set(true);
    this.auth.login(this.form.getRawValue()).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: session => void this.router.navigateByUrl(destination(session)),
      error: (error: unknown) => this.error.set(error instanceof HttpErrorResponse && error.status === 401 ? 'Невірний номер телефону або пароль.' : error instanceof HttpErrorResponse && error.status === 429 ? 'Забагато спроб. Зачекайте та спробуйте ще раз.' : 'Не вдалося виконати вхід. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
}
