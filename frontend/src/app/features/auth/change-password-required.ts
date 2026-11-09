import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService, destination } from '../../core/auth';
import { PasswordForm } from '../profile/password-form';

@Component({ selector: 'app-change-password-required', imports: [PasswordForm], template: `
<div class="auth-page"><main class="auth-main"><div class="auth-card"><p class="eyebrow"><span class="eyebrow__line"></span> БЕЗПЕКА ОБЛІКОВОГО ЗАПИСУ</p><h1>Потрібно змінити пароль.</h1><p class="auth-intro">Ви увійшли з тимчасовим паролем. Для продовження встановіть новий пароль.</p>
@if (!changed()) { <app-password-form [forced]="true" (changed)="passwordChanged()" /> }
@else { <p class="profile-success" role="status">Пароль змінено. Перевіряємо сесію…</p>@if (sessionError()) { <div class="auth-alert" role="alert">{{ sessionError() }}</div><button class="button button--secondary" type="button" [disabled]="checking()" (click)="checkSession()">Перевірити сесію ще раз</button> } }
@if (logoutError()) { <div class="auth-alert" role="alert">{{ logoutError() }}</div> }
<button class="button button--outline auth-submit" type="button" [disabled]="loggingOut()" (click)="logout()">{{ loggingOut() ? 'Виходимо…' : 'Вийти' }}</button></div></main></div>` })
export class ChangePasswordRequired {
  private readonly auth = inject(AuthService); private readonly router = inject(Router);
  readonly changed = signal(false); readonly checking = signal(false); readonly sessionError = signal('');
  readonly loggingOut = signal(false); readonly logoutError = signal('');
  passwordChanged() { this.changed.set(true); this.checkSession(); }
  checkSession() {
    if (this.checking()) return;
    this.checking.set(true); this.sessionError.set('');
    this.auth.getSession().pipe(finalize(() => this.checking.set(false))).subscribe({
      next: session => { if (!session) { void this.router.navigateByUrl('/login'); return; } if (session.mustChangePassword) { this.sessionError.set('Не вдалося підтвердити зміну пароля. Спробуйте перевірити сесію ще раз.'); return; } void this.router.navigateByUrl(destination(session)); },
      error: () => this.sessionError.set('Пароль змінено, але не вдалося перевірити сесію. Перевірте з’єднання та повторіть спробу.'),
    });
  }
  logout() {
    if (this.loggingOut()) return;
    this.loggingOut.set(true); this.logoutError.set('');
    this.auth.logout().pipe(finalize(() => this.loggingOut.set(false))).subscribe({ next: () => void this.router.navigateByUrl('/login'), error: () => this.logoutError.set('Не вдалося вийти. Перевірте з’єднання та спробуйте ще раз.') });
  }
}
