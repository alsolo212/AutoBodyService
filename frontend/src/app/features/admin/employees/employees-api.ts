import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { switchMap } from 'rxjs';

// Assignment information is deliberately absent until the API supplies it in tasks 37–38.
export interface Employee { id: string; fullName: string; specialization: string; isActive: boolean; version: number; }
export interface EmployeeDetailsChange { fullName: string; specialization: string; }

@Injectable({ providedIn: 'root' })
export class EmployeesApi {
  private readonly http = inject(HttpClient);
  private readonly path = '/api/admin/employees';
  list() { return this.http.get<Employee[]>(this.path); }
  create(body: EmployeeDetailsChange) {
    return this.token().pipe(switchMap(() => this.http.post<Employee>(this.path, { ...body, isActive: true })));
  }
  update(id: string, version: number, body: Partial<EmployeeDetailsChange> & { isActive?: boolean }) {
    return this.token().pipe(switchMap(() => this.http.patch<Employee>(`${this.path}/${encodeURIComponent(id)}`, { version, ...body })));
  }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
