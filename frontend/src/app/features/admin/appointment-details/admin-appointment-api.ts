import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map, switchMap } from 'rxjs';
import { AppointmentDetails, AppointmentStatus, RepairPlan, StatusHistory } from '../../appointments/appointments-api';

export type ContactReason = 'NewAppointment' | 'ScheduleConflictOrReschedule' | 'Agreement' | 'ReadyForPayment' | 'RepairDateConflict';
export interface AdminStatusHistory extends StatusHistory { actorName: string; reason: string | null; isCorrection?: boolean; previousInspectionStartUtc?: string | null; newInspectionStartUtc?: string | null; }
export interface ManualContact { reason: ContactReason; actorName: string; recordedAtUtc: string; }
export interface PaymentConfirmation { actorName: string; paidAtUtc: string; handedOverAtUtc: string; }
export interface AdminAppointment extends Omit<AppointmentDetails, 'history'> {
  customerName: string; customerPhone: string; version: number; history: AdminStatusHistory[];
  paymentConfirmation: PaymentConfirmation | null; manualContacts: ManualContact[];
  paymentHistory?: PaymentConfirmation[];
  assignments: { id: string; employeeId: string; name: string; specialization: string; assignedById: string; assignedByName: string; assignedAtUtc: string; releasedAtUtc: string | null; releasedById: string | null; releasedByName: string | null }[] | null;
  activePause?: { id: string; reason: string; durationDays: number; workplaceReleased: boolean; startedOn: string; expectedReturn: string } | null;
  dateConflict?: boolean; alternativeStart?: string | null;
  pauseHistory?: { reason: string; durationDays: number; workplaceReleased: boolean; startedOn: string; expectedReturn: string; startedAtUtc: string; endedAtUtc: string | null }[];
}
export interface RepairPlanChange { version: number; price: string; plannedStartUtc: string; estimatedFinishUtc: string; inspectionPerformed: boolean; customerAgreed?: boolean; priceReason?: string; }
export interface TransitionChange { version: number; target: AppointmentStatus; inspectionPerformed?: boolean; paymentReceived?: boolean; vehicleHandedOver?: boolean; }
export interface AdminBoardCard { id: string; customerName: string; vin: string | null; make: string; model: string; inspectionStartUtc: string; inspectionStartLocal: string; status: AppointmentStatus; repairPlan: RepairPlan | null; version: number; dateConflict?: boolean; }
export interface AdminBoardPage { items: AdminBoardCard[]; page: number; pageSize: number; total: number; }
export interface AdminBoardFilter { search: string; from: string; to: string; sort: string; direction: string; pageSize: number; }
export interface DashboardItem { id: string; customerName: string; make: string; model: string; status: AppointmentStatus; inspectionStartUtc: string; inspectionStartLocal: string; occurredAtUtc: string; }
export interface ContactTask { appointmentId: string; customerName: string; make: string; model: string; reason: ContactReason; triggeredAtUtc: string | null; }
export interface AdminDashboard { counts: { awaitingInspection: number; scheduled: number; inProgress: number; awaitingPayment: number }; upcomingInspections: DashboardItem[]; newAppointments: DashboardItem[]; cancellations: DashboardItem[]; contactTasks: ContactTask[]; }

@Injectable({ providedIn: 'root' })
export class AdminAppointmentApi {
  private readonly http = inject(HttpClient);
  dashboard() { return this.http.get<AdminDashboard>('/api/admin/dashboard'); }
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
  plan(id: string, body: RepairPlanChange) {
    // The validated decimal literal is sent unchanged, avoiding JavaScript number conversion.
    const { price, ...fields } = body;
    const json = JSON.stringify(fields).slice(0, -1) + ',"price":' + price + '}';
    return this.token().pipe(switchMap(() => this.http.put<void>(`${this.path(id)}/repair-plan`, json, { headers: new HttpHeaders({ 'Content-Type': 'application/json' }) })));
  }
  schedule(id: string, body: { version: number; price: string; start: string; durationDays: number; inspectionPerformed: boolean; priceReason?: string }) {
    const { price, ...fields } = body;
    const json = JSON.stringify(fields).slice(0, -1) + ',"price":' + price + '}';
    return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/schedule`, json, { headers: new HttpHeaders({ 'Content-Type': 'application/json' }) })));
  }
  transition(id: string, body: TransitionChange) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/transition`, body))); }
  pause(id: string, body: { version: number; reason: string; durationDays: number; workplaceReleased: boolean }) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/pause`, body))); }
  resume(id: string, version: number) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/resume`, { version }))); }
  cancel(id: string, version: number, reason: string) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/cancel`, { version, confirmed: true, reason }))); }
  correctStatus(id: string, body: { version: number; target: AppointmentStatus; reason: string; confirmed: boolean; inspectionStartUtc?: string }) {
    return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/correct-status`, body)));
  }
  contact(id: string, reason: ContactReason, snapshot: { version: number; customerPhone: string; customerName: string }) { return this.token().pipe(switchMap(() => this.http.post<void>(`${this.path(id)}/manual-contact`, { reason, ...snapshot }))); }
  private path(id: string) { return `/api/admin/appointments/${encodeURIComponent(id)}`; }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
