import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { catchError, map, of, switchMap, tap, throwError } from 'rxjs';

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
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
  getSession() {
    return this.http.get<AuthSession>('/api/auth/session').pipe(
      tap(session => this.session.set(session)),
      catchError((error: HttpErrorResponse) => {
        this.session.set(null);
        return error.status === 401 ? of(null) : throwError(() => error);
      }),
    );
  }
  login(credentials: Credentials) {
    return this.token().pipe(
      switchMap(() => this.http.post<void>('/api/auth/login', credentials)),
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
  logout() {
    return this.token().pipe(
      switchMap(() => this.http.post<void>('/api/auth/logout', {})),
      tap(() => this.session.set(null)),
    );
  }
}
