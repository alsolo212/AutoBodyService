import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService, destination } from '../../core/auth';
import { RouterLink } from '@angular/router';
import { PublicContacts } from '../../shared/public-contacts/public-contacts';
import { PublicPrices } from '../../shared/public-prices/public-prices';

@Component({
  selector: 'app-public-shell',
  imports: [RouterLink, PublicContacts, PublicPrices],
  template: `
    <a class="skip-link" href="#main">Перейти до вмісту</a>
    <header class="site-header">
      <div class="site-header__inner container">
        <a class="brand" routerLink="/" aria-label="AutoBodyService — головна"><span class="brand__name">AutoBody<span>Service</span><small>СЕРВІС КУЗОВНОГО РЕМОНТУ</small></span></a>
        <nav class="public-nav" aria-label="Основна навігація">
          <a href="#services">Послуги</a><a href="#prices">Ціни</a><a href="#process">Як це працює</a><a href="#contacts">Контакти</a>
        </nav>
        @if (sessionLoading()) { <span class="header-action" role="status">Перевіряємо вхід…</span> } @else { <a class="button button--outline header-action" [routerLink]="sessionTarget()">{{ sessionLabel() }} <span aria-hidden="true">↗</span></a> }
        <details class="public-mobile-nav"><summary>Меню</summary><nav aria-label="Мобільна навігація"><a href="#services">Послуги</a><a href="#prices">Ціни</a><a href="#process">Як це працює</a><a href="#contacts">Контакти</a>@if (!sessionLoading()) { <a [routerLink]="sessionTarget()">{{ sessionLabel() }}</a> } @else { <span role="status">Перевіряємо вхід…</span> }</nav></details>
      </div>
    </header>
    <main id="main">
      <section class="hero">
        <div class="container hero__grid">
          <div class="hero__content">
            <p class="eyebrow"><span class="eyebrow__line"></span> КУЗОВНИЙ РЕМОНТ</p>
            <h1>Увага до деталей.<br><em>Турбота про авто.</em></h1>
            <p class="hero__lead">Від першого огляду до повернення автомобіля — зрозумілий процес і вся інформація про ремонт в одному місці.</p>
            <div class="hero__actions"><a class="button button--primary" routerLink="/account/book">Записатися на огляд <span aria-hidden="true">↗</span></a><a class="text-link" href="#process">Як це працює <span aria-hidden="true">↓</span></a></div>
            @if (!sessionLoading() && auth.session() && !auth.session()?.mustChangePassword) { <p>Сповіщення ще в розробці.</p> }
          </div>
          <aside class="hero-card" aria-label="Етапи обслуговування">
            <div class="hero-card__top"><span>ВАШ АВТОМОБІЛЬ</span><span>01 / 05</span></div>
            <div class="hero-card__body"><span class="hero-card__number">01</span><p>Перший крок</p><h2>Починаємо<br>з огляду.</h2><p>Після огляду обговорюємо обсяг робіт, вартість і терміни ремонту.</p></div>
            <div class="hero-card__bottom"><span>ЗРОЗУМІЛО НА КОЖНОМУ ЕТАПІ</span><span aria-hidden="true">↗</span></div>
          </aside>
        </div>
      </section>
      <section class="section container" id="services">
        <div class="section-heading"><div><p class="eyebrow"><span class="eyebrow__line"></span> НАШ ПІДХІД</p><h2>Ремонт починається<br>з розмови.</h2></div><p>Кожен автомобіль і кожне пошкодження — різні. Тому спершу уважно розглядаємо деталі та узгоджуємо подальші дії.</p></div>
        <div class="principles"><article><span>01</span><h3>Первинний огляд</h3><p>Знайомимося з автомобілем та описом проблеми.</p></article><article><span>02</span><h3>Узгодження</h3><p>Обговорюємо вартість і терміни після огляду.</p></article><article><span>03</span><h3>Прозорий процес</h3><p>Інформація про етапи ремонту — у вашому кабінеті.</p></article></div>
      </section>
      <section class="process-section" id="process"><div class="container process-grid"><div><p class="eyebrow"><span class="eyebrow__line"></span> ПРОСТО ТА ПОСЛІДОВНО</p><h2>Від огляду<br>до видачі авто.</h2></div><ol class="process-list"><li><span>01</span><div><strong>Запис на огляд</strong><p>Оберіть зручний час і опишіть проблему.</p></div></li><li><span>02</span><div><strong>Огляд</strong><p>Оглядаємо автомобіль і визначаємо необхідні роботи.</p></div></li><li><span>03</span><div><strong>Погодження</strong><p>Особисто погоджуємо вартість і терміни ремонту.</p></div></li><li><span>04</span><div><strong>Ремонт</strong><p>Стежте за перебігом у своєму кабінеті.</p></div></li><li><span>05</span><div><strong>Оплата та видача автомобіля</strong><p>Оплата відбувається особисто під час отримання авто.</p></div></li></ol></div></section>
      <section class="section container" id="prices"><p class="eyebrow"><span class="eyebrow__line"></span> ПОСЛУГИ ТА ЦІНИ</p><h2>Орієнтовні ціни</h2><app-public-prices /></section>
      <section class="section container" id="contacts"><p class="eyebrow"><span class="eyebrow__line"></span> ЗВ’ЯЗОК З НАМИ</p><h2>Контакти</h2><app-public-contacts /></section>
    </main>
    <footer class="site-footer"><div class="container footer-inner"><a class="footer-brand" routerLink="/">AutoBody<span>Service</span></a><p>Кузовний ремонт. Увага до кожної деталі.</p><a href="#main">На початок ↑</a></div></footer>
  `,
})
export class PublicShell {
  readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  readonly sessionLoading = signal(true);
  readonly sessionTarget = signal('/login');
  readonly sessionLabel = signal('Увійти');
  constructor() {
    this.auth.getSession().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: session => {
        this.sessionTarget.set(session ? destination(session) : '/login');
        this.sessionLabel.set(session ? (session.mustChangePassword ? 'Змінити пароль' : session.roles.some(role => role === 'Admin' || role === 'SuperAdmin') ? 'Панель керування' : 'Особистий кабінет') : 'Увійти');
        this.sessionLoading.set(false);
      },
      error: () => { this.sessionTarget.set('/login'); this.sessionLabel.set('Перевірити вхід'); this.sessionLoading.set(false); },
    });
  }
}
