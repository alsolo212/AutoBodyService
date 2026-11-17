import { NgTemplateOutlet } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../../core/auth';
import { LogoutControl } from '../../../shared/logout-control/logout-control';
import { MobileNavigation } from '../../../shared/mobile-navigation/mobile-navigation';
import { NotificationBell } from '../../../shared/notification-bell/notification-bell';
import { SkipLink } from '../../../shared/skip-link/skip-link';

@Component({ selector: 'app-account-header', imports: [NgTemplateOutlet, RouterLink, RouterLinkActive, LogoutControl, MobileNavigation, NotificationBell, SkipLink], templateUrl: './account-header.html', styleUrl: './account-shell.scss' })
export class AccountHeader {
  private readonly auth = inject(AuthService);
  readonly isStaff = () => this.auth.session()?.roles.some(role => role === 'Admin' || role === 'SuperAdmin') ?? false;
}
