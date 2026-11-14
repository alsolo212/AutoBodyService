import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../../../core/auth';
import { NotificationBell } from '../../../shared/notification-bell/notification-bell';

@Component({ selector: 'app-account-header', imports: [RouterLink, NotificationBell], templateUrl: './account-header.html', styleUrl: './account-shell.scss' })
export class AccountHeader {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly loggingOut = signal(false);
  readonly logoutError = signal('');
  readonly isStaff = () => this.auth.session()?.roles.some(role => role === 'Admin' || role === 'SuperAdmin') ?? false;
  logout() {
    if (this.loggingOut()) return;
    this.loggingOut.set(true); this.logoutError.set('');
    this.auth.logout().pipe(finalize(() => this.loggingOut.set(false))).subscribe({
      next: () => void this.router.navigateByUrl('/login'),
      error: () => this.logoutError.set('Не вдалося вийти. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
}
