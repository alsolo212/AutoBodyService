import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { PublicShell } from './public-shell';

describe('public shell', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ imports: [PublicShell], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function start() {
    const fixture = TestBed.createComponent(PublicShell); fixture.detectChanges();
    http.expectOne('/api/prices').flush([]); http.expectOne('/api/public/contacts').flush({ phone: null, address: null, workingHours: null });
    return fixture;
  }
  it('shows anonymous sign-in, five stages and unconfigured contacts', () => {
    const fixture = start(); http.expectOne('/api/auth/session').flush(null, { status: 401, statusText: 'Unauthorized' }); fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.header-action')?.getAttribute('href')).toBe('/login');
    expect(element.querySelectorAll('#process .process-list li')).toHaveLength(5);
    expect(element.querySelector('#contacts')?.textContent).toContain('ще не налаштовано');
    expect(element.querySelector('#contacts')?.textContent).not.toContain('Години роботи:');
    expect(element.querySelector('a[href="/account/book"]')).not.toBeNull();
  });
  it('uses the verified role session destination', () => {
    const fixture = start(); http.expectOne('/api/auth/session').flush({ name: 'Admin', phone: '', roles: ['Admin'], mustChangePassword: false }); fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.header-action')?.getAttribute('href')).toBe('/admin');
    expect(fixture.nativeElement.querySelector('.header-action')?.textContent).toContain('Панель керування');
  });
});
