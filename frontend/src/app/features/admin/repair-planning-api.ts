import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export interface PlanningOrder { id: string; customerName: string; customerPhone: string; make: string; model: string; start: string; finish: string; alternativeStart: string | null; version: number; }
export interface PlanningOverview { conflicts: PlanningOrder[]; earlyOpportunity: PlanningOrder | null; }
export interface PauseOffer { pauseId: string; expectedReturn: string; candidate: PlanningOrder | null; }
export interface DelayPreview { appointmentId: string; finish: string; confirmationToken: string; conflicts: PlanningOrder[]; }

@Injectable({ providedIn: 'root' })
export class RepairPlanningApi {
  private readonly http = inject(HttpClient);
  overview() { return this.http.get<PlanningOverview>('/api/admin/repair-planning'); }
  pauseOffer(id: string) { return this.http.get<PauseOffer>(`/api/admin/repair-planning/pause/${encodeURIComponent(id)}/offer`); }
  decline(id: string, candidateId: string) { return this.token().pipe(switchMap(() => this.http.post<void>(`/api/admin/repair-planning/pause/${encodeURIComponent(id)}/decline`, { candidateId }))); }
  accept(candidate: PlanningOrder, pauseId: string | null) { return this.token().pipe(switchMap(() => this.http.post<void>(`/api/admin/repair-planning/${encodeURIComponent(candidate.id)}/accept-earlier`, { version: candidate.version, pauseId, customerAgreed: true }))); }
  delayPreview(id: string, finish: string) { return this.http.get<DelayPreview>(`/api/admin/repair-planning/${encodeURIComponent(id)}/delay-preview`, { params: { finish } }); }
  delay(id: string, version: number, preview: DelayPreview) { return this.token().pipe(switchMap(() => this.http.post<void>(`/api/admin/repair-planning/${encodeURIComponent(id)}/delay`, { version, finish: preview.finish, confirmationToken: preview.confirmationToken, confirmed: true }))); }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
