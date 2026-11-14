import { Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { NotificationRealtime } from '../../core/notification-realtime';
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
  close(event: Event) {
    const details = event.currentTarget as HTMLDetailsElement;
    details.open = false;
    details.querySelector('summary')?.focus();
    event.stopPropagation();
  }
}
