import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { API_BASE_URL } from '../../core/api-config';

interface PublicContactsData { phone: string | null; address: string | null; workingHours: string | null; }

@Component({
  selector: 'app-public-contacts',
  template: `
    @if (loading()) { <p>Завантажуємо контактні дані…</p> }
    @else if (failed()) {
      <p role="alert">Контактні дані тимчасово недоступні.</p>
      <button type="button" (click)="load()">Спробувати ще раз</button>
    } @else if (contacts(); as info) {
      @if (info.phone) { <p>Телефон: <a [href]="'tel:' + info.phone">{{ info.phone }}</a></p> }
      @else { <p>Контактний телефон СТО ще не налаштовано. Зверніться до персоналу СТО особисто.</p> }
      @if (info.address) { <p>Адреса: {{ info.address }}</p> }
      @if (info.workingHours) { <p>Години роботи: {{ info.workingHours }}</p> }
    }
  `,
  styles: `:host { display: block; overflow-wrap: anywhere; } a { color: inherit; text-decoration: underline; } button { cursor: pointer; font: inherit; }`,
})
export class PublicContacts {
  private readonly http = inject(HttpClient);
  readonly contacts = signal<PublicContactsData | null>(null);
  readonly loading = signal(true);
  readonly failed = signal(false);

  constructor() { this.load(); }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.http.get<PublicContactsData>(`${API_BASE_URL}/public/contacts`).subscribe({
      next: contacts => { this.contacts.set(contacts); this.loading.set(false); },
      error: () => { this.failed.set(true); this.loading.set(false); },
    });
  }
}
