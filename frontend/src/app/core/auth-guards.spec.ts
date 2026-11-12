import { TestBed } from '@angular/core/testing';
import { GuardResult, provideRouter, Router, UrlTree } from '@angular/router';
import { firstValueFrom, Observable, Subject, throwError } from 'rxjs';
import { AuthService, AuthSession } from './auth';
import { authGuard, guestGuard } from './auth-guards';

const account = (role: string, mustChangePassword = false): AuthSession => ({ name: 'Test', phone: '+380000000000', roles: [role], mustChangePassword });

describe('guestGuard', () => {
  let session: Subject<AuthSession | null>;
  beforeEach(() => {
    session = new Subject<AuthSession | null>();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { getSession: () => session.asObservable() } }] });
  });
  const invoke = () => TestBed.runInInjectionContext(() => guestGuard({} as never, {} as never));

  it('waits for the server session before allowing a guest', async () => {
    let resolved = false;
    const result = firstValueFrom(invoke() as Observable<GuardResult>).then(value => { resolved = true; return value; });
    expect(resolved).toBe(false);
    session.next(null);
    expect(await result).toBe(true);
  });

  for (const [role, mustChange, expected] of [
    ['User', false, '/account/appointments'], ['Admin', false, '/admin'],
    ['SuperAdmin', false, '/admin'], ['User', true, '/account/change-temporary-password'],
    ['Admin', true, '/account/change-temporary-password'],
  ] as const) {
    it(`redirects ${role}, mandatory change ${mustChange}, to ${expected}`, async () => {
      const result = firstValueFrom(invoke() as Observable<GuardResult>);
      session.next(account(role, mustChange));
      expect(TestBed.inject(Router).serializeUrl(await result as UrlTree)).toBe(expected);
    });
  }

  it('allows the login page on a session lookup failure without redirecting in a loop', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { getSession: () => throwError(() => new Error('offline')) } }] });
    expect(await firstValueFrom(invoke() as Observable<GuardResult>)).toBe(true);
  });
});

describe('admin authGuard', () => {
  let session: Subject<AuthSession | null>;
  beforeEach(() => {
    session = new Subject<AuthSession | null>();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { getSession: () => session.asObservable() } }] });
  });
  for (const role of ['Admin', 'SuperAdmin']) {
    it(`allows ${role} to the common dashboard`, async () => {
      const result = firstValueFrom(TestBed.runInInjectionContext(() => authGuard({ data: { area: 'admin' } } as never, {} as never)) as Observable<GuardResult>);
      session.next(account(role)); expect(await result).toBe(true);
    });
  }
  it('redirects User away from the dashboard', async () => {
    const result = firstValueFrom(TestBed.runInInjectionContext(() => authGuard({ data: { area: 'admin' } } as never, {} as never)) as Observable<GuardResult>);
    session.next(account('User'));
    expect(TestBed.inject(Router).serializeUrl(await result as UrlTree)).toBe('/account/appointments');
  });
});
