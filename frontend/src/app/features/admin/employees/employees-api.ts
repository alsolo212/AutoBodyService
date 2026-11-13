import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { switchMap } from 'rxjs';

export interface EmployeeCurrentAssignment { id: string; appointmentId: string; }
export interface Employee { id: string; fullName: string; specialization: string; isActive: boolean; version: number; currentAssignments: EmployeeCurrentAssignment[]; }
export interface ActiveAssignment { id: string; appointmentId: string; employeeId: string; }
export interface EmployeeDetailsChange { fullName: string; specialization: string; }

@Injectable({ providedIn: 'root' })
export class EmployeesApi {
  private readonly http = inject(HttpClient);
  private readonly path = '/api/admin/employees';
  list() { return this.http.get<Employee[]>(this.path); }
  activeAssignments() { return this.http.get<ActiveAssignment[]>('/api/admin/assignments?active=true'); }
  assign(appointmentId: string, employeeId: string) {
    return this.token().pipe(switchMap(() => this.http.post<ActiveAssignment>('/api/admin/assignments', { appointmentId, employeeId })));
  }
  release(id: string) {
    return this.token().pipe(switchMap(() => this.http.patch<void>(`/api/admin/assignments/${encodeURIComponent(id)}/release`, null)));
  }
  create(body: EmployeeDetailsChange) {
    return this.token().pipe(switchMap(() => this.http.post<Employee>(this.path, { ...body, isActive: true })));
  }
  update(id: string, version: number, body: Partial<EmployeeDetailsChange> & { isActive?: boolean }) {
    return this.token().pipe(switchMap(() => this.http.patch<Employee>(`${this.path}/${encodeURIComponent(id)}`, { version, ...body })));
  }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
