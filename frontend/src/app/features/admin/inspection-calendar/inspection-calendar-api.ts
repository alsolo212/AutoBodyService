import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { switchMap } from 'rxjs';
import { AppointmentStatus } from '../../appointments/appointments-api';

export type Day = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';
export type DateMode = 'Weekly' | 'Closed' | 'Replace';
export type SlotMode = 'Open' | 'Closed';
export interface Interval { start: string; end: string; }
export interface WeeklyInterval extends Interval { dayOfWeek: Day; }
export interface WeeklySchedule { version: string; timeZone: string; intervals: WeeklyInterval[]; }
export interface SlotRule { start: string; mode: SlotMode; }
export interface DateException { date: string; mode: DateMode; reason: string | null; intervals: Interval[]; slots: SlotRule[]; }
export interface DateSchedule { version: string; date: string; exception: DateException | null; }
export interface AvailableSlots { date: string; slots: { startUtc: string; endUtc: string }[]; }
export interface CalendarAppointment { id: string; make: string; model: string; customerName: string; inspectionStartUtc: string; status: AppointmentStatus; }
export interface SchedulePreview { confirmationToken: string; conflicts: CalendarAppointment[]; }
export type DateChange = { version: string; date: string; exception: Omit<DateException, 'date'> | null; confirmationToken?: string };
export type WeeklyChange = { version: string; intervals: WeeklyInterval[]; confirmationToken?: string };

@Injectable({ providedIn: 'root' })
export class InspectionCalendarApi {
  private readonly http = inject(HttpClient);
  weekly() { return this.http.get<WeeklySchedule>('/api/admin/inspection-schedule'); }
  date(date: string) { return this.http.get<DateSchedule>('/api/admin/inspection-exceptions', { params: new HttpParams().set('date', date) }); }
  available(date: string) { return this.http.get<AvailableSlots>('/api/inspection-slots', { params: new HttpParams().set('date', date) }); }
  appointments(date: string) { return this.http.get<CalendarAppointment[]>('/api/admin/inspection-calendar', { params: new HttpParams().set('date', date) }); }
  previewWeekly(change: WeeklyChange) { return this.token().pipe(switchMap(() => this.http.post<SchedulePreview>('/api/admin/inspection-schedule/preview', change))); }
  previewDate(change: DateChange) { return this.token().pipe(switchMap(() => this.http.post<SchedulePreview>('/api/admin/inspection-exceptions/preview', change))); }
  saveWeekly(change: WeeklyChange) { return this.token().pipe(switchMap(() => this.http.put<void>('/api/admin/inspection-schedule', change))); }
  saveDate(change: DateChange) { return this.token().pipe(switchMap(() => this.http.put<SchedulePreview>('/api/admin/inspection-exceptions', change))); }
  private token() { return this.http.get<void>('/api/auth/antiforgery'); }
}
