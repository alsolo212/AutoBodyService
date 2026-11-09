import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export type AppointmentStatus = 'AwaitingInspection' | 'Scheduled' | 'InProgress' | 'AwaitingPayment' | 'Completed' | 'Cancelled';
export type AppointmentCategory = 'all' | 'active' | 'completed' | 'cancelled';
export interface RepairPlan { agreedPrice: number; currency: string; plannedStartUtc: string; estimatedFinishUtc: string; }
export interface AppointmentSummary { id: string; make: string; model: string; maskedVin: string; inspectionStartUtc: string; status: AppointmentStatus; repairPlan: RepairPlan | null; }
export interface StatusHistory { fromStatus: AppointmentStatus | null; toStatus: AppointmentStatus; changedAtUtc: string; }
export interface Participant { name: string; specialization: string; }
export interface AppointmentDetails { id: string; vin: string; make: string; model: string; problemDescription: string; inspectionStartUtc: string; status: AppointmentStatus; history: StatusHistory[]; repairPlan: RepairPlan | null; participants: Participant[] | null; }

const labels: Record<AppointmentStatus, string> = { AwaitingInspection: 'Очікує огляду', Scheduled: 'Заплановано', InProgress: 'У роботі', AwaitingPayment: 'Очікує оплати', Completed: 'Завершено', Cancelled: 'Скасовано' };
export function statusLabel(status: AppointmentStatus): string { return labels[status] ?? 'Невідомий статус'; }
export { kyivTime } from '../../shared/time/kyiv-time';
export function price(value: number, currency: string): string {
  return `${new Intl.NumberFormat('uk-UA', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)} ${currency === 'UAH' ? '₴' : currency}`;
}

@Injectable({ providedIn: 'root' })
export class AppointmentsApi {
  private readonly http = inject(HttpClient);
  mine(category: AppointmentCategory) {
    return this.http.get<AppointmentSummary[]>('/api/appointments/my', category === 'all' ? {} : { params: { category } });
  }
  cancel(id: string) { return this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => this.http.post<void>(`/api/appointments/${encodeURIComponent(id)}/cancel`, {}))); }
  details(id: string) { return this.http.get<AppointmentDetails>(`/api/appointments/${encodeURIComponent(id)}`); }
}
