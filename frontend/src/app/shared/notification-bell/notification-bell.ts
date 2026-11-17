import { Component, computed, inject, input, signal } from '@angular/core';
import { finalize } from 'rxjs';
import { RouterLink } from '@angular/router';
import { NotificationItem, NotificationRealtime } from '../../core/notification-realtime';
import { AuthService } from '../../core/auth';
import { kyivTime } from '../time/kyiv-time';

/** Only application routes for an appointment can be used as notification links. */
export function notificationLink(link: string | null): string | null {
  return link && /^\/(account|admin)\/appointments\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(link) ? link : null;
}

@Component({ selector: 'app-notification-bell', imports: [RouterLink], templateUrl: './notification-bell.html', styleUrl: './notification-bell.scss' })
export class NotificationBell {
  readonly area = input<'account' | 'admin'>('account');
  readonly notifications = inject(NotificationRealtime);
  readonly auth = inject(AuthService);
  readonly recent = computed(() => this.notifications.history().slice(0, 5));
  readonly format = kyivTime;
  readonly safeLink = notificationLink;
  readonly readError = signal('');
  readonly failedId = signal<string | null>(null);
  private readonly pending = new Set<string>();
  opened(event: Event, item: NotificationItem) {
    if ((event.currentTarget as HTMLDetailsElement).open && !item.readAtUtc) this.read(item.id);
  }
  read(id: string) {
    if (this.pending.has(id)) return;
    this.pending.add(id); this.readError.set(''); this.failedId.set(null);
    this.notifications.markRead(id).pipe(finalize(() => this.pending.delete(id))).subscribe({
      error: () => { this.failedId.set(id); this.readError.set('Не вдалося позначити сповіщення прочитаним.'); },
    });
  }
  visit(item: NotificationItem, event: Event) { if (!item.readAtUtc) this.read(item.id); this.dismiss(event); }
  dismiss(event: Event) {
    const details = (event.currentTarget as HTMLElement).closest('details.bell') as HTMLDetailsElement | null;
    if (details) { details.open = false; details.querySelector<HTMLElement>(':scope > summary')?.focus(); }
  }
  close(event: Event) {
    const details = event.currentTarget as HTMLDetailsElement;
    details.open = false;
    details.querySelector('summary')?.focus();
    event.stopPropagation();
  }
}
