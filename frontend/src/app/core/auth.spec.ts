import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService, destination } from './auth';

describe('AuthService', () => {
  let service: AuthService;
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  it('obtains antiforgery before login and loads the current session', () => {
    const user = { name: 'Test', phone: '+380000000000', roles: ['User'], mustChangePassword: false };
    let result: unknown;
    service.login({ phone: '0000000000', password: 'test-value' }).subscribe(value => result = value);
    http.expectOne('/api/auth/antiforgery').flush(null);
    const login = http.expectOne('/api/auth/login');
    expect(login.request.method).toBe('POST');
    login.flush(null);
    http.expectOne('/api/auth/session').flush(user);
    expect(result).toEqual(user);
    expect(service.session()).toEqual(user);
  });
  it('clears the session on unauthorized responses but reports network errors', () => {
    let value: unknown = 'pending';
    service.getSession().subscribe(result => value = result);
    http.expectOne('/api/auth/session').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(value).toBeNull();
    let status = -1;
    service.getSession().subscribe({ error: error => status = error.status });
    http.expectOne('/api/auth/session').error(new ProgressEvent('error'));
    expect(status).toBe(0);
  });
  it('refreshes antiforgery before logout and clears session on success', () => {
    service.session.set({ name: 'Test', phone: '+380000000000', roles: ['User'], mustChangePassword: false });
    service.logout().subscribe();
    http.expectOne('/api/auth/antiforgery').flush(null);
    const logout = http.expectOne('/api/auth/logout');
    expect(logout.request.method).toBe('POST');
    logout.flush(null);
    expect(service.session()).toBeNull();
  });
});

describe('destination', () => {
  it('prioritizes password changes and sends admin roles to the admin area', () => {
    const user = { name: 'Test', phone: '+380000000000', roles: ['User'], mustChangePassword: false };
    expect(destination(user)).toBe('/account/appointments');
    expect(destination({ ...user, roles: ['Admin'] })).toBe('/admin');
    expect(destination({ ...user, roles: ['SuperAdmin'] })).toBe('/admin');
    expect(destination({ ...user, roles: ['Admin'], mustChangePassword: true })).toBe('/account/change-temporary-password');
  });
});
