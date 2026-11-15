import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService } from '../../../core/auth';
import { ManagedUser } from './users-api';
import { Users } from './users';

const customer: ManagedUser = { id: 'user-1', name: 'Олена', phone: '+380000000001', isAdmin: false, isBlocked: false, version: 'v1' };
const admin: ManagedUser = { id: 'admin-1', name: 'Адміністратор', phone: '+380000000002', isAdmin: true, isBlocked: false, version: 'v2' };
const session = (role: string) => ({ name: 'Оператор', phone: '+380000000099', roles: [role], mustChangePassword: false });

describe('Users', () => {
  let http: HttpTestingController;
  let auth: AuthService;
  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [Users], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController); auth = TestBed.inject(AuthService);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  });
  afterEach(() => { http.verify(); localStorage.clear(); sessionStorage.clear(); });
  function searchRequest() { return http.expectOne(req => req.url === '/api/admin/users'); }
  function create(role = 'SuperAdmin', items: ManagedUser[] = [customer, admin]) {
    auth.session.set(session(role));
    const fixture = TestBed.createComponent(Users); fixture.detectChanges();
    searchRequest().flush(items); fixture.detectChanges();
    const dialogs = fixture.nativeElement.querySelectorAll('dialog') as NodeListOf<HTMLDialogElement>;
    for (const dialog of dialogs) {
      dialog.showModal = vi.fn(() => dialog.setAttribute('open', ''));
      dialog.close = vi.fn(() => dialog.removeAttribute('open'));
    }
    return fixture;
  }
  function confirm(fixture: ReturnType<typeof create>, action: 'reset' | 'role' | 'block', user: ManagedUser) {
    fixture.componentInstance.ask(action, user); fixture.detectChanges();
    const dialog: HTMLDialogElement = fixture.nativeElement.querySelector('app-confirm-dialog dialog');
    expect(dialog.open).toBe(true); http.expectNone('/api/auth/antiforgery');
    (dialog.querySelector('.button--danger') as HTMLButtonElement).click();
    http.expectOne('/api/auth/antiforgery').flush(null);
  }
  it('searches by name or phone and only renders ordinary users for Admin', () => {
    const fixture = create('Admin');
    expect(fixture.nativeElement.textContent).toContain('Олена');
    expect(fixture.nativeElement.textContent).not.toContain('Адміністратор');
    expect(fixture.nativeElement.querySelectorAll('.user-actions button')).toHaveLength(1);
    fixture.componentInstance.searchForm.controls.search.setValue('+380000000001'); fixture.componentInstance.load();
    const request = searchRequest(); expect(request.request.params.get('search')).toBe('+380000000001');
    request.flush([customer]); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(customer.phone);
  });
  it('confirms reset and displays the secret once without browser storage', () => {
    const fixture = create('Admin', [customer]);
    confirm(fixture, 'reset', customer);
    const request = http.expectOne('/api/admin/users/user-1/reset-password');
    expect(request.request.body).toEqual({ version: 'v1' });
    request.flush({ temporaryPassword: 'example-temporary-secret' });
    searchRequest().flush([{ ...customer, version: 'v3' }]); fixture.detectChanges();
    const dialog: HTMLDialogElement = fixture.nativeElement.querySelector('.secret-dialog');
    expect(dialog.open).toBe(true); expect(dialog.textContent).toContain('example-temporary-secret');
    expect(dialog.textContent).toContain('вручну поза системою');
    expect(JSON.stringify(localStorage)).not.toContain('example-temporary-secret');
    expect(JSON.stringify(sessionStorage)).not.toContain('example-temporary-secret');
    expect(location.href).not.toContain('example-temporary-secret');
    (dialog.querySelector('button') as HTMLButtonElement).click(); fixture.detectChanges();
    expect(dialog.open).toBe(false); expect(dialog.textContent).not.toContain('example-temporary-secret');
    expect(fixture.componentInstance.secret()).toBeNull();
  });
  it('sends versioned role and block changes after confirmation and reloads server state', () => {
    const fixture = create();
    confirm(fixture, 'role', admin);
    const role = http.expectOne('/api/admin/users/admin-1/roles');
    expect(role.request.method).toBe('PUT'); expect(role.request.body).toEqual({ version: 'v2', isAdmin: false });
    role.flush(null); searchRequest().flush([customer, { ...admin, isAdmin: false, version: 'v3' }]); fixture.detectChanges();
    expect(fixture.componentInstance.users()[1].isAdmin).toBe(false);
    const refreshed = fixture.componentInstance.users()[1];
    confirm(fixture, 'block', refreshed);
    const block = http.expectOne('/api/admin/users/admin-1/block');
    expect(block.request.method).toBe('PATCH'); expect(block.request.body).toEqual({ version: 'v3', isBlocked: true });
    block.flush(null); searchRequest().flush([customer, { ...refreshed, isBlocked: true, version: 'v4' }]); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Заблоковано');
  });
  it.each([404, 409])('refreshes after status %i without assuming the action succeeded', status => {
    const fixture = create(); confirm(fixture, 'block', customer);
    http.expectOne('/api/admin/users/user-1/block').flush(null, { status, statusText: 'Error' });
    searchRequest().flush([{ ...customer, version: 'new' }]); fixture.detectChanges();
    expect(fixture.componentInstance.actionError()).toContain(status === 404 ? 'не знайдено' : 'змінилися');
    expect(fixture.componentInstance.users()[0].version).toBe('new');
    expect(fixture.componentInstance.notice()).toBe('');
  });
  it('revalidates a forbidden action and clears a revoked session', () => {
    const fixture = create(); const navigate = vi.mocked(TestBed.inject(Router).navigateByUrl);
    confirm(fixture, 'block', customer);
    http.expectOne('/api/admin/users/user-1/block').flush(null, { status: 403, statusText: 'Forbidden' });
    expect(fixture.componentInstance.actionError()).toContain('Недостатньо прав');
    http.expectOne('/api/auth/session').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges(); expect(auth.session()).toBeNull(); expect(navigate).toHaveBeenCalledWith('/login');
    expect(fixture.componentInstance.users()).toEqual([]);
  });
  it('clears the secret and ignores a late reset response after session revocation', () => {
    const fixture = create('Admin', [customer]);
    confirm(fixture, 'reset', customer);
    const request = http.expectOne('/api/admin/users/user-1/reset-password');
    auth.invalidateSession();
    request.flush({ temporaryPassword: 'must-not-appear' }); fixture.detectChanges();
    expect(fixture.componentInstance.secret()).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('must-not-appear');
    http.expectNone(req => req.url === '/api/admin/users');
  });
  it('clears a shown secret when the current session is revoked', () => {
    const fixture = create('Admin', [customer]); confirm(fixture, 'reset', customer);
    http.expectOne('/api/admin/users/user-1/reset-password').flush({ temporaryPassword: 'visible-once' });
    searchRequest().flush([customer]); fixture.detectChanges();
    auth.invalidateSession(); fixture.detectChanges();
    expect(fixture.componentInstance.secret()).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('visible-once');
  });
  it('discards the secret when the page is hidden for navigation', () => {
    const fixture = create('Admin', [customer]); confirm(fixture, 'reset', customer);
    http.expectOne('/api/admin/users/user-1/reset-password').flush({ temporaryPassword: 'pagehide-secret' });
    searchRequest().flush([customer]); fixture.detectChanges();
    window.dispatchEvent(new Event('pagehide')); fixture.detectChanges();
    expect(fixture.componentInstance.secret()).toBeNull();
    expect(fixture.nativeElement.querySelector('.temporary-password')).toBeNull();
  });
  it('cancels an older search instead of showing outdated results', () => {
    const fixture = create();
    fixture.componentInstance.searchForm.controls.search.setValue('old'); fixture.componentInstance.load();
    const old = searchRequest();
    fixture.componentInstance.searchForm.controls.search.setValue('new'); fixture.componentInstance.load();
    expect(old.cancelled).toBe(true);
    const current = searchRequest(); expect(current.request.params.get('search')).toBe('new');
    current.flush([customer]); fixture.detectChanges();
    expect(fixture.componentInstance.users()).toEqual([customer]);
  });
  it('invalidates the current session on an unauthorized action', () => {
    const fixture = create('Admin', [customer]); confirm(fixture, 'reset', customer);
    http.expectOne('/api/admin/users/user-1/reset-password').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges(); expect(auth.session()).toBeNull();
    expect(TestBed.inject(Router).navigateByUrl).toHaveBeenCalledWith('/login');
    expect(fixture.componentInstance.users()).toEqual([]);
  });
});
