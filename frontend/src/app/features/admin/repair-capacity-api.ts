import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export interface RepairCapacityOverview { workplaces: number | null; inProgress: number; days: { date: string; reserved: number }[]; }
export interface RepairPreview { today: string; latestStart: string; nearestStart: string | null; finish: string; fits: boolean; workplaces: number | null; inProgress: number; }

@Injectable({ providedIn: 'root' })
export class RepairCapacityApi {
  private readonly http = inject(HttpClient);
  overview(date: string) { return this.http.get<RepairCapacityOverview>('/api/admin/repair-capacity', { params: { date } }); }
  preview(start: string, durationDays: number) { return this.http.get<RepairPreview>('/api/admin/repair-capacity/preview', { params: { start, durationDays } }); }
  save(workplaces: number) { return this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => this.http.put<void>('/api/admin/repair-capacity', { workplaces }))); }
}
