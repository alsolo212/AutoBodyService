import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, input } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../core/auth';
import { LogoutControl } from '../../shared/logout-control/logout-control';
import { MobileNavigation } from '../../shared/mobile-navigation/mobile-navigation';
import { NotificationBell } from '../../shared/notification-bell/notification-bell';
import { SkipLink } from '../../shared/skip-link/skip-link';

@Component({ selector: 'app-admin-shell', imports: [NgTemplateOutlet, RouterLink, RouterLinkActive, LogoutControl, MobileNavigation, NotificationBell, SkipLink], templateUrl: './admin-shell.html', styleUrl: './admin-shell.scss' })
export class AdminShell {
  private readonly auth = inject(AuthService);
  readonly wide = input(false);
  readonly isSuperAdmin = () => this.auth.session()?.roles.includes('SuperAdmin') ?? false;
}
