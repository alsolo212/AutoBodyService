import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AccountHeader } from '../../appointments/account-shell/account-header';
import { problemDescriptionValidator } from './problem-description-validator';
import { kyivTime } from '../../../shared/time/kyiv-time';
import { Subject, catchError, finalize, map, merge, of, startWith, switchMap, tap } from 'rxjs';

interface Slot { startUtc: string; endUtc: string; }
interface SlotsResponse { slots: Slot[]; }
interface CreatedAppointment { id: string; inspectionStartUtc: string; status: string; }
const nonBlank = (control: AbstractControl): ValidationErrors | null =>
  typeof control.value === 'string' && control.value.trim() ? null : { required: true };
const kyivDate = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
export const formatInspection = kyivTime;

@Component({ selector: 'app-booking', imports: [ReactiveFormsModule, AccountHeader], templateUrl: './booking.html', styleUrl: './booking.scss' })
export class Booking {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly fb = inject(FormBuilder).nonNullable;
  readonly today = kyivDate();
  readonly form = this.fb.group({
    date: [this.today, [Validators.required]],
    slot: ['', [Validators.required]],
    vin: ['', [nonBlank, Validators.pattern(/^[A-HJ-NPR-Z0-9]{17}$/)]],
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
  readonly format = formatInspection;
  private readonly refreshRequested = new Subject<void>();

  constructor() {
    merge(this.form.controls.date.valueChanges.pipe(startWith(this.today)), this.refreshRequested.pipe(map(() => this.form.controls.date.value))).pipe(tap(() => this.form.controls.slot.setValue('')),
      switchMap(date => this.fetchSlots(date)), takeUntilDestroyed(this.destroyRef)).subscribe();
  }
  private fetchSlots(date: string) {
    this.slots.set([]); this.loadError.set(false);
    if (!date || date < this.today) { this.loading.set(false); return of(null); }
    this.loading.set(true);
    return this.http.get<SlotsResponse>('/api/inspection-slots', { params: { date } }).pipe(
      tap(result => { this.slots.set(result.slots.filter(slot => new Date(slot.startUtc).getTime() > Date.now())); this.loading.set(false); }),
      catchError(() => { this.loadError.set(true); this.loading.set(false); return of(null); }),
    );
  }
  refresh(): void {
    this.refreshRequested.next();
  }
  next(): void {
    const fields = this.step() === 1 ? ['date', 'slot'] as const : this.step() === 2 ? ['vin', 'make', 'model'] as const : ['problem'] as const;
    fields.forEach(name => this.form.controls[name].markAsTouched());
    if (fields.some(name => this.form.controls[name].invalid) || (this.step() === 1 && (this.loading() || !this.slots().some(s => s.startUtc === this.form.controls.slot.value && new Date(s.startUtc).getTime() > Date.now())))) return;
    this.step.update(step => step + 1);
  }
  submit(): void {
    if (this.submitting() || this.form.invalid) return;
    const { slot, vin, make, model, problem } = this.form.getRawValue();
    this.submitting.set(true); this.submitError.set('');
    this.http.get<void>('/api/auth/antiforgery').pipe(
      switchMap(() => this.http.post<CreatedAppointment>('/api/appointments', { inspectionStartUtc: slot, vin: vin.trim(), make: make.trim(), model: model.trim(), problemDescription: problem.trim() })),
      finalize(() => this.submitting.set(false)), takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: result => void this.router.navigate(['/account/book/success'], { state: { inspectionStartUtc: result.inspectionStartUtc } }),
      error: (error: HttpErrorResponse) => {
        if (error.status === 409) { this.conflict.set(true); this.step.set(1); this.refresh(); }
        else this.submitError.set(error.status === 400 ? 'Перевірте введені дані та спробуйте ще раз.' : 'Не вдалося створити запис. Перевірте з’єднання та спробуйте ще раз.');
      },
    });
  }
}
