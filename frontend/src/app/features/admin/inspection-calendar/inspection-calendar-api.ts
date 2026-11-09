import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';

export type Day = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';
export type DateMode = 'Weekly' | 'Closed' | 'Replace';
export type SlotMode = 'Open' | 'Closed';
export interface Interval { start: string; end: string; }
export interface WeeklyInterval extends Interval { dayOfWeek: Day; }
export interface WeeklySchedule { version: string; timeZone: string; intervals: WeeklyInterval[]; }
export interface SlotRule { start: string; mode: SlotMode; }
export interface DateException { date: string; mode: DateMode; reason: string | null; intervals: Interval[]; slots: SlotRule[]; }
export interface DateSchedule { version: string; date: string; exception: DateException | null; bookingConflictsChecked: boolean; warning: string; }
export interface AvailableSlots { date: string; slots: { startUtc: string; endUtc: string }[]; }
export interface UpdateResult { bookingConflictsChecked: boolean; warning: string; }

@Injectable({ providedIn: 'root' })
export class InspectionCalendarApi {
  private readonly http = inject(HttpClient);
  weekly() { return this.http.get<WeeklySchedule>('/api/admin/inspection-schedule'); }
  date(date: string) { return this.http.get<DateSchedule>('/api/admin/inspection-exceptions', { params: new HttpParams().set('date', date) }); }
  available(date: string) { return this.http.get<AvailableSlots>('/api/inspection-slots', { params: new HttpParams().set('date', date) }); }
  saveWeekly(version: string, intervals: WeeklyInterval[]) {
    return this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => this.http.put<void>('/api/admin/inspection-schedule', { version, intervals })));
  }
  saveDate(version: string, date: string, exception: Omit<DateException, 'date'> | null) {
    return this.http.get<void>('/api/auth/antiforgery').pipe(switchMap(() => this.http.put<UpdateResult>('/api/admin/inspection-exceptions', { version, date, exception })));
  }
}
