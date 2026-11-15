import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export interface ManagedUser { id: string; name: string; phone: string; isAdmin: boolean; isBlocked: boolean; version: string; }
export interface TemporaryPassword { temporaryPassword: string; }

@Injectable({ providedIn: 'root' })
export class UsersApi {
  private readonly http = inject(HttpClient);
  private readonly path = '/api/admin/users';
  search(term: string) {
    return this.http.get<ManagedUser[]>(this.path, { params: new HttpParams().set('search', term) });
  }
  reset(user: ManagedUser) {
    return this.token().pipe(switchMap(() => this.http.post<TemporaryPassword>(`${this.path}/${encodeURIComponent(user.id)}/reset-password`, { version: user.version })));
  }
  role(user: ManagedUser) {
    return this.token().pipe(switchMap(() => this.http.put<void>(`${this.path}/${encodeURIComponent(user.id)}/roles`, { version: user.version, isAdmin: !user.isAdmin })));
  }
  block(user: ManagedUser) {
    return this.token().pipe(switchMap(() => this.http.patch<void>(`${this.path}/${encodeURIComponent(user.id)}/block`, { version: user.version, isBlocked: !user.isBlocked })));
  }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
