import { Component, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiStatus } from '../../core/api-status/api-status';
import { AuthService } from '../../core/auth';
import { Dashboard } from '../../features/admin/dashboard/dashboard';
import { Employees } from '../../features/admin/employees/employees';
import { AssignmentBoard } from '../../features/admin/assignment-board/assignment-board';
import { PricesEditor } from '../../features/admin/prices/prices-editor';
import { NotificationInbox } from '../../features/notifications/notification-inbox/notification-inbox';
import { Users } from '../../features/admin/users/users';
import { Reports } from '../../features/admin/reports/reports';
import { AdminShell } from '../admin-shell/admin-shell';

@Component({
  selector: 'app-workspace-shell',
  imports: [RouterLink, ApiStatus, Dashboard, Employees, AssignmentBoard, PricesEditor, NotificationInbox, Users, Reports, AdminShell],
  template: `
    <app-admin-shell>
      <nav class="breadcrumbs" aria-label="Шлях сторінки"><a routerLink="/admin">Панель керування</a>@if (section) { <span aria-hidden="true">/</span><span aria-current="page">{{ section === 'Звіти' ? 'Контроль та звітність' : section }}</span> }</nav>
      @if (!section) { <app-admin-dashboard /> }
      @else if (section === 'Розподіл робіт') { @defer (on immediate) { <app-assignment-board /> } @placeholder { <p role="status">Завантажуємо розподіл робіт…</p> } }
      @else if (section === 'Налаштування майстерні') { <app-prices-editor /> }
      @else if (section === 'Сповіщення') { <app-notification-inbox /> }
      @else if (section === 'Працівники') { <app-employees /> }
      @else if (section === 'Користувачі') { <app-users /> }
      @else if (section === 'Звіти' && isSuperAdmin()) { @defer (on immediate) { <app-reports /> } @placeholder { <p role="status">Завантажуємо звіти…</p> } }
      @else if (section === 'Звіти') { <p role="alert">Доступ до звітності обмежено.</p> }
      <app-api-status />
    </app-admin-shell>
  `,
})
export class WorkspaceShell {
  private readonly auth = inject(AuthService);
  readonly section = inject(ActivatedRoute).snapshot.data['section'] as string | undefined;
  readonly isSuperAdmin = () => this.auth.session()?.roles.includes('SuperAdmin') ?? false;
}
