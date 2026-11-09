import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { UiState } from '../../shared/ui-state/ui-state';
import { ApiStatus } from '../../core/api-status/api-status';
import { AuthService } from '../../core/auth';
import { finalize } from 'rxjs';

@Component({
  selector: 'app-workspace-shell',
  imports: [RouterLink, UiState, ApiStatus],
  template: `
    <a class="skip-link" [attr.href]="isAdmin ? '/admin#workspace-main' : '/account/appointments#workspace-main'">Перейти до вмісту</a>
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
              @for (item of primaryItems; track item) { <li><span class="sidebar__item" [class.sidebar__item--active]="$first" [attr.aria-current]="$first ? 'page' : null">{{ item }}</span></li> }
            </ul>
            <p class="sidebar__label">{{ isAdmin ? 'КЕРУВАННЯ' : 'ОСОБИСТЕ' }}</p>
            <ul>@for (item of secondaryItems; track item) { <li>@if (item === 'Мій профіль') { <a class="sidebar__item" [routerLink]="isAdmin ? '/admin/profile' : '/account/profile'">{{ item }}</a> } @else { <span class="sidebar__item">{{ item }}</span> }</li> }</ul>
          </nav>
          <div class="sidebar__note"><span>УВАГА ДО ДЕТАЛЕЙ</span><p>Усе важливе про автомобіль — в одному місці.</p></div>
        </aside>
        <main class="workspace-main" id="workspace-main">
          <div class="workspace-main__inner">
            @if (logoutError()) { <div class="auth-alert" role="alert">{{ logoutError() }}</div> }
            <div class="breadcrumbs"><a routerLink="/">Головна</a><span>/</span><span>{{ isAdmin ? 'Панель керування' : 'Особистий кабінет' }}</span></div>
            <div class="workspace-heading"><div><p class="eyebrow"><span class="eyebrow__line"></span> {{ isAdmin ? 'ПАНЕЛЬ КЕРУВАННЯ' : 'ОСОБИСТИЙ КАБІНЕТ' }}</p><h1>{{ isAdmin ? 'Робочий простір' : 'Мої записи' }}</h1><p>{{ isAdmin ? 'Замовлення, розклад і робота команди — в одному місці.' : 'Тут ви зможете переглядати свої записи та перебіг ремонту.' }}</p></div><span class="status-pill">У ПІДГОТОВЦІ</span></div>
            <section class="workspace-panel" aria-labelledby="workspace-panel-title"><div class="panel-heading"><div><p class="panel-eyebrow">{{ isAdmin ? 'ОГЛЯД' : 'ВАШІ АВТОМОБІЛІ' }}</p><h2 id="workspace-panel-title">{{ isAdmin ? 'Панель керування' : 'Записи на огляд' }}</h2></div><span class="panel-tag">AUTO / BODY</span></div>
              <app-ui-state kind="empty" title="Розділ у підготовці" [description]="isAdmin ? 'Функції керування з’являться після підключення сервісу.' : 'Запис на огляд і перегляд замовлень з’являться після підключення сервісу.'" />
            </section>
            <p class="workspace-footnote">{{ isAdmin ? 'Доступ до даних і дій перевірятиметься сервером.' : 'Інформація про ваші записи буде доступна після входу.' }}</p>
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
  readonly isAdmin = inject(ActivatedRoute).snapshot.data['area'] === 'admin';
  readonly menuOpen = signal(false);
  readonly primaryItems = this.isAdmin ? ['Огляд', 'Замовлення', 'Розподіл робіт', 'Календар оглядів'] : ['Мої записи', 'Запис на огляд'];
  readonly secondaryItems = this.isAdmin ? ['Працівники', 'Послуги та ціни', 'Користувачі', 'Сповіщення', 'Мій профіль', 'Контроль та звітність'] : ['Сповіщення', 'Мій профіль'];
}
