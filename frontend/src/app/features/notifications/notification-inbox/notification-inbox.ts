import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { NotificationItem, NotificationRealtime } from '../../../core/notification-realtime';
import { AuthService } from '../../../core/auth';
import { notificationLink } from '../../../shared/notification-bell/notification-bell';
import { kyivTime } from '../../../shared/time/kyiv-time';

@Component({ selector: 'app-notification-inbox', imports: [RouterLink], templateUrl: './notification-inbox.html', styleUrl: './notification-inbox.scss' })
export class NotificationInbox {
  readonly notifications = inject(NotificationRealtime);
  readonly auth = inject(AuthService);
  readonly filter = signal<'all' | 'unread'>('all');
  private readonly openIds = signal<Set<string>>(new Set());
  private readonly keepOpen = signal<Set<string>>(new Set());
  readonly visible = computed(() => this.filter() === 'unread' ? this.notifications.history().filter(item => !item.readAtUtc || this.keepOpen().has(item.id)) : this.notifications.history());
  readonly busy = signal(false);
  readonly reading = signal<Set<string>>(new Set());
  readonly actionError = signal('');
  readonly format = kyivTime;
  readonly safeLink = notificationLink;
  constructor() { this.notifications.refresh(); }
  setFilter(filter: 'all' | 'unread') {
    this.keepOpen.set(filter === 'unread' ? new Set(this.notifications.history().filter(item => !item.readAtUtc && this.openIds().has(item.id)).map(item => item.id)) : new Set());
    this.filter.set(filter);
  }
  opened(event: Event, item: NotificationItem) {
    const open = (event.currentTarget as HTMLDetailsElement).open;
    this.openIds.update(ids => { const next = new Set(ids); if (open) next.add(item.id); else next.delete(item.id); return next; });
    if (open && this.filter() === 'unread') this.keepOpen.update(ids => new Set(ids).add(item.id));
    if (!open) this.keepOpen.update(ids => { const next = new Set(ids); next.delete(item.id); return next; });
    if (open && !item.readAtUtc) this.read(item.id);
  }
  openLink(item: NotificationItem) { if (!item.readAtUtc) this.read(item.id); }
  read(id: string) {
    if (this.reading().has(id)) return;
    this.reading.update(ids => new Set(ids).add(id)); this.actionError.set('');
    this.notifications.markRead(id).pipe(finalize(() => this.reading.update(ids => { const next = new Set(ids); next.delete(id); return next; }))).subscribe({ error: error => this.actionError.set(this.errorMessage(error)) });
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
