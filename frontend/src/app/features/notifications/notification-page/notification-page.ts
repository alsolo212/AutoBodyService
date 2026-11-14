import { Component } from '@angular/core';
import { AccountShell } from '../../appointments/account-shell/account-shell';
import { NotificationInbox } from '../notification-inbox/notification-inbox';

@Component({ selector: 'app-notification-page', imports: [AccountShell, NotificationInbox], templateUrl: './notification-page.html' })
export class NotificationPage {}
