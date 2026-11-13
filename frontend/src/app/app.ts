import { Component, inject } from '@angular/core';
import { NotificationRealtime } from './core/notification-realtime';
import { RouterOutlet } from '@angular/router';

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  template: '<router-outlet />',
})
export class App {
  private readonly notifications = inject(NotificationRealtime);
}
