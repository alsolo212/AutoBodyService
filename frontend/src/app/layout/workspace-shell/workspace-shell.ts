import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiState } from '../../shared/ui-state/ui-state';
import { ApiStatus } from '../../core/api-status/api-status';
import { AuthService } from '../../core/auth';
import { finalize } from 'rxjs';
import { Dashboard } from '../../features/admin/dashboard/dashboard';

@Component({
  selector: 'app-workspace-shell',
  imports: [RouterLink, UiState, ApiStatus, Dashboard],
  template: `
    <a class="skip-link" [attr.href]="isAdmin ? '/' + routePath + '#workspace-main' : '/account/appointments#workspace-main'">Перейти до вмісту</a>
    <div class="workspace">
      <header class="workspace-topbar">
        <a class="brand brand--compact" routerLink="/" aria-label="AutoBodyService — головна"><span class="brand__name">AutoBody<span>Service</span><small>СЕРВІС КУЗОВНОГО РЕМОНТУ</small></span></a>
        <span class="workspace-topbar__label">{{ isAdmin ? 'Панель керування' : 'Особистий кабінет' }}</span>
        <a class="workspace-home" routerLink="/">На головну</a>
        @if (isAdmin) { <a class="workspace-switch" routerLink="/account/appointments">Особистий кабінет</a> }
        @else if (isStaff()) { <a class="workspace-switch" routerLink="/admin">Панель керування</a> }
        <button class="button button--secondary" type="button" (click)="logout()" [disabled]="loggingOut()">{{ loggingOut() ? 'Виходимо…' : 'Вийти' }}</button>
        <button class="menu-toggle button button--secondary" type="button" (click)="menuOpen.update(value => !value)" [attr.aria-expanded]="menuOpen()" aria-controls="workspace-nav">{{ menuOpen() ? 'Закрити меню' : 'Меню' }}</button>
      </header>
      <div class="workspace-layout">
        <aside class="sidebar" id="workspace-nav" [class.sidebar--open]="menuOpen()">
          <div class="sidebar__intro"><span class="sidebar__overline">{{ isAdmin ? 'РОБОЧИЙ ПРОСТІР' : 'ВАШ ПРОСТІР' }}</span><strong>{{ isAdmin ? 'Керування сервісом' : 'Мій кабінет' }}</strong></div>
          <nav [attr.aria-label]="isAdmin ? 'Навігація панелі керування' : 'Навігація особистого кабінету'">
            <p class="sidebar__label">{{ isAdmin ? 'ОСНОВНЕ' : 'АВТОМОБІЛЬ' }}</p>
            <ul>
              @for (item of primaryItems; track item) { <li>@if (isAdmin && item === 'Огляд') { <a class="sidebar__item" [class.sidebar__item--active]="!section" [attr.aria-current]="!section ? 'page' : null" routerLink="/admin">{{ item }}</a> } @else if (isAdmin && item === 'Календар оглядів') { <a class="sidebar__item" routerLink="/admin/inspection-calendar">{{ item }}</a> } @else if (isAdmin && item === 'Замовлення') { <a class="sidebar__item" routerLink="/admin/appointments">{{ item }}</a> } @else if (isAdmin && item === 'Розподіл робіт') { <a class="sidebar__item" routerLink="/admin/assignments">{{ item }}</a> } @else if (!isAdmin && item === 'Запис на огляд') { <a class="sidebar__item" routerLink="/account/book">{{ item }}</a> } @else { <span class="sidebar__item" [class.sidebar__item--active]="$first" [attr.aria-current]="$first ? 'page' : null">{{ item }}</span> }</li> }
            </ul>
            <p class="sidebar__label">{{ isAdmin ? 'КЕРУВАННЯ' : 'ОСОБИСТЕ' }}</p>
            <ul>@for (item of secondaryItems; track item) { <li>@if (isAdmin && item === 'Працівники') { <a class="sidebar__item" routerLink="/admin/employees">{{ item }}</a> } @else if (isAdmin && item === 'Послуги та ціни') { <a class="sidebar__item" routerLink="/admin/prices">{{ item }}</a> } @else if (item === 'Мій профіль') { <a class="sidebar__item" [routerLink]="isAdmin ? '/admin/profile' : '/account/profile'">{{ item }}</a> } @else { <span class="sidebar__item">{{ item }}</span> }</li> }</ul>
          </nav>
          <div class="sidebar__note"><span>УВАГА ДО ДЕТАЛЕЙ</span><p>Усе важливе про автомобіль — в одному місці.</p></div>
        </aside>
        <main class="workspace-main" id="workspace-main">
          <div class="workspace-main__inner">
            @if (logoutError()) { <div class="auth-alert" role="alert">{{ logoutError() }}</div> }
            <div class="breadcrumbs"><a routerLink="/">Головна</a><span>/</span><span>{{ isAdmin ? 'Панель керування' : 'Особистий кабінет' }}</span></div>
            @if (isAdmin && !section) {
              <app-admin-dashboard />
            } @else {
              <div class="workspace-heading"><div><p class="eyebrow"><span class="eyebrow__line"></span> {{ isAdmin ? 'ПАНЕЛЬ КЕРУВАННЯ' : 'ОСОБИСТИЙ КАБІНЕТ' }}</p><h1>{{ isAdmin ? section : 'Мої записи' }}</h1><p>{{ isAdmin ? 'Цей розділ з’явиться на одному з наступних етапів.' : 'Тут ви зможете переглядати свої записи та перебіг ремонту.' }}</p></div><span class="status-pill">У ПІДГОТОВЦІ</span></div>
              <section class="workspace-panel" aria-labelledby="workspace-panel-title"><div class="panel-heading"><div><p class="panel-eyebrow">AUTO / BODY</p><h2 id="workspace-panel-title">{{ isAdmin ? section : 'Записи на огляд' }}</h2></div></div>
                <app-ui-state kind="empty" title="Розділ у підготовці" description="Функції цього розділу ще розробляються." />
              </section>
            }
            <app-api-status />
          </div>
        </main>
      </div>
    </div>
  `,
})
export class WorkspaceShell {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly loggingOut = signal(false);
  readonly logoutError = signal('');
  logout() {
    if (this.loggingOut()) return;
    this.loggingOut.set(true); this.logoutError.set('');
    this.auth.logout().pipe(finalize(() => this.loggingOut.set(false))).subscribe({
      next: () => void this.router.navigateByUrl('/login'),
      error: () => this.logoutError.set('Не вдалося вийти. Перевірте з’єднання та спробуйте ще раз.'),
    });
  }
  readonly isStaff = () => this.auth.session()?.roles.some(role => role === 'Admin' || role === 'SuperAdmin') ?? false;
  private readonly route = inject(ActivatedRoute);
  readonly isAdmin = this.route.snapshot.data['area'] === 'admin';
  readonly routePath = this.route.snapshot.routeConfig?.path ?? 'admin';
  readonly section = this.route.snapshot.data['section'] as string | undefined;
  readonly menuOpen = signal(false);
  readonly primaryItems = this.isAdmin ? ['Огляд', 'Замовлення', 'Розподіл робіт', 'Календар оглядів'] : ['Мої записи', 'Запис на огляд'];
  readonly secondaryItems = this.isAdmin ? ['Працівники', 'Послуги та ціни', 'Користувачі', 'Сповіщення', 'Мій профіль'] : ['Сповіщення', 'Мій профіль'];
}
