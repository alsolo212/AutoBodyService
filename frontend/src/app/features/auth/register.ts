import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../../core/auth';
import { nameValidator, passwordValidator, passwordsMatch, phoneValidator } from './auth-validation';

@Component({ selector: 'app-register', imports: [ReactiveFormsModule, RouterLink], template: `
<div class="auth-page"><header class="auth-header"><a class="brand" routerLink="/" aria-label="AutoBodyService — головна"><span class="brand__name">AutoBody<span>Service</span><small>СЕРВІС КУЗОВНОГО РЕМОНТУ</small></span></a><a class="auth-back" routerLink="/">← На головну</a></header>
<main class="auth-main"><div class="auth-card"><p class="eyebrow"><span class="eyebrow__line"></span> ОСОБИСТИЙ КАБІНЕТ</p><h1>Створіть обліковий запис.</h1><p class="auth-intro">Вкажіть свої дані, щоб користуватися особистим кабінетом.</p>
@if (success()) { <div class="auth-success" role="status"><strong>Запит прийнято.</strong><p>Якщо облікового запису з цим номером ще немає, його створено. Тепер ви можете увійти. Якщо не вдається увійти, зверніться до персоналу СТО.</p><a class="button button--primary" routerLink="/login">Перейти до входу ↗</a></div> }
@else {
@if (error()) { <div class="auth-alert" role="alert">{{ error() }}</div> }
<form [formGroup]="form" (ngSubmit)="submit()" novalidate>
<div class="field"><label class="field__label" for="register-name">Ім’я</label><input class="field__input" id="register-name" formControlName="name" autocomplete="name" maxlength="200" [attr.aria-invalid]="invalid('name')" [attr.aria-describedby]="invalid('name') ? 'register-name-error' : null" />@if (invalid('name')) { <p class="field__error" id="register-name-error">Вкажіть ім’я.</p> }</div>
<div class="field"><label class="field__label" for="register-phone">Номер телефону</label><input class="field__input" id="register-phone" formControlName="phone" type="tel" autocomplete="tel" inputmode="tel" placeholder="+380" [attr.aria-invalid]="invalid('phone')" aria-describedby="register-phone-help" /><p id="register-phone-help" [class.field__error]="invalid('phone')" class="field__hint">{{ invalid('phone') ? 'Введіть коректний номер України.' : 'Наприклад, +380 та 9 цифр номера.' }}</p></div>
<div class="field"><label class="field__label" for="register-password">Пароль</label><input class="field__input" id="register-password" formControlName="password" type="password" autocomplete="new-password" [attr.aria-invalid]="invalid('password')" aria-describedby="password-help" /><p id="password-help" [class.field__error]="invalid('password')" class="field__hint">Щонайменше 12 символів: великі й малі латинські літери, цифра та спеціальний символ.</p></div>
<div class="field"><label class="field__label" for="register-confirm">Повторіть пароль</label><input class="field__input" id="register-confirm" formControlName="confirmPassword" type="password" autocomplete="new-password" [attr.aria-invalid]="confirmInvalid()" [attr.aria-describedby]="confirmInvalid() ? 'confirm-error' : null" />@if (confirmInvalid()) { <p class="field__error" id="confirm-error">Паролі мають збігатися.</p> }</div>
<button class="button button--primary auth-submit" type="submit" [disabled]="loading()">{{ loading() ? 'Надсилаємо…' : 'Зареєструватися' }} <span aria-hidden="true">↗</span></button>
</form> }
<p class="auth-switch">Вже маєте обліковий запис? <a routerLink="/login">Увійти</a></p></div></main></div>` })
export class Register {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly auth = inject(AuthService);
  readonly loading = signal(false); readonly success = signal(false); readonly error = signal('');
  readonly form = this.fb.group({ name: ['', [nameValidator, Validators.maxLength(200)]], phone: ['', [Validators.required, phoneValidator]], password: ['', [Validators.required, passwordValidator]], confirmPassword: ['', Validators.required] }, { validators: passwordsMatch });
  invalid(name: 'name' | 'phone' | 'password') { const field = this.form.controls[name]; return field.touched && field.invalid; }
  confirmInvalid() { const field = this.form.controls.confirmPassword; return field.touched && (field.invalid || this.form.hasError('mismatch')); }
  submit() {
    if (this.loading()) return;
    this.form.markAllAsTouched(); if (this.form.invalid) return;
    const { name, phone, password } = this.form.getRawValue();
    this.loading.set(true); this.error.set('');
    this.auth.register({ name: name.trim(), phone, password }).pipe(finalize(() => this.loading.set(false))).subscribe({
      next: () => { this.form.reset(); this.success.set(true); },
      error: (error: unknown) => this.error.set(error instanceof HttpErrorResponse && error.status === 429 ? 'Забагато спроб. Зачекайте та спробуйте ще раз.' : error instanceof HttpErrorResponse && error.status === 400 ? 'Не вдалося зареєструватися. Перевірте введені дані та спробуйте ще раз.' : 'Не вдалося надіслати запит. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
}
