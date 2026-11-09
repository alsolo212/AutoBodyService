import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PublicContacts } from './public-contacts';

describe('public contacts', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [PublicContacts], providers: [provideHttpClient(), provideHttpClientTesting()] }));

  it('shows configured public contact details and an accessible telephone link', () => {
    const fixture = TestBed.createComponent(PublicContacts);
    TestBed.inject(HttpTestingController).expectOne('/api/public/contacts').flush({ phone: '+380000000000', address: 'Тестова адреса', workingHours: 'За домовленістю' });
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('a')?.getAttribute('href')).toBe('tel:+380000000000');
    expect(element.textContent).toContain('Тестова адреса');
    expect(element.textContent).toContain('За домовленістю');
  });

  it('explains when the phone is not configured', () => {
    const fixture = TestBed.createComponent(PublicContacts);
    TestBed.inject(HttpTestingController).expectOne('/api/public/contacts').flush({ phone: null, address: null, workingHours: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Контактний телефон СТО ще не налаштовано');
    expect(fixture.nativeElement.querySelector('a')).toBeNull();
  });

  it('allows a retry after a failed request', () => {
    const fixture = TestBed.createComponent(PublicContacts);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/public/contacts').flush(null, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role=alert]')).not.toBeNull();
    fixture.nativeElement.querySelector('button').click();
    http.expectOne('/api/public/contacts').flush({ phone: null, address: null, workingHours: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('ще не налаштовано');
    http.verify();
  });
});
