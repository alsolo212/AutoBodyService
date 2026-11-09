import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal, viewChild } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Observable, finalize, forkJoin } from 'rxjs';
import { AuthService } from '../../../core/auth';
import { ConfirmDialog } from '../../../shared/confirm-dialog/confirm-dialog';
import { DateMode, Day, InspectionCalendarApi, SlotMode } from './inspection-calendar-api';

const todayKyiv = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
const shortTime = (value: string) => value.slice(0, 5);

@Component({
  selector: 'app-inspection-calendar',
  imports: [ReactiveFormsModule, RouterLink, ConfirmDialog],
  templateUrl: './inspection-calendar.html',
  styleUrl: './inspection-calendar.scss',
})
export class InspectionCalendar {
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly api = inject(InspectionCalendarApi);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly confirmDialog = viewChild.required(ConfirmDialog);
  readonly days: { value: Day; label: string }[] = [
    { value: 'Monday', label: 'Понеділок' }, { value: 'Tuesday', label: 'Вівторок' },
    { value: 'Wednesday', label: 'Середа' }, { value: 'Thursday', label: 'Четвер' },
    { value: 'Friday', label: 'П’ятниця' }, { value: 'Saturday', label: 'Субота' }, { value: 'Sunday', label: 'Неділя' },
  ];
  readonly tab = signal<'appointments' | 'settings'>('appointments');
  readonly loaded = signal(false); readonly displayedDate = signal('');
  readonly loading = signal(false); readonly saving = signal(false); readonly error = signal(''); readonly message = signal('');
  readonly warning = signal(''); readonly slots = signal<{ startUtc: string; endUtc: string }[]>([]);
  readonly loggingOut = signal(false); readonly logoutError = signal('');
  readonly dateForm = this.fb.group({ date: [todayKyiv(), Validators.required] });
  readonly weeklyForm = this.fb.group({ intervals: this.fb.array<ReturnType<InspectionCalendar['weeklyRow']>>([]) });
  readonly exceptionForm = this.fb.group({ mode: this.fb.control<DateMode>('Weekly'), reason: ['', Validators.maxLength(500)], intervals: this.fb.array<ReturnType<InspectionCalendar['intervalRow']>>([]), slots: this.fb.array<ReturnType<InspectionCalendar['slotRow']>>([]) });
  readonly weeklyIntervals = this.weeklyForm.controls.intervals;
  readonly dateIntervals = this.exceptionForm.controls.intervals;
  readonly slotRules = this.exceptionForm.controls.slots;
  private version = '';
  private loadedDate = '';
  private requestSerial = 0;
  private saveKind: 'weekly' | 'date' | 'reset' = 'weekly';

  constructor() { this.load(); }
  weeklyRow(dayOfWeek: Day = 'Monday', start = '', end = '') { return this.fb.group({ dayOfWeek: this.fb.control<Day>(dayOfWeek), start: [shortTime(start), Validators.required], end: [shortTime(end), Validators.required] }); }
  intervalRow(start = '', end = '') { return this.fb.group({ start: [shortTime(start), Validators.required], end: [shortTime(end), Validators.required] }); }
  slotRow(start = '', mode: SlotMode = 'Closed') { return this.fb.group({ start: [shortTime(start), Validators.required], mode: this.fb.control<SlotMode>(mode) }); }
  addWeekly(day: Day) { this.weeklyIntervals.push(this.weeklyRow(day)); this.weeklyForm.markAsDirty(); }
  removeWeekly(index: number) { this.weeklyIntervals.removeAt(index); this.weeklyForm.markAsDirty(); }
  addInterval() { this.dateIntervals.push(this.intervalRow()); this.exceptionForm.markAsDirty(); }
  addSlot() { this.slotRules.push(this.slotRow()); this.exceptionForm.markAsDirty(); }
  removeInterval(index: number) { this.dateIntervals.removeAt(index); this.exceptionForm.markAsDirty(); }
  removeSlot(index: number) { this.slotRules.removeAt(index); this.exceptionForm.markAsDirty(); }
  setTab(tab: 'appointments' | 'settings') { this.tab.set(tab); }
  load() {
    if (this.dateForm.invalid || this.saving()) return;
    const date = this.dateForm.getRawValue().date;
    const serial = ++this.requestSerial;
    this.loaded.set(false); this.version = ''; this.slots.set([]); this.warning.set('');
    this.loading.set(true); this.error.set(''); this.message.set('');
    forkJoin({ weekly: this.api.weekly(), date: this.api.date(date), slots: this.api.available(date) }).pipe(finalize(() => { if (serial === this.requestSerial) this.loading.set(false); })).subscribe({
      next: result => {
        if (serial !== this.requestSerial) return;
        if (this.dateForm.getRawValue().date !== date) { this.error.set('Обрану дату змінено. Завантажте її.'); return; }
        if (result.weekly.version !== result.date.version) { this.error.set('Графік змінився під час завантаження. Оновіть дані.'); return; }
        this.version = result.weekly.version; this.loadedDate = date; this.displayedDate.set(date); this.loaded.set(true);
        this.weeklyIntervals.clear(); result.weekly.intervals.forEach(i => this.weeklyIntervals.push(this.weeklyRow(i.dayOfWeek, i.start, i.end)));
        const exception = result.date.exception;
        this.dateIntervals.clear(); this.slotRules.clear();
        exception?.intervals.forEach(i => this.dateIntervals.push(this.intervalRow(i.start, i.end)));
        exception?.slots.forEach(s => this.slotRules.push(this.slotRow(s.start, s.mode)));
        this.exceptionForm.patchValue({ mode: exception?.mode ?? 'Weekly', reason: exception?.reason ?? '' });
        this.weeklyForm.markAsPristine(); this.exceptionForm.markAsPristine();
        this.warning.set(result.date.bookingConflictsChecked ? '' : result.date.warning);
        this.slots.set(result.slots.slots);
      },
      error: () => { if (serial === this.requestSerial) this.error.set('Не вдалося завантажити календар. Перевірте з’єднання та спробуйте ще раз.'); },
    });
  }
  requestSave(kind: 'weekly' | 'date' | 'reset') {
    if (this.loading() || this.saving() || !this.version) return;
    this.error.set('');
    if (this.dateForm.getRawValue().date !== this.loadedDate) { this.error.set('Спочатку завантажте обрану дату.'); return; }
    if (kind === 'weekly' && this.weeklyForm.invalid || kind === 'date' && (this.exceptionForm.controls.reason.invalid || (this.exceptionForm.controls.mode.value === 'Replace' && this.dateIntervals.invalid) || (this.exceptionForm.controls.mode.value !== 'Closed' && this.slotRules.invalid))) { this.error.set('Заповніть усі поля інтервалів.'); return; }
    if (kind === 'date' && this.exceptionForm.controls.mode.value === 'Replace' && this.dateIntervals.length === 0) { this.error.set('Додайте хоча б один інтервал для цієї дати.'); return; }
    this.saveKind = kind; this.confirmDialog().open();
  }
  save() {
    if (this.saving() || this.loading()) return;
    const date = this.dateForm.getRawValue().date;
    const value = this.exceptionForm.getRawValue();
    const exception = this.saveKind === 'reset' ? null : { mode: value.mode, reason: value.reason.trim() || null, intervals: value.mode === 'Replace' ? value.intervals : [], slots: value.mode === 'Closed' ? [] : value.slots };
    const operation: Observable<unknown> = this.saveKind === 'weekly' ? this.api.saveWeekly(this.version, this.weeklyForm.getRawValue().intervals) : this.api.saveDate(this.version, date, exception);
    this.saving.set(true); this.error.set('');
    operation.pipe(finalize(() => this.saving.set(false))).subscribe({
      next: () => { this.saving.set(false); this.load(); this.message.set('Зміни збережено. Перевірте наявні записи та зв’яжіться з клієнтами за потреби.'); },
      error: (error: unknown) => this.error.set(error instanceof HttpErrorResponse && error.status === 409 ? 'Графік змінився. Оновіть дані та повторіть спробу.' : error instanceof HttpErrorResponse && error.status === 400 ? 'Некоректні або перехресні інтервали. Перевірте час і тривалість.' : 'Не вдалося зберегти графік. Перевірте з’єднання.'),
    });
  }
  formatTime(value: string) { return new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit', timeZoneName: 'shortOffset' }).format(new Date(value)); }
  logout() {
    if (this.loggingOut()) return;
    this.loggingOut.set(true); this.logoutError.set('');
    this.auth.logout().pipe(finalize(() => this.loggingOut.set(false))).subscribe({ next: () => void this.router.navigateByUrl('/login'), error: () => this.logoutError.set('Не вдалося вийти. Перевірте з’єднання.') });
  }
}
