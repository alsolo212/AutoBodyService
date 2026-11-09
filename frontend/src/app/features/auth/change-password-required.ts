import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../../core/auth';
@Component({ selector: 'app-change-password-required', template: `
<div class="auth-page"><main class="auth-main"><div class="auth-card"><p class="eyebrow"><span class="eyebrow__line"></span> БЕЗПЕКА ОБЛІКОВОГО ЗАПИСУ</p><h1>Потрібно змінити пароль.</h1><p class="auth-intro">Ви увійшли з тимчасовим паролем. Для продовження потрібно встановити новий пароль.</p><div class="auth-note"><strong>Зміна пароля поки недоступна</strong><p>Зверніться до персоналу СТО по допомогу з доступом.</p></div>@if (error()) { <div class="auth-alert" role="alert">{{ error() }}</div> }<button class="button button--outline auth-submit" type="button" [disabled]="loading()" (click)="logout()">{{ loading() ? 'Виходимо…' : 'Вийти' }}</button></div></main></div>` })
export class ChangePasswordRequired {
  private readonly auth = inject(AuthService); private readonly router = inject(Router);
  readonly loading = signal(false); readonly error = signal('');
  logout() { this.loading.set(true); this.error.set(''); this.auth.logout().pipe(finalize(() => this.loading.set(false))).subscribe({ next: () => void this.router.navigateByUrl('/login'), error: () => this.error.set('Не вдалося вийти. Перевірте з’єднання та спробуйте ще раз.') }); }
}
