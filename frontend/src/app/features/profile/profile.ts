import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../../core/auth';
import { OwnProfile, ProfileService } from '../../core/profile';
import { nameValidator } from '../auth/auth-validation';
import { AccountHeader } from '../appointments/account-shell/account-header';
import { PasswordForm } from './password-form';

@Component({ selector: 'app-profile', imports: [ReactiveFormsModule, RouterLink, PasswordForm, AccountHeader], template: `
<div class="profile-page">@if (isAdminArea) { <header class="profile-header"><a class="brand" routerLink="/" aria-label="AutoBodyService — головна"><span class="brand__name">AutoBody<span>Service</span><small>СЕРВІС КУЗОВНОГО РЕМОНТУ</small></span></a><nav aria-label="Навігація профілю"><a [routerLink]="isAdminArea ? '/admin' : '/account/appointments'">{{ isAdminArea ? 'Панель керування' : 'Мої записи' }}</a>@if (isStaff()) { <a [routerLink]="isAdminArea ? '/account/appointments' : '/admin'">{{ isAdminArea ? 'Особистий кабінет' : 'Панель керування' }}</a> }</nav><button class="button button--secondary" type="button" [disabled]="loggingOut()" (click)="logout()">{{ loggingOut() ? 'Виходимо…' : 'Вийти' }}</button></header> } @else { <app-account-header /> }
<main class="profile-main"><p class="eyebrow"><span class="eyebrow__line"></span> {{ isAdminArea ? 'ПАНЕЛЬ КЕРУВАННЯ' : 'ОСОБИСТИЙ КАБІНЕТ' }}</p><h1>Мій профіль</h1><p class="profile-intro">Керуйте особистими даними та паролем.</p>
@if (logoutError()) { <div class="auth-alert" role="alert">{{ logoutError() }}</div> }
@if (loading()) { <p role="status">Завантажуємо профіль…</p> }
@else if (loadError()) { <div class="auth-alert" role="alert">{{ loadError() }}</div><button class="button button--secondary" type="button" (click)="load()">Спробувати ще раз</button> }
@else if (profile(); as item) {
  <section class="profile-panel" aria-labelledby="personal-details"><h2 id="personal-details">Особисті дані</h2><p>Номер телефону змінюється через персонал СТО.</p>
  @if (saveError()) { <div class="auth-alert" role="alert">{{ saveError() }}</div> }
  @if (saved()) { <div class="profile-success" role="status">Ім’я збережено.</div> }
  <form class="profile-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
    <div class="field"><label class="field__label" for="profile-name">Ім’я</label><input class="field__input" id="profile-name" formControlName="name" autocomplete="name" maxlength="200" [attr.aria-invalid]="nameInvalid()" [attr.aria-describedby]="nameInvalid() ? 'profile-name-error' : null" />@if (nameInvalid()) { <p class="field__error" id="profile-name-error">Вкажіть ім’я (не більше 200 символів).</p> }</div>
    <div class="field"><span class="field__label">Номер телефону</span><p class="profile-phone">{{ item.phone }}</p></div>
    <button class="button button--primary" type="submit" [disabled]="saving()">{{ saving() ? 'Зберігаємо…' : 'Зберегти ім’я' }}</button>
  </form></section>
  <section class="profile-panel" aria-labelledby="password-heading"><h2 id="password-heading">Зміна пароля</h2><p>Після зміни пароля інші активні сеанси завершаться.</p><app-password-form /></section>
}
</main></div>` })
export class Profile {
  private readonly fb = inject(NonNullableFormBuilder); private readonly profiles = inject(ProfileService);
  private readonly auth = inject(AuthService); private readonly router = inject(Router);
  readonly isAdminArea = inject(ActivatedRoute).snapshot.data['area'] === 'admin';
  readonly isStaff = () => this.auth.session()?.roles.some(role => role === 'Admin' || role === 'SuperAdmin') ?? false;
  readonly profile = signal<OwnProfile | null>(null); readonly loading = signal(false); readonly loadError = signal('');
  readonly saving = signal(false); readonly saved = signal(false); readonly saveError = signal('');
  readonly loggingOut = signal(false); readonly logoutError = signal('');
  readonly form = this.fb.group({ name: ['', [nameValidator, Validators.maxLength(200)]] });
  constructor() { this.load(); }
  nameInvalid() { return this.form.controls.name.touched && this.form.controls.name.invalid; }
  load() {
    this.loading.set(true); this.loadError.set('');
    this.profiles.get().pipe(finalize(() => this.loading.set(false))).subscribe({
      next: profile => { this.profile.set(profile); this.form.setValue({ name: profile.name }); },
      error: (error: unknown) => this.loadError.set(error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' : 'Не вдалося завантажити профіль. Перевірте з’єднання.'),
    });
  }
  save() {
    if (this.saving()) return;
    this.form.markAllAsTouched(); if (this.form.invalid) return;
    const name = this.form.getRawValue().name.trim();
    this.saving.set(true); this.saved.set(false); this.saveError.set('');
    this.profiles.updateName(name).pipe(finalize(() => this.saving.set(false))).subscribe({
      next: () => { this.profile.update(profile => profile ? { ...profile, name } : profile); this.form.setValue({ name }); this.saved.set(true); this.auth.session.update(session => session ? { ...session, name } : session); },
      error: (error: unknown) => this.saveError.set(error instanceof HttpErrorResponse && error.status === 401 ? 'Сесію завершено. Увійдіть знову.' : error instanceof HttpErrorResponse && error.status === 409 ? 'Дані змінилися. Оновіть сторінку та спробуйте ще раз.' : error instanceof HttpErrorResponse && error.status === 400 ? 'Не вдалося зберегти ім’я. Перевірте введені дані.' : 'Не вдалося зберегти ім’я. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
  logout() {
    if (this.loggingOut()) return;
    this.loggingOut.set(true); this.logoutError.set('');
    this.auth.logout().pipe(finalize(() => this.loggingOut.set(false))).subscribe({ next: () => void this.router.navigateByUrl('/login'), error: () => this.logoutError.set('Не вдалося вийти. Перевірте з’єднання та спробуйте ще раз.') });
  }
}
