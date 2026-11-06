import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-public-shell',
  imports: [RouterLink],
  template: `
    <a class="skip-link" href="#main">Перейти до вмісту</a>
    <header class="site-header">
      <div class="site-header__inner container">
        <a class="brand" routerLink="/" aria-label="AutoBodyService — головна"><span class="brand__name">AutoBody<span>Service</span><small>СЕРВІС КУЗОВНОГО РЕМОНТУ</small></span></a>
        <nav class="public-nav" aria-label="Основна навігація">
          <a href="#services">Послуги</a><a href="#process">Як це працює</a>
        </nav>
        <a class="button button--outline header-action" routerLink="/account/appointments">Особистий кабінет <span aria-hidden="true">↗</span></a>
        <details class="public-mobile-nav"><summary>Меню</summary><nav aria-label="Мобільна навігація"><a href="#services">Послуги</a><a href="#process">Як це працює</a><a routerLink="/account/appointments">Особистий кабінет</a></nav></details>
      </div>
    </header>
    <main id="main">
      <section class="hero">
        <div class="container hero__grid">
          <div class="hero__content">
            <p class="eyebrow"><span class="eyebrow__line"></span> КУЗОВНИЙ РЕМОНТ</p>
            <h1>Увага до деталей.<br><em>Турбота про авто.</em></h1>
            <p class="hero__lead">Від першого огляду до повернення автомобіля — зрозумілий процес і вся інформація про ремонт в одному місці.</p>
            <div class="hero__actions"><a class="button button--primary" routerLink="/account/appointments">Перейти до кабінету <span aria-hidden="true">↗</span></a><a class="text-link" href="#process">Як це працює <span aria-hidden="true">↓</span></a></div>
          </div>
          <aside class="hero-card" aria-label="Етапи обслуговування">
            <div class="hero-card__top"><span>ВАШ АВТОМОБІЛЬ</span><span>01 / 04</span></div>
            <div class="hero-card__body"><span class="hero-card__number">01</span><p>Перший крок</p><h2>Починаємо<br>з огляду.</h2><p>Після огляду обговорюємо обсяг робіт, вартість і терміни ремонту.</p></div>
            <div class="hero-card__bottom"><span>ЗРОЗУМІЛО НА КОЖНОМУ ЕТАПІ</span><span aria-hidden="true">↗</span></div>
          </aside>
        </div>
      </section>
      <section class="section container" id="services">
        <div class="section-heading"><div><p class="eyebrow"><span class="eyebrow__line"></span> НАШ ПІДХІД</p><h2>Ремонт починається<br>з розмови.</h2></div><p>Кожен автомобіль і кожне пошкодження — різні. Тому спершу уважно розглядаємо деталі та узгоджуємо подальші дії.</p></div>
        <div class="principles"><article><span>01</span><h3>Первинний огляд</h3><p>Знайомимося з автомобілем та описом проблеми.</p></article><article><span>02</span><h3>Узгодження</h3><p>Обговорюємо вартість і терміни після огляду.</p></article><article><span>03</span><h3>Прозорий процес</h3><p>Інформація про етапи ремонту — у вашому кабінеті.</p></article></div>
      </section>
      <section class="process-section" id="process"><div class="container process-grid"><div><p class="eyebrow"><span class="eyebrow__line"></span> ПРОСТО ТА ПОСЛІДОВНО</p><h2>Від огляду<br>до видачі авто.</h2></div><ol class="process-list"><li><span>01</span><div><strong>Запис на огляд</strong><p>Оберіть зручний час і опишіть проблему.</p></div></li><li><span>02</span><div><strong>Огляд та узгодження</strong><p>Особисто погоджуємо деталі ремонту.</p></div></li><li><span>03</span><div><strong>Ремонт</strong><p>Стежте за перебігом у своєму кабінеті.</p></div></li><li><span>04</span><div><strong>Отримання автомобіля</strong><p>Оплата відбувається під час отримання авто.</p></div></li></ol></div></section>
    </main>
    <footer class="site-footer"><div class="container footer-inner"><a class="footer-brand" routerLink="/">AutoBody<span>Service</span></a><p>Кузовний ремонт. Увага до кожної деталі.</p><a href="#main">На початок ↑</a></div></footer>
  `,
})
export class PublicShell {}
