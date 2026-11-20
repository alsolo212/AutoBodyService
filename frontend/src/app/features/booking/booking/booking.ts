import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, ElementRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NgTemplateOutlet } from '@angular/common';
import { AdminShell } from '../../../layout/admin-shell/admin-shell';
import { phoneValidator } from '../../auth/auth-validation';
import { AccountHeader } from '../../appointments/account-shell/account-header';
import { problemDescriptionValidator } from './problem-description-validator';
import { kyivTime } from '../../../shared/time/kyiv-time';
import { AvailabilityCalendar } from '../availability-calendar/availability-calendar';
import { Subject, catchError, finalize, map, merge, of, startWith, switchMap, tap } from 'rxjs';

interface Slot { startUtc: string; endUtc: string; }
interface SlotsResponse { slots: Slot[]; }
interface CreatedAppointment { id: string; inspectionStartUtc: string; status: string; }
const nonBlank = (control: AbstractControl): ValidationErrors | null =>
  typeof control.value === 'string' && control.value.trim() ? null : { required: true };
const kyivDate = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
export const formatInspection = kyivTime;

@Component({ selector: 'app-booking', imports: [ReactiveFormsModule, AccountHeader, AdminShell, AvailabilityCalendar, NgTemplateOutlet, RouterLink], templateUrl: './booking.html', styleUrl: './booking.scss' })
export class Booking {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly element = inject(ElementRef<HTMLElement>);
  private readonly fb = inject(FormBuilder).nonNullable;
  readonly guestMode = inject(ActivatedRoute).snapshot.data['guestBooking'] === true;
  readonly today = kyivDate();
  readonly maxDate = (() => { const [year, month] = this.today.split('-').map(Number); return new Date(Date.UTC(year, month + 1, 0)).toISOString().slice(0, 10); })();
  readonly form = this.fb.group({
    date: ['', [Validators.required, control => control.value && (control.value < this.today || control.value > this.maxDate) ? { range: true } : null]],
    slot: ['', [Validators.required]],
    vin: ['', [Validators.pattern(/^[A-HJ-NPR-Z0-9]{17}$/)]],
    customerName: ['', this.guestMode ? [nonBlank, Validators.maxLength(200), control => /\p{Cc}/u.test(String(control.value ?? '')) ? { invalidText: true } : null] : []],
    customerPhone: ['', this.guestMode ? [Validators.required, phoneValidator] : []],
    make: ['', [nonBlank, Validators.maxLength(200)]],
    model: ['', [nonBlank, Validators.maxLength(200)]],
    problem: ['', [problemDescriptionValidator, Validators.maxLength(5000)]],
  });
  readonly step = signal(1);
  readonly slots = signal<Slot[]>([]);
  readonly loading = signal(false);
  readonly loadError = signal(false);
  readonly submitError = signal('');
  readonly submitting = signal(false);
  readonly conflict = signal(false);
  readonly guestCreated = signal<string | null>(null);
  readonly calendarRevision = signal(0);
  readonly format = formatInspection;
  private readonly refreshRequested = new Subject<void>();

  constructor() {
    merge(this.form.controls.date.valueChanges.pipe(startWith('')), this.refreshRequested.pipe(map(() => this.form.controls.date.value))).pipe(tap(() => this.form.controls.slot.setValue('')),
      switchMap(date => this.fetchSlots(date)), takeUntilDestroyed(this.destroyRef)).subscribe();
  }
  private fetchSlots(date: string) {
    this.slots.set([]); this.loadError.set(false);
    if (!date || date < this.today || date > this.maxDate) { this.loading.set(false); return of(null); }
    this.loading.set(true);
    return this.http.get<SlotsResponse>('/api/inspection-slots', { params: { date } }).pipe(
      tap(result => { this.slots.set(result.slots.filter(slot => new Date(slot.startUtc).getTime() > Date.now())); this.loading.set(false); }),
      catchError(() => { this.loadError.set(true); this.loading.set(false); return of(null); }),
    );
  }
  refresh(): void {
    this.refreshRequested.next();
  }
  hasCurrentSlot(): boolean { return this.slots().some(s => s.startUtc === this.form.controls.slot.value && new Date(s.startUtc).getTime() > Date.now()); }
  next(): void {
    const fields = this.step() === 1 ? (this.guestMode ? ['customerName', 'customerPhone', 'date', 'slot'] as const : ['date', 'slot'] as const) : this.step() === 2 ? ['vin', 'make', 'model'] as const : ['problem'] as const;
    fields.forEach(name => this.form.controls[name].markAsTouched());
    const invalid = fields.find(name => this.form.controls[name].invalid);
    if (invalid || (this.step() === 1 && (this.loading() || !this.hasCurrentSlot()))) {
      this.focus(invalid === 'slot' ? '[data-slot-error]' : invalid ? `[formcontrolname="${invalid}"]` : '[data-slot-error]'); return;
    }
    this.step.update(step => step + 1);
    this.focus('[data-step-heading]');
  }
  back(): void { this.step.update(step => step - 1); this.focus('[data-step-heading]'); }
  private focus(selector: string): void { setTimeout(() => (this.element.nativeElement as HTMLElement).querySelector<HTMLElement>(selector)?.focus()); }
  submit(): void {
    if (this.submitting() || this.form.invalid) return;
    const { slot, vin, make, model, problem, customerName, customerPhone } = this.form.getRawValue();
    this.submitting.set(true); this.submitError.set('');
    this.http.get<void>('/api/auth/antiforgery').pipe(
      switchMap(() => this.http.post<CreatedAppointment>(this.guestMode ? '/api/admin/appointments/guest' : '/api/appointments', { inspectionStartUtc: slot, vin: vin.trim() || null, make: make.trim(), model: model.trim(), problemDescription: problem.trim(), ...(this.guestMode ? { customerName: customerName.trim(), customerPhone } : {}) })),
      finalize(() => this.submitting.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: result => { if (this.guestMode) { this.guestCreated.set(result.id); this.focus('[data-success-heading]'); } else void this.router.navigate(['/account/book/success'], { state: { inspectionStartUtc: result.inspectionStartUtc } }); },
      error: (error: HttpErrorResponse) => {
        if (error.status === 409) { this.conflict.set(true); this.step.set(1); this.calendarRevision.update(value => value + 1); this.refresh(); this.focus('[data-step-heading]'); }
        else { this.submitError.set(error.status === 429 ? 'За сьогодні вже створено 10 заявок. Спробуйте завтра за київським часом.' : error.status === 400 ? 'Перевірте введені дані та спробуйте ще раз.' : 'Не вдалося створити запис. Перевірте з’єднання та спробуйте ще раз.'); this.focus('[data-submit-error]'); }
      },
    });
  }
}
