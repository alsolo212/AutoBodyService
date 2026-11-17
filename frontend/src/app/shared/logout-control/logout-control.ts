import { Component, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { finalize } from 'rxjs';
import { AuthService } from '../../core/auth';
import { ConfirmDialog } from '../confirm-dialog/confirm-dialog';

@Component({ selector: 'app-logout-control', imports: [ConfirmDialog], templateUrl: './logout-control.html', styleUrl: './logout-control.scss' })
export class LogoutControl {
  private static nextId = 0;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly dialog = viewChild.required(ConfirmDialog);
  readonly dialogId = `confirm-logout-${++LogoutControl.nextId}`;
  readonly loggingOut = signal(false);
  readonly error = signal('');
  open(): void { if (!this.loggingOut()) { this.error.set(''); this.dialog().open(); } }
  logout(): void {
    if (this.loggingOut()) return;
    this.loggingOut.set(true); this.error.set('');
    this.auth.logout().pipe(finalize(() => this.loggingOut.set(false))).subscribe({
      next: () => void this.router.navigateByUrl('/login'),
      error: () => this.error.set('Не вдалося вийти. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
}
