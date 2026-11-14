import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Subject, catchError, defer, filter, finalize, map, of, switchMap, take, tap, throwError } from 'rxjs';

export interface AuthSession { name: string; phone: string; roles: string[]; mustChangePassword: boolean; }
export interface Credentials { phone: string; password: string; }
export interface Registration extends Credentials { name: string; }
export function destination(session: AuthSession): string {
  if (session.mustChangePassword) return '/account/change-temporary-password';
  return session.roles.some(role => role === 'Admin' || role === 'SuperAdmin') ? '/admin' : '/account/appointments';
}
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  readonly session = signal<AuthSession | null>(null);
  readonly passwordChanging = signal(false);
  readonly passwordChangeRevision = signal(0);
  readonly passwordChanges = new Subject<'start' | 'end'>();
  private sessionRevision = 0;
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
  getSession() {
    return defer(() => this.passwordChanging()
      ? this.passwordChanges.pipe(filter(phase => phase === 'end'), take(1), switchMap(() => this.readSession()))
      : this.readSession());
  }
  private readSession() {
    const revision = this.sessionRevision;
    return this.http.get<AuthSession>('/api/auth/session').pipe(
      map(session => revision === this.sessionRevision ? session : null),
      tap(session => { if (revision === this.sessionRevision) this.session.set(session); }),
      catchError((error: HttpErrorResponse) => {
        if (revision !== this.sessionRevision) return of(null);
        this.session.set(null);
        return error.status === 401 ? of(null) : throwError(() => error);
      }),
    );
  }
  login(credentials: Credentials) {
    return this.token().pipe(
      switchMap(() => this.http.post<void>('/api/auth/login', credentials)),
      tap(() => { this.sessionRevision++; }),
      switchMap(() => this.getSession()),
      map(session => {
        if (!session) throw new Error('Session unavailable after sign in');
        return session;
      }),
    );
  }
  register(request: Registration) {
    return this.token().pipe(switchMap(() => this.http.post<void>('/api/auth/register', request)));
  }
  changePassword(currentPassword: string, newPassword: string) {
    return defer(() => {
      this.passwordChanging.set(true);
      this.passwordChangeRevision.update(value => value + 1);
      this.passwordChanges.next('start');
      return this.token().pipe(switchMap(() => this.http.post<void>('/api/auth/change-password', { currentPassword, newPassword })),
        finalize(() => { this.passwordChanging.set(false); this.passwordChangeRevision.update(value => value + 1); this.passwordChanges.next('end'); }));
    });
  }
  logout() {
    return this.token().pipe(
      switchMap(() => this.http.post<void>('/api/auth/logout', {})),
      tap(() => { this.sessionRevision++; this.session.set(null); }),
    );
  }
}
