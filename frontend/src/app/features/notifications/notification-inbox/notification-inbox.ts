import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { NotificationRealtime } from '../../../core/notification-realtime';
import { AuthService } from '../../../core/auth';
import { notificationLink } from '../../../shared/notification-bell/notification-bell';
import { kyivTime } from '../../../shared/time/kyiv-time';

@Component({ selector: 'app-notification-inbox', imports: [RouterLink], templateUrl: './notification-inbox.html', styleUrl: './notification-inbox.scss' })
export class NotificationInbox {
  readonly notifications = inject(NotificationRealtime);
  readonly auth = inject(AuthService);
  readonly filter = signal<'all' | 'unread'>('all');
  readonly visible = computed(() => this.filter() === 'unread' ? this.notifications.history().filter(item => !item.readAtUtc) : this.notifications.history());
  readonly busy = signal(false);
  readonly actionError = signal('');
  readonly format = kyivTime;
  readonly safeLink = notificationLink;
  constructor() { this.notifications.refresh(); }
  read(id: string) {
    if (this.busy()) return;
    this.busy.set(true); this.actionError.set('');
    this.notifications.markRead(id).pipe(finalize(() => this.busy.set(false))).subscribe({ error: error => this.actionError.set(this.errorMessage(error)) });
  }
  readAll() {
    if (this.busy() || !this.notifications.unreadCount()) return;
    this.busy.set(true); this.actionError.set('');
    this.notifications.markAllRead().pipe(finalize(() => this.busy.set(false))).subscribe({ error: error => this.actionError.set(this.errorMessage(error)) });
  }
  private errorMessage(error: unknown) {
    return error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403)
      ? 'Сесію завершено. Увійдіть знову.' : 'Не вдалося оновити сповіщення. Спробуйте ще раз.';
  }
}
