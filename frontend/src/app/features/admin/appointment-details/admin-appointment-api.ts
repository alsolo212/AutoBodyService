import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, switchMap } from 'rxjs';
import { AppointmentDetails, AppointmentStatus, RepairPlan, StatusHistory } from '../../appointments/appointments-api';

export type ContactReason = 'NewAppointment' | 'ScheduleConflictOrReschedule' | 'Agreement' | 'ReadyForPayment';
export interface AdminStatusHistory extends StatusHistory { actorName: string; reason: string | null; }
export interface ManualContact { reason: ContactReason; actorName: string; recordedAtUtc: string; }
export interface PaymentConfirmation { actorName: string; paidAtUtc: string; handedOverAtUtc: string; }
export interface AdminAppointment extends Omit<AppointmentDetails, 'history'> {
  customerName: string; customerPhone: string; version: number; history: AdminStatusHistory[];
  paymentConfirmation: PaymentConfirmation | null; manualContacts: ManualContact[];
  assignments: { name: string; specialization: string; assignedAtUtc: string; releasedAtUtc: string | null }[] | null;
}
export interface RepairPlanChange { version: number; price: number; plannedStartUtc: string; estimatedFinishUtc: string; inspectionPerformed: boolean; }
export interface TransitionChange { version: number; target: AppointmentStatus; inspectionPerformed?: boolean; paymentReceived?: boolean; vehicleHandedOver?: boolean; }
export interface AdminBoardCard { id: string; customerName: string; vin: string; make: string; model: string; inspectionStartUtc: string; inspectionStartLocal: string; status: AppointmentStatus; repairPlan: RepairPlan | null; version: number; }
export interface AdminBoardPage { items: AdminBoardCard[]; page: number; pageSize: number; total: number; }
export interface AdminBoardFilter { search: string; from: string; to: string; sort: string; direction: string; pageSize: number; }

@Injectable({ providedIn: 'root' })
export class AdminAppointmentApi {
  private readonly http = inject(HttpClient);
  list(status: AppointmentStatus, page: number, filter: AdminBoardFilter) {
    let params = new HttpParams().set('status', status).set('page', page).set('pageSize', filter.pageSize).set('sort', filter.sort).set('direction', filter.direction);
    if (filter.search.trim()) params = params.set('search', filter.search.trim());
    if (filter.from) params = params.set('from', filter.from);
    if (filter.to) params = params.set('to', filter.to);
    return this.http.get<AdminBoardPage>('/api/admin/appointments', { params });
  }
  details(id: string) {
    return this.http.get<AdminAppointment>(`/api/appointments/${encodeURIComponent(id)}`).pipe(map(item => {
      if (!item || typeof item.customerName !== 'string' || typeof item.customerPhone !== 'string' ||
          typeof item.version !== 'number' || !Array.isArray(item.manualContacts))
        throw new HttpErrorResponse({ status: 403, statusText: 'Administrative details unavailable' });
      return item;
    }));
  }
  plan(id: string, body: RepairPlanChange) { return this.token().pipe(switchMap(() => this.http.put<void>(`${this.path(id)}/repair-plan`, body))); }
  transition(id: string, body: TransitionChange) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/transition`, body))); }
  cancel(id: string, version: number, reason: string) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/cancel`, { version, confirmed: true, reason }))); }
  contact(id: string, reason: ContactReason) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/manual-contact`, { reason }))); }
  private path(id: string) { return `/api/admin/appointments/${encodeURIComponent(id)}`; }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
